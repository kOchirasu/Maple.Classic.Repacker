using Maple.Classic.File.IO;
using Maple.Classic.Repacker.Engine.Audio;
using Maple.Classic.Repacker.Engine.Canvas;
using Maple.Classic.Repacker.Engine.Models;
using Maple.Classic.Repacker.Engine.Serialization;
using Maple.Classic.Repacker.Engine.Workspace;
using Xunit;

namespace Maple.Classic.Repacker.Tests;

public sealed class RepackerEngineTests {
    [Fact]
    public void PngCodec_RoundTrips_Rgba8888() {
        int width = 12;
        int height = 10;
        byte[] rgba = new byte[width * height * 4];
        for (int i = 0; i < rgba.Length; i += 4) {
            rgba[i] = (byte) ((i * 7) & 0xFF);
            rgba[i + 1] = (byte) ((i * 13) & 0xFF);
            rgba[i + 2] = (byte) ((i * 23) & 0xFF);
            rgba[i + 3] = (byte) (i % 8 == 0 ? 0 : 255);
        }

        byte[] png = PngCodec.EncodeRgba(width, height, rgba);
        Assert.NotEmpty(png);

        (int decodedW, int decodedH, byte[] decodedRgba) = PngCodec.DecodeRgba(png);
        Assert.Equal(width, decodedW);
        Assert.Equal(height, decodedH);
        Assert.Equal(rgba, decodedRgba);
    }

    [Theory]
    [InlineData(WzCanvasFormat.Bgra4444, 8, 8)]
    [InlineData(WzCanvasFormat.Bgra8888, 8, 8)]
    [InlineData(WzCanvasFormat.Bgr565, 8, 8)]
    [InlineData(WzCanvasFormat.Bgr565Block16, 32, 32)]
    [InlineData(WzCanvasFormat.Dxt3, 8, 8)]
    [InlineData(WzCanvasFormat.Dxt5, 8, 8)]
    public void WzCanvasCodec_EncodesAndDecodes_AllFormats(WzCanvasFormat format, int width, int height) {
        byte[] rgba = new byte[width * height * 4];
        for (int y = 0; y < height; y++) {
            for (int x = 0; x < width; x++) {
                int idx = (y * width + x) * 4;
                rgba[idx] = 255;
                rgba[idx + 1] = 170;
                rgba[idx + 2] = 0;
                rgba[idx + 3] = 255;
            }
        }

        byte[] compressed = WzCanvasCodec.EncodeFromRgba(width, height, (int) format, rgba);
        Assert.NotEmpty(compressed);

        var canvasVal = new WzValue.Canvas(width, height, (int) format, compressed);
        byte[] decoded = WzCanvasCodec.DecodeToRgba(canvasVal);
        Assert.Equal(width * height * 4, decoded.Length);

        // Verify non-transparent warm orange pixels survived format quantization
        Assert.True(decoded[0] > 200);
        Assert.True(decoded[1] > 120);
        Assert.Equal(255, decoded[3]);
    }

    [Fact]
    public void WzSoundCodec_SynthesizesAndExtracts_PlayableWav() {
        WzValue.Sound sound = WzSoundCodec.CreateSynthesizedChimeSound(523, 659, 120);
        ExtractedSoundData extracted = WzSoundCodec.ExtractAudioStream(sound);

        Assert.Equal("audio/wav", extracted.MimeType);
        Assert.Equal("wav", extracted.Extension);
        Assert.Contains("PCM WAV", extracted.FormatName);
        Assert.Equal((byte) 'R', extracted.AudioBytes[0]);
        Assert.Equal((byte) 'I', extracted.AudioBytes[1]);
        Assert.Equal((byte) 'F', extracted.AudioBytes[2]);
        Assert.Equal((byte) 'F', extracted.AudioBytes[3]);

        WzValue.Sound reimported = WzSoundCodec.CreateFromAudioBytes(extracted.AudioBytes);
        Assert.True(reimported.DataLength > 0);
        Assert.True(reimported.Header.Length >= 71);
    }

    [Fact]
    public void JsonWzCodec_RoundTrips_Properties() {
        var rootObj = new WzValue.Object([
            new WzProperty("name", "Blue Starter Coat"),
            new WzProperty("reqLevel", new WzValue.Short(10)),
            new WzProperty("origin", (14, 22)),
            new WzProperty("info", new WzValue.Object([
                new WzProperty("islot", "Ma"),
                new WzProperty("reqSTR", new WzValue.Int(15)),
            ])),
        ]);

        string json = rootObj.ToJson(2);
        WzValue roundTripped = JsonWzCodec.Parse(json, rootObj);

        Assert.Equal("Blue Starter Coat", (string) roundTripped["name"]!);
        Assert.IsType<WzValue.Short>(roundTripped["reqLevel"]);
        Assert.Equal(10, (int) roundTripped["reqLevel"]!);
        Assert.Equal((14, 22), ((int, int)) roundTripped["origin"]!);
        Assert.Equal("Ma", (string) roundTripped["info/islot"]!);
    }

