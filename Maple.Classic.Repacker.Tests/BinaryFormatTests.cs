using Maple.Classic.File.IO.Values;
using Maple.Classic.Repacker.Engine.Audio;
using Maple.Classic.Repacker.Engine.Canvas;
using Xunit;
using Xunit.Abstractions;

namespace Maple.Classic.Repacker.Tests;

/// <summary>
/// Byte-exact checks of the binary headers the codecs produce, so refactoring how they are written cannot change the
/// output.
/// </summary>
public sealed class BinaryFormatTests(ITestOutputHelper output) {
    private const string WAV_HEADER_HEX = "524946462C00000057415645666D742010000000010001002256000044AC00000200100064617461080000000102030405060708";
    private const string DX8_WAVE_HEADER_HEX = "0283EB36E44F52CE119F530020AF0BA7708BEB36E44F52CE119F530020AF0BA7700001819F580556C3CE11BF0100AA0055595A0012010001002256000044AC0000020010000000";
    private const string DX8_MP3_HEADER_HEX = "0283EB36E44F52CE119F530020AF0BA7708BEB36E44F52CE119F530020AF0BA7700001819F580556C3CE11BF0100AA0055595A001E5500020044AC0000803E0000010000000C00010002000000A10101007105";
    private const string PNG_HEX = "89504E470D0A1A0A0000000D49484452000000030000000208060000009D74661A000000174944415478016364E01653D48102C696FE594BA16C1D003C7F055726D6AB260000000049454E44AE426082";

    [Fact]
    public void Dx8WaveHeader_IsByteExact() {
        WzValue.Sound sound = WzSoundCodec.CreateFromAudioBytes(BuildWav(sampleRate: 22050, channels: 1, bits: 16, pcmLength: 8));
        AssertHex(DX8_WAVE_HEADER_HEX, sound.Header.ToArray());
    }

    [Fact]
    public void Dx8Mp3Header_IsByteExact() {
        // MPEG-1 Layer III frame header: 128 kbps, 44.1 kHz, joint stereo
        byte[] mp3 = [0xFF, 0xFB, 0x90, 0x44, 0, 0, 0, 0];
        WzValue.Sound sound = WzSoundCodec.CreateFromAudioBytes(mp3);
        AssertHex(DX8_MP3_HEADER_HEX, sound.Header.ToArray());
    }

    [Fact]
    public void PcmSound_ExtractsAsByteExactRiffWave() {
        // A Sound_DX8 whose payload is raw PCM: extraction wraps it in a 44-byte RIFF header
        WzValue.Sound fromWav = WzSoundCodec.CreateFromAudioBytes(BuildWav(sampleRate: 22050, channels: 1, bits: 16, pcmLength: 8));
        var rawPcm = new WzValue.Sound(8, fromWav.Duration, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, fromWav.Header.ToArray());
        ExtractedSoundData extracted = WzSoundCodec.ExtractAudioStream(rawPcm);
        AssertHex(WAV_HEADER_HEX, extracted.AudioBytes);
    }

    [Fact]
    public void Png_IsByteExact() {
        byte[] rgba = new byte[3 * 2 * 4];
        for (int i = 0; i < rgba.Length; i++) {
            rgba[i] = (byte) (i * 11);
        }
        AssertHex(PNG_HEX, PngCodec.EncodeRgba(3, 2, rgba));
    }

    [Theory]
    // color type, bytes per pixel, 2 pixels of raw channel data, expected RGBA for those pixels
    [InlineData(0, 1, new byte[] { 10, 200 }, new byte[] { 10, 10, 10, 255, 200, 200, 200, 255 })]
    [InlineData(2, 3, new byte[] { 1, 2, 3, 4, 5, 6 }, new byte[] { 1, 2, 3, 255, 4, 5, 6, 255 })]
    [InlineData(4, 2, new byte[] { 7, 70, 8, 80 }, new byte[] { 7, 7, 7, 70, 8, 8, 8, 80 })]
    [InlineData(3, 1, new byte[] { 1, 0 }, new byte[] { 40, 50, 60, 128, 10, 20, 30, 255 })]
    public void PngDecode_SupportsEveryColorTypeAndFilter(byte colorType, int bpp, byte[] pixels, byte[] expectedRgba) {
        // 2x5 image: one row per filter type (None, Sub, Up, Average, Paeth), every row holding the same pixels
        const int width = 2;
        const int height = 5;
        int stride = width * bpp;
        var scanlines = new MemoryStream();
        byte[] prevRow = new byte[stride];
        for (byte filter = 0; filter < height; filter++) {
            scanlines.WriteByte(filter);
            for (int x = 0; x < stride; x++) {
                int a = x >= bpp ? pixels[x - bpp] : 0;
                int b = prevRow[x];
                int c = x >= bpp ? prevRow[x - bpp] : 0;
                int predictor = filter switch {
                    1 => a,
                    2 => b,
                    3 => (a + b) >> 1,
                    4 => Paeth(a, b, c),
                    _ => 0,
                };
                scanlines.WriteByte(unchecked((byte) (pixels[x] - predictor)));
            }
            prevRow = pixels;
        }

        byte[] png = BuildPng(width, height, colorType, scanlines.ToArray(),
            palette: colorType == 3 ? [10, 20, 30, 40, 50, 60] : null,
            transparency: colorType == 3 ? [255, 128] : null);
        (int w, int h, byte[] rgba) = PngCodec.DecodeRgba(png);

        Assert.Equal((width, height), (w, h));
        for (int row = 0; row < height; row++) {
            Assert.Equal(expectedRgba, rgba.AsSpan(row * width * 4, width * 4).ToArray());
        }
    }

    private static int Paeth(int a, int b, int c) {
        int p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    /// <summary>Minimal PNG writer for test input (the decoder does not verify CRCs, so they are left zero).</summary>
    private static byte[] BuildPng(int width, int height, byte colorType, byte[] scanlines, byte[]? palette, byte[]? transparency) {
        using var ms = new MemoryStream();
        ms.Write([137, 80, 78, 71, 13, 10, 26, 10]);

        void Chunk(string type, byte[] data) {
            Span<byte> u32 = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(u32, (uint) data.Length);
            ms.Write(u32);
            ms.Write(System.Text.Encoding.ASCII.GetBytes(type));
            ms.Write(data);
            ms.Write(new byte[4]);
        }

        byte[] ihdr = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint) width);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint) height);
        ihdr[8] = 8;
        ihdr[9] = colorType;
        Chunk("IHDR", ihdr);
        if (palette is not null) Chunk("PLTE", palette);
        if (transparency is not null) Chunk("tRNS", transparency);

        using var compressed = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(compressed, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true)) {
            z.Write(scanlines);
        }
        Chunk("IDAT", compressed.ToArray());
        Chunk("IEND", []);
        return ms.ToArray();
    }

    private void AssertHex(string expectedHex, byte[] actual) {
        string actualHex = Convert.ToHexString(actual);
        output.WriteLine(actualHex);
        Assert.Equal(expectedHex, actualHex);
    }

    private static byte[] BuildWav(int sampleRate, ushort channels, ushort bits, int pcmLength) {
        int blockAlign = channels * bits / 8;
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8);
        w.Write(36 + pcmLength);
        w.Write("WAVEfmt "u8);
        w.Write(16);
        w.Write((ushort) 1);
        w.Write(channels);
        w.Write(sampleRate);
        w.Write(sampleRate * blockAlign);
        w.Write((ushort) blockAlign);
        w.Write(bits);
        w.Write("data"u8);
        w.Write(pcmLength);
        w.Write(new byte[pcmLength]);
        return ms.ToArray();
    }
}
