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
    public void WzSoundCodec_ImportsAndExtracts_PlayableWav() {
        byte[] wav = new byte[44 + 4410];
        "RIFF"u8.CopyTo(wav.AsSpan(0, 4));
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(4, 4), wav.Length - 8);
        "WAVE"u8.CopyTo(wav.AsSpan(8, 4));
        "fmt "u8.CopyTo(wav.AsSpan(12, 4));
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(16, 4), 16);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(20, 2), 1); // PCM
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(22, 2), 1); // 1 channel
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(24, 4), 22050); // sampleRate
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(28, 4), 44100); // byteRate
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(32, 2), 2); // blockAlign
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(34, 2), 16); // bitsPerSample
        "data"u8.CopyTo(wav.AsSpan(36, 4));
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(40, 4), 4410);

        WzValue.Sound sound = WzSoundCodec.CreateFromAudioBytes(wav);
        ExtractedSoundData extracted = WzSoundCodec.ExtractAudioStream(sound);

        Assert.Equal("audio/wav", extracted.MimeType);
        Assert.Equal("wav", extracted.Extension);
        Assert.Contains("WAV", extracted.FormatName);
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

        byte[] sprite36 = new byte[36 * 36 * 4];
        Array.Fill(sprite36, (byte) 200);
        byte[] encoded36 = WzCanvasCodec.EncodeFromRgba(36, 36, (int) WzCanvasFormat.Bgra8888, sprite36);
        byte[] dummy1x1 = WzCanvasCodec.EncodeFromRgba(1, 1, (int) WzCanvasFormat.Bgra4444, [0, 0, 0, 0]);

        byte[] charBytes;
        using (var charWz = new WzArchive()) {
            var coatImg = new WzValue.Object();
            coatImg["info/islot"] = "Ma";
            coatImg["info/reqLevel"] = new WzValue.Short(10);
            for (int f = 0; f < 3; f++) {
                coatImg[$"stand1/{f}/delay"] = 500;
                coatImg[$"stand1/{f}/mail"] = new WzValue.Canvas(
                    36,
                    36,
                    (int) WzCanvasFormat.Bgra8888,
                    encoded36,
                    [new WzProperty("origin", (18, 18))]);
            }
            charWz.SetImage("Coat/01040000.img", coatImg);

            var petImg = new WzValue.Object();
            petImg["5002828/stand0/0"] = new WzValue.Canvas(
                1,
                1,
                (int) WzCanvasFormat.Bgra4444,
                dummy1x1,
                [
                    new WzProperty("origin", (18, 32)),
                    new WzProperty("_outlink", "Character/PetEquip/_Canvas/01802653.img/5002828/stand0/0"),
                ]);
            charWz.SetImage("PetEquip/01802653.img", petImg);
            charBytes = charWz.ToBytes();
        }

        byte[] charCanvasBytes;
        using (var canvasWz = new WzArchive()) {
            var petCanvasImg = new WzValue.Object();
            petCanvasImg["5002828/stand0/0"] = new WzValue.Canvas(
                36,
                36,
                (int) WzCanvasFormat.Bgra8888,
                encoded36,
                [new WzProperty("origin", (18, 32))]);
            canvasWz.SetImage("PetEquip/_Canvas/01802653.img", petCanvasImg);
            charCanvasBytes = canvasWz.ToBytes();
        }

        byte[] stringBytes;
        using (var strWz = new WzArchive()) {
            var mobImg = new WzValue.Object();
            mobImg["100100/name"] = "Snail";
            strWz.SetImage("Mob.img", mobImg);
            stringBytes = strWz.ToBytes();
        }

        ArchiveSummaryDto charSummary = workspace.OpenArchiveFromBytes("Character_000.wz", charBytes);
        workspace.OpenArchiveFromBytes("Character_Canvas_000.wz", charCanvasBytes);
        workspace.OpenArchiveFromBytes("String_000.wz", stringBytes);

        string charArchiveId = charSummary.ArchiveId;

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
        Assert.Equal(0, updatedLevel.ChildCount);
        Assert.Contains(updatedLevel.Children, c => c.Name == "islot" && c.Path == "Coat/01040000.img/info/islot");
        Assert.Contains(updatedLevel.Children, c => c.Name == "reqLevel" && c.Path == "Coat/01040000.img/info/reqLevel");

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

        TreeNodeDto[] infoChildren = workspace.GetTreeChildren(charArchiveId, "Coat/01040000.img/info");
        TreeNodeDto islotChild = Assert.Single(infoChildren, n => n.Name == "islot");
        Assert.Equal(RepackerChangeState.Unchanged, islotChild.ChangeState);

        TreeNodeDto reqLevelChild = Assert.Single(infoChildren, n => n.Name == "reqLevel");
        Assert.Equal(RepackerChangeState.Modified, reqLevelChild.ChangeState);

        TreeNodeDto customTagChild = Assert.Single(infoChildren, n => n.Name == "customTag");
        Assert.Equal(RepackerChangeState.Added, customTagChild.ChangeState);

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

        TreeNodeDto[] infoAfterDel = workspace.GetTreeChildren(charArchiveId, "Coat/01040000.img/info");
        TreeNodeDto deletedTreeEntry = Assert.Single(infoAfterDel, n => n.Path == pastedPath);
        Assert.Equal(RepackerChangeState.Deleted, deletedTreeEntry.ChangeState);

        NodeInspectionDto deletedInspect = workspace.InspectNode(charArchiveId, pastedPath);
        Assert.True(deletedInspect.Ok, deletedInspect.Error);
        Assert.Equal(RepackerChangeState.Deleted, deletedInspect.ChangeState);
        Assert.Contains(deletedInspect.Children, c => c.Path == pastedPath && c.ChangeState == RepackerChangeState.Deleted);

        (bool restoreOk, string? restoreErr, string restoredPath) = workspace.RestoreNode(charArchiveId, pastedPath);
        Assert.True(restoreOk, restoreErr);
        Assert.Equal(pastedPath, restoredPath);

        TreeNodeDto[] infoAfterRestore = workspace.GetTreeChildren(charArchiveId, "Coat/01040000.img/info");
        TreeNodeDto restoredTreeEntry = Assert.Single(infoAfterRestore, n => n.Path == pastedPath);
        Assert.NotEqual(RepackerChangeState.Deleted, restoredTreeEntry.ChangeState);

        (bool delAgainOk, string? delAgainErr, _) = workspace.DeleteNode(charArchiveId, pastedPath);
        Assert.True(delAgainOk, delAgainErr);

        // 6b. Delete an .img node and verify CanUndo / Undo / Redo restores and re-deletes the .img node
        (bool delImgOk, string? delImgErr, _) = workspace.DeleteNode(charArchiveId, "Coat/01040000.img");
        Assert.True(delImgOk, delImgErr);

        NodeInspectionDto deletedImgInspect = workspace.InspectNode(charArchiveId, "Coat/01040000.img");
        Assert.True(deletedImgInspect.Ok, deletedImgInspect.Error);
        Assert.Equal(RepackerChangeState.Deleted, deletedImgInspect.ChangeState);
        Assert.True(deletedImgInspect.CanUndo);

        // Switching to another node or archive root keeps Undo available in the archive undo queue
        NodeInspectionDto otherNodeInspect = workspace.InspectNode(charArchiveId, "PetEquip/01802653.img");
        Assert.True(otherNodeInspect.CanUndo);
        NodeInspectionDto rootInspect = workspace.InspectNode(charArchiveId, "");
        Assert.True(rootInspect.CanUndo);

        (bool undoImgOk, string? undoImgErr) = workspace.Undo(charArchiveId, "");
        Assert.True(undoImgOk, undoImgErr);

        NodeInspectionDto restoredImgInspect = workspace.InspectNode(charArchiveId, "Coat/01040000.img");
        Assert.True(restoredImgInspect.Ok, restoredImgInspect.Error);
        Assert.NotEqual(RepackerChangeState.Deleted, restoredImgInspect.ChangeState);
        Assert.True(restoredImgInspect.CanRedo);

        (bool redoImgOk, string? redoImgErr) = workspace.Redo(charArchiveId, "");
        Assert.True(redoImgOk, redoImgErr);
        Assert.Equal(RepackerChangeState.Deleted, workspace.InspectNode(charArchiveId, "Coat/01040000.img").ChangeState);

        (bool undoImgAgainOk, string? undoImgAgainErr) = workspace.Undo(charArchiveId, "");
        Assert.True(undoImgAgainOk, undoImgAgainErr);
        Assert.NotEqual(RepackerChangeState.Deleted, workspace.InspectNode(charArchiveId, "Coat/01040000.img").ChangeState);

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
        WzValue coatImgRepacked = repacked.ParseImage("Coat/01040000.img");
        Assert.NotNull(coatImgRepacked["info"]);
    }

    [Fact]
    public void DirNodes_AutomaticallyLinkAndOpenSplitWzFilesAndSubdirectories() {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"maple_repacker_dirlink_{Guid.NewGuid():N}");
        try {
            string baseDir = Path.Combine(tempRoot, "Base");
            string charDir = Path.Combine(tempRoot, "Character");
            string coatDir = Path.Combine(charDir, "Coat");
            string coatCanvasDir = Path.Combine(coatDir, "_Canvas");
            string weaponDir = Path.Combine(charDir, "Weapon");
            string stringDir = Path.Combine(tempRoot, "String");

            Directory.CreateDirectory(baseDir);
            Directory.CreateDirectory(coatCanvasDir);
            Directory.CreateDirectory(weaponDir);
            Directory.CreateDirectory(stringDir);

            // 1. Write Data/String/String.wz (63-byte empty stub) + Data/String/String_000.wz (actual Eqp.img)
            using (var emptyWz = new WzArchive()) {
                emptyWz.Save(Path.Combine(stringDir, "String.wz"));
            }
            using (var stringShard = new WzArchive()) {
                var eqpImg = new WzValue.Object();
                eqpImg["Eqp/Coat/1040000/name"] = "White Undershirt";
                stringShard.SetImage("Eqp.img", eqpImg);
                stringShard.Save(Path.Combine(stringDir, "String_000.wz"));
            }

            // 2. Write Data/Character/Coat/_Canvas/_Canvas_000.wz with real 24x20 sprite
            byte[] rgba = new byte[24 * 20 * 4];
            for (int i = 0; i < rgba.Length; i += 4) {
                rgba[i] = 60;
                rgba[i + 1] = 140;
                rgba[i + 2] = 240;
                rgba[i + 3] = 255;
            }
            byte[] encodedSprite = WzCanvasCodec.EncodeFromRgba(24, 20, (int) WzCanvasFormat.Bgra8888, rgba);
            using (var coatCanvasWz = new WzArchive()) {
                var canvasImg = new WzValue.Object();
                canvasImg["stand1/0/mail"] = new WzValue.Canvas(
                    24,
                    20,
                    (int) WzCanvasFormat.Bgra8888,
                    encodedSprite,
                    [new WzProperty("origin", (11, 19))]);
                coatCanvasWz.SetImage("01040000.img", canvasImg);
                coatCanvasWz.Save(Path.Combine(coatCanvasDir, "_Canvas_000.wz"));
            }

            // 3. Write Data/Character/Coat/Coat.wz (empty DIR "_Canvas") + Coat_000.wz (01040000.img with _outlink)
            System.IO.File.WriteAllBytes(Path.Combine(coatDir, "Coat.wz"), BuildDirectoryOnlyWzArchive(["_Canvas"]));
            using (var coatShard = new WzArchive()) {
                var coatImg = new WzValue.Object();
                coatImg["info/islot"] = "Ma";
                coatImg["stand1/0/mail"] = new WzValue.Canvas(
                    1,
                    1,
                    (int) WzCanvasFormat.Bgra4444,
                    WzCanvasCodec.EncodeFromRgba(1, 1, (int) WzCanvasFormat.Bgra4444, [0, 0, 0, 0]),
                    [
                        new WzProperty("origin", (11, 19)),
                        new WzProperty("_outlink", "Character/Coat/_Canvas/01040000.img/stand1/0/mail"),
                    ]);
                coatShard.SetImage("01040000.img", coatImg);
                coatShard.Save(Path.Combine(coatDir, "Coat_000.wz"));
            }

            // 4. Write Data/Character/Weapon/Weapon_000.wz
            using (var weaponShard = new WzArchive()) {
                var weaponImg = new WzValue.Object();
                weaponImg["info/islot"] = "Wp";
                weaponShard.SetImage("01302000.img", weaponImg);
                weaponShard.Save(Path.Combine(weaponDir, "Weapon_000.wz"));
            }

            // 5. Write Data/Character/Character.wz with empty DIR nodes "Coat" and "Weapon"
            string charWzPath = Path.Combine(charDir, "Character.wz");
            System.IO.File.WriteAllBytes(charWzPath, BuildDirectoryOnlyWzArchive(["Coat", "Weapon"]));

            // 6. Write Data/Base/Base.wz with empty DIR nodes "Character" and "String" + Data/Base/Base_000.wz
            string baseWzPath = Path.Combine(baseDir, "Base.wz");
            System.IO.File.WriteAllBytes(baseWzPath, BuildDirectoryOnlyWzArchive(["Character", "String"]));
            using (var baseShard = new WzArchive()) {
                var pddImg = new WzValue.Object();
                pddImg["info/version"] = 1;
                baseShard.SetImage("StandardPDD.img", pddImg);
                baseShard.Save(Path.Combine(baseDir, "Base_000.wz"));
            }

            // Test A: Opening Character.wz directly auto-links Coat, Coat/_Canvas, and Weapon
            using (var ws = new RepackerWorkspace()) {
                ArchiveSummaryDto charSummary = ws.OpenArchiveFromFile(charWzPath);
                Assert.True(charSummary.ImageCount >= 2);

                TreeNodeDto[] rootNodes = ws.GetTreeChildren(charSummary.ArchiveId, "");
                TreeNodeDto coatNode = Assert.Single(rootNodes, n => n.Name == "Coat");
                TreeNodeDto weaponNode = Assert.Single(rootNodes, n => n.Name == "Weapon");
                Assert.True(coatNode.HasChildren);
                Assert.True(weaponNode.HasChildren);

                TreeNodeDto[] coatChildren = ws.GetTreeChildren(charSummary.ArchiveId, "Coat");
                Assert.Contains(coatChildren, n => n.Name == "01040000.img" && n.NodeKind == RepackerNodeKind.Image);
                TreeNodeDto canvasDirNode = Assert.Single(coatChildren, n => n.Name == "_Canvas" && n.NodeKind == RepackerNodeKind.Directory);
                Assert.True(canvasDirNode.HasChildren);

                TreeNodeDto[] canvasChildren = ws.GetTreeChildren(charSummary.ArchiveId, "Coat/_Canvas");
                Assert.Contains(canvasChildren, n => n.Name == "01040000.img" && n.NodeKind == RepackerNodeKind.Image);

                // Verify _outlink inside Coat/01040000.img resolves to Coat/_Canvas/01040000.img
                NodeInspectionDto mailInspect = ws.InspectNode(charSummary.ArchiveId, "Coat/01040000.img/stand1/0/mail");
                Assert.True(mailInspect.Ok, mailInspect.Error);
                Assert.NotNull(mailInspect.Canvas);
                Assert.Equal(24, mailInspect.Canvas.Width);
                Assert.Equal(20, mailInspect.Canvas.Height);
                Assert.Contains("Coat/_Canvas/01040000.img/stand1/0/mail", mailInspect.Canvas.ResolvedLinkPath);
            }

            // Test B: Opening 63-byte String.wz directly auto-links String_000.wz
            using (var ws = new RepackerWorkspace()) {
                ArchiveSummaryDto strSummary = ws.OpenArchiveFromFile(Path.Combine(stringDir, "String.wz"));
                Assert.Equal(1, strSummary.ImageCount);
                TreeNodeDto[] strChildren = ws.GetTreeChildren(strSummary.ArchiveId, "");
                Assert.Contains(strChildren, n => n.Name == "Eqp.img");
                NodeInspectionDto nameNode = ws.InspectNode(strSummary.ArchiveId, "Eqp.img/Eqp/Coat/1040000/name");
                Assert.Equal("White Undershirt", nameNode.DisplayValue);
            }

            // Test C: Opening Base/Base.wz directly auto-links sibling Character and String directories
            using (var ws = new RepackerWorkspace()) {
                ArchiveSummaryDto baseSummary = ws.OpenArchiveFromFile(baseWzPath);
                TreeNodeDto[] baseChildren = ws.GetTreeChildren(baseSummary.ArchiveId, "");
                Assert.Contains(baseChildren, n => n.Name == "Character" && n.HasChildren);
                Assert.Contains(baseChildren, n => n.Name == "String" && n.HasChildren);

                TreeNodeDto[] baseCharChildren = ws.GetTreeChildren(baseSummary.ArchiveId, "Character");
                Assert.Contains(baseCharChildren, n => n.Name == "Coat" && n.HasChildren);

                NodeInspectionDto eqpInspect = ws.InspectNode(baseSummary.ArchiveId, "String/Eqp.img/Eqp/Coat/1040000/name");
                Assert.True(eqpInspect.Ok, eqpInspect.Error);
                Assert.Equal("White Undershirt", eqpInspect.DisplayValue);
            }

            // Test D: OpenDataDirectory on Data/Character opens Character.wz with linked subdirs instead of duplicate shard archives
            using (var ws = new RepackerWorkspace()) {
                ArchiveSummaryDto[] opened = ws.OpenDataDirectory(charDir);
                ArchiveSummaryDto single = Assert.Single(opened);
                Assert.Equal("Character.wz", single.Name);
                TreeNodeDto[] coatKids = ws.GetTreeChildren(single.ArchiveId, "Coat");
                Assert.Contains(coatKids, n => n.Name == "01040000.img");
            }

            // Test D2: OpenDataDirectory on root Data/ merges everything under Base.wz (not Base_000.wz)
            using (var ws = new RepackerWorkspace()) {
                ArchiveSummaryDto[] opened = ws.OpenDataDirectory(tempRoot);
                ArchiveSummaryDto single = Assert.Single(opened);
                Assert.Equal("Base.wz", single.Name);
                Assert.Single(ws.GetArchiveSummaries());

                TreeNodeDto[] baseRoot = ws.GetTreeChildren(single.ArchiveId, "");
                Assert.Contains(baseRoot, n => n.Name == "StandardPDD.img" && n.NodeKind == RepackerNodeKind.Image);
                Assert.Contains(baseRoot, n => n.Name == "Character" && n.NodeKind == RepackerNodeKind.Directory);
                Assert.Contains(baseRoot, n => n.Name == "String" && n.NodeKind == RepackerNodeKind.Directory);
            }

            // Test E: OpenArchiveFromBytes resolves disk path via hintPaths and links String_000.wz and Character subdirs
            using (var ws = new RepackerWorkspace()) {
                byte[] strStubBytes = System.IO.File.ReadAllBytes(Path.Combine(stringDir, "String.wz"));
                ArchiveSummaryDto strSummary = ws.OpenArchiveFromBytes("String.wz", strStubBytes, filePath: null, version: null, hintPaths: [tempRoot]);
                Assert.Equal(1, strSummary.ImageCount);
                Assert.Contains(ws.GetTreeChildren(strSummary.ArchiveId, ""), n => n.Name == "Eqp.img");

                byte[] charStubBytes = System.IO.File.ReadAllBytes(charWzPath);
                ArchiveSummaryDto charSummary = ws.OpenArchiveFromBytes("Character.wz", charStubBytes, filePath: null, version: null, hintPaths: [tempRoot]);
                Assert.Equal((ushort) 779, charSummary.Version);
                Assert.True(charSummary.ImageCount >= 3);
                TreeNodeDto[] rootDirs = ws.GetTreeChildren(charSummary.ArchiveId, "");
                Assert.Contains(rootDirs, n => n.Name == "Coat" && n.HasChildren);
                Assert.Contains(rootDirs, n => n.Name == "Weapon" && n.HasChildren);
            }

            // Test F: Opening a shard or child .wz file that is already linked through an open archive is detected as reopening
            using (var ws = new RepackerWorkspace()) {
                string stringWzPath = Path.Combine(stringDir, "String.wz");
                string string000Path = Path.Combine(stringDir, "String_000.wz");

                ArchiveSummaryDto str1 = ws.OpenArchiveFromFile(stringWzPath);
                Assert.NotNull(str1.LinkedFilePaths);
                Assert.Contains(str1.LinkedFilePaths, p => p.EndsWith("String_000.wz", StringComparison.OrdinalIgnoreCase));

                ArchiveSummaryDto str2 = ws.OpenArchiveFromFile(string000Path);
                Assert.Equal(str1.ArchiveId, str2.ArchiveId);
                Assert.Equal("String.wz", str2.Name);
                Assert.Single(ws.GetArchiveSummaries());

                string coat000Path = Path.Combine(coatDir, "Coat_000.wz");
                ArchiveSummaryDto char1 = ws.OpenArchiveFromFile(charWzPath);
                Assert.NotNull(char1.LinkedFilePaths);
                Assert.Contains(char1.LinkedFilePaths, p => p.EndsWith("Coat_000.wz", StringComparison.OrdinalIgnoreCase));

                ArchiveSummaryDto char2 = ws.OpenArchiveFromFile(coat000Path);
                Assert.Equal(char1.ArchiveId, char2.ArchiveId);
                Assert.Equal("Character.wz", char2.Name);
                Assert.Equal(2, ws.GetArchiveSummaries().Length);
            }
        } finally {
            if (Directory.Exists(tempRoot)) {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    private static byte[] BuildDirectoryOnlyWzArchive(IReadOnlyList<string> dirNames, ushort version = 779) {
        const uint fstart = 0x3C;
        uint vh = 0;
        foreach (char ch in version.ToString(System.Globalization.CultureInfo.InvariantCulture)) {
            vh = vh * 32 + ch + 1;
        }
        ushort encVer = (ushort) (0xFFu ^ ((vh >> 24) & 0xFFu) ^ ((vh >> 16) & 0xFFu) ^ ((vh >> 8) & 0xFFu) ^ (vh & 0xFFu));

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        // 60-byte PKG1 header
        bw.Write("PKG1"u8);
        bw.Write(0UL); // placeholder fsize
        bw.Write(fstart);
        byte[] copyright = System.Text.Encoding.UTF8.GetBytes("Package file v1.0 Copyright 2002 Wizet, ZMS");
        bw.Write(copyright);
        bw.Write((byte) 0);

        // At fstart (0x3C): encVer + entry count
        bw.Write(encVer);
        bw.Write((sbyte) dirNames.Count);

        var offsetPositions = new int[dirNames.Count];
        foreach ((int idx, string name) in dirNames.Index()) {
            bw.Write((byte) 3); // WzNodeKind.Directory
            bw.Write((sbyte) (-name.Length));
            for (int k = 0; k < name.Length; k++) {
                bw.Write((byte) (name[k] ^ unchecked((byte) (0xAA + k))));
            }
            bw.Write((sbyte) 0); // size = 0
            bw.Write((sbyte) 0); // checksum = 0
            offsetPositions[idx] = (int) ms.Position;
            bw.Write(0u); // placeholder offset
        }

        // Match real MSCW directory-only index .wz files where the file ends right after the root table
        uint eofPos = (uint) ms.Position;
        byte[] bytes = ms.ToArray();
        Span<byte> span = bytes;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(4, 8), (ulong) (bytes.Length - (int) fstart));

        for (int i = 0; i < dirNames.Count; i++) {
            uint fieldPos = (uint) offsetPositions[i];
            uint real = eofPos;
            uint o = unchecked(fieldPos - fstart) ^ uint.MaxValue;
            o = unchecked(o * vh);
            o = unchecked(o - 0x581C3F6Du);
            o = System.Numerics.BitOperations.RotateLeft(o, (int) (o & 0x1Fu));
            uint encrypted = o ^ unchecked(real - unchecked(fstart * 2u));
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(offsetPositions[i], 4), encrypted);
        }

        return bytes;
    }
}