    [Fact]
    public void RepackerWorkspace_SupportsFullInspectionCrudSearchAndRepack() {
        using var workspace = new RepackerWorkspace();
        ArchiveSummaryDto[] loaded = workspace.LoadSampleWorkspace();
        Assert.Equal(5, loaded.Length);

        string charArchiveId = loaded.First(a => a.Name == "Character_000.wz").ArchiveId;

        // 1. Expand tree down to Coat/01040000.img/stand1/0/mail
        TreeNodeDto[] rootChildren = workspace.GetTreeChildren(charArchiveId, "");
        Assert.Contains(rootChildren, n => n.Name == "Coat" && n.NodeKind == RepackerNodeKind.Directory);

        TreeNodeDto[] coatChildren = workspace.GetTreeChildren(charArchiveId, "Coat");
        Assert.Contains(coatChildren, n => n.Name == "01040000.img" && n.NodeKind == RepackerNodeKind.Image);

        // 2. Inspect Canvas node with _outlink resolution across Character_Canvas_000.wz
        NodeInspectionDto outlinkNode = workspace.InspectNode(charArchiveId, "PetEquip/01802653.img/5002828/stand0/0");
        Assert.True(outlinkNode.Ok, outlinkNode.Error);
        Assert.Equal(RepackerPropertyKind.Canvas, outlinkNode.PropertyKind);
        Assert.NotNull(outlinkNode.Canvas);
        Assert.NotNull(outlinkNode.Canvas.Outlink);
        Assert.Contains("Character_Canvas_000.wz", outlinkNode.Canvas.ResolvedLinkPath);
        Assert.NotEmpty(outlinkNode.Canvas.PngBytes);

        // 3. Inspect animation node (stand1)
        NodeInspectionDto stand1Node = workspace.InspectNode(charArchiveId, "Coat/01040000.img/stand1");
        Assert.NotEmpty(stand1Node.AnimationFrames);
        Assert.Equal(3, stand1Node.AnimationFrames.Length);

        // 4. Inline external _outlink pixel data into PetEquip/01802653.img/5002828/stand0/0
        (bool inlineOk, string? inlineErr, int inlinedCount) = workspace.InlineOutlinks(
            charArchiveId,
            "PetEquip/01802653.img/5002828/stand0/0");
        Assert.True(inlineOk, inlineErr);
        Assert.Equal(1, inlinedCount);

        NodeInspectionDto afterInline = workspace.InspectNode(charArchiveId, "PetEquip/01802653.img/5002828/stand0/0");
        Assert.NotNull(afterInline.Canvas);
        Assert.Null(afterInline.Canvas.Outlink);
        Assert.Equal(36, afterInline.Canvas.Width);
        Assert.Equal(36, afterInline.Canvas.Height);

        // 5. Update primitive property & verify Undo / Redo
        (bool updateOk, string? updateErr) = workspace.SetPropertyValue(
            charArchiveId,
            "Coat/01040000.img/info/reqLevel",
            RepackerPropertyKind.Short,
            "25",
            null,
            null);
        Assert.True(updateOk, updateErr);

        NodeInspectionDto updatedLevel = workspace.InspectNode(charArchiveId, "Coat/01040000.img/info/reqLevel");
        Assert.Equal("25", updatedLevel.DisplayValue);
        Assert.True(updatedLevel.CanUndo);

        (bool undoOk, string? undoErr) = workspace.Undo(charArchiveId, "Coat/01040000.img/info/reqLevel");
        Assert.True(undoOk, undoErr);
        Assert.Equal("10", workspace.InspectNode(charArchiveId, "Coat/01040000.img/info/reqLevel").DisplayValue);

        (bool redoOk, string? redoErr) = workspace.Redo(charArchiveId, "Coat/01040000.img/info/reqLevel");
        Assert.True(redoOk, redoErr);
        Assert.Equal("25", workspace.InspectNode(charArchiveId, "Coat/01040000.img/info/reqLevel").DisplayValue);

        // 6. Add, rename, duplicate, copy/paste, and delete property
        (bool addOk, string? addErr, string addedPath) = workspace.AddProperty(
            charArchiveId,
            "Coat/01040000.img/info",
            "customTag",
            RepackerPropertyKind.String,
            "Legend");
        Assert.True(addOk, addErr);
        Assert.Equal("Coat/01040000.img/info/customTag", addedPath);

        (bool renOk, string? renErr, string newPath) = workspace.RenameNode(
            charArchiveId,
            "Coat/01040000.img/info/customTag",
            "renamedTag");
        Assert.True(renOk, renErr);
        Assert.Equal("Coat/01040000.img/info/renamedTag", newPath);

        (bool copyOk, string? copyErr, _) = workspace.CopyNode(charArchiveId, "Coat/01040000.img/info/renamedTag");
        Assert.True(copyOk, copyErr);

        (bool pasteOk, string? pasteErr, string pastedPath) = workspace.PasteNode(charArchiveId, "Coat/01040000.img/info");
        Assert.True(pasteOk, pasteErr);
        Assert.NotEmpty(pastedPath);

        (bool delOk, string? delErr, _) = workspace.DeleteNode(charArchiveId, pastedPath);
        Assert.True(delOk, delErr);

        // 7. Global search across archives
        SearchResultItemDto[] hits = workspace.Search(new SearchQueryDto {
            Query = "Snail",
            MaxResults = 50,
        });
        Assert.NotEmpty(hits);

        // 8. Export repacked .wz archive bytes and verify WzArchive round-trip
        (bool expOk, string? expErr, string wzFileName, byte[] wzBytes) = workspace.ExportArchiveBytes(charArchiveId);
        Assert.True(expOk, expErr);
        Assert.Equal("Character_000.wz", wzFileName);
        using WzArchive repacked = WzArchive.FromBytes(wzBytes, "Character_000");
        WzValue coatImg = repacked.ParseImage("Coat/01040000.img");
        Assert.NotNull(coatImg["info"]);
    }
}
