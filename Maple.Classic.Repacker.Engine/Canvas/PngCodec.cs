using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Maple.Classic.Repacker.Engine.Canvas;

/// <summary>
/// Zero-dependency cross-platform PNG encoder and decoder operating on 32-bit RGBA8888 pixel buffers.
/// </summary>
public static class PngCodec {
    private static ReadOnlySpan<byte> PngSignature => [137, 80, 78, 71, 13, 10, 26, 10];

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable() {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++) {
            uint c = n;
            for (int k = 0; k < 8; k++) {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }
            table[n] = c;
        }
        return table;
    }

    private static uint ComputeCrc(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data) {
        uint c = 0xFFFFFFFFu;
        foreach (byte b in type) {
            c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        }
        foreach (byte b in data) {
            c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        }
        return c ^ 0xFFFFFFFFu;
    }

    public static byte[] EncodeRgba(int width, int height, ReadOnlySpan<byte> rgba) {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(width, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(height, 0);
        int expectedLen = width * height * 4;
        if (rgba.Length < expectedLen) {
            throw new ArgumentException($"RGBA buffer length ({rgba.Length}) is smaller than {width}x{height}x4 ({expectedLen}).", nameof(rgba));
        }

        using var ms = new MemoryStream(expectedLen / 2 + 128);
        ms.Write(PngSignature);

        // 1. IHDR chunk
        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr[..4], (uint) width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.Slice(4, 4), (uint) height);
        ihdr[8] = 8;  // Bit depth = 8
        ihdr[9] = 6;  // Color type = 6 (Truecolor with alpha RGBA)
        ihdr[10] = 0; // Compression = Deflate
        ihdr[11] = 0; // Filter = Adaptive
        ihdr[12] = 0; // Interlace = None
        WriteChunk(ms, "IHDR"u8, ihdr);

        // 2. IDAT chunk (filtered scanlines compressed with ZLibStream)
        int stride = width * 4;
        byte[] rawScanlines = new byte[height * (stride + 1)];
        for (int y = 0; y < height; y++) {
            int srcRow = y * stride;
            int dstRow = y * (stride + 1);
            // Filter 1 (Sub) gives strong compression on pixel art sprites while remaining fast
            rawScanlines[dstRow] = 1;
            for (int x = 0; x < stride; x++) {
                byte cur = rgba[srcRow + x];
                byte left = x >= 4 ? rgba[srcRow + x - 4] : (byte) 0;
                rawScanlines[dstRow + 1 + x] = unchecked((byte) (cur - left));
            }
        }

        byte[] compressedIdat;
        using (var idatMs = new MemoryStream()) {
            using (var zlib = new ZLibStream(idatMs, CompressionLevel.Fastest, leaveOpen: true)) {
                zlib.Write(rawScanlines, 0, rawScanlines.Length);
            }
            compressedIdat = idatMs.ToArray();
        }
        WriteChunk(ms, "IDAT"u8, compressedIdat);

        // 3. IEND chunk
        WriteChunk(ms, "IEND"u8, []);
        return ms.ToArray();
    }

    public static (int Width, int Height, byte[] Rgba) DecodeRgba(ReadOnlySpan<byte> pngBytes) {
        if (pngBytes.Length < 33 || !pngBytes[..8].SequenceEqual(PngSignature)) {
            throw new InvalidDataException("Invalid PNG signature.");
        }

        int pos = 8;
        int width = 0;
        int height = 0;
        byte bitDepth = 0;
        byte colorType = 0;
        byte interlace = 0;
        byte[]? palette = null;
        byte[]? trns = null;
        using var idatBuffer = new MemoryStream();

        while (pos + 8 <= pngBytes.Length) {
            int chunkLen = (int) BinaryPrimitives.ReadUInt32BigEndian(pngBytes.Slice(pos, 4));
            ReadOnlySpan<byte> chunkType = pngBytes.Slice(pos + 4, 4);
            pos += 8;
            if (chunkLen < 0 || pos + chunkLen + 4 > pngBytes.Length) {
                break;
            }
            ReadOnlySpan<byte> chunkData = pngBytes.Slice(pos, chunkLen);
            pos += chunkLen + 4; // skip data + CRC

            if (chunkType.SequenceEqual("IHDR"u8)) {
                if (chunkLen < 13) {
                    throw new InvalidDataException("Malformed PNG IHDR chunk.");
                }
                width = (int) BinaryPrimitives.ReadUInt32BigEndian(chunkData[..4]);
                height = (int) BinaryPrimitives.ReadUInt32BigEndian(chunkData.Slice(4, 4));
                bitDepth = chunkData[8];
                colorType = chunkData[9];
                interlace = chunkData[12];
            } else if (chunkType.SequenceEqual("PLTE"u8)) {
                palette = chunkData.ToArray();
            } else if (chunkType.SequenceEqual("tRNS"u8)) {
                trns = chunkData.ToArray();
            } else if (chunkType.SequenceEqual("IDAT"u8)) {
                idatBuffer.Write(chunkData);
            } else if (chunkType.SequenceEqual("IEND"u8)) {
                break;
            }
        }

        if (width <= 0 || height <= 0) {
            throw new InvalidDataException("PNG IHDR missing or invalid dimensions.");
        }
        if (interlace != 0) {
            throw new InvalidDataException("Interlaced PNG images are not supported; please save as non-interlaced PNG.");
        }
        if (bitDepth != 8) {
            throw new InvalidDataException($"Unsupported PNG bit depth {bitDepth} (only 8-bit per channel PNGs are supported).");
        }

        int bpp = colorType switch {
            0 => 1, // Grayscale
            2 => 3, // RGB
            3 => 1, // Indexed Palette
            4 => 2, // Grayscale + Alpha
            6 => 4, // RGBA
            _ => throw new InvalidDataException($"Unsupported PNG color type {colorType}."),
        };

        int stride = width * bpp;
        int expectedDecompressed = height * (stride + 1);
        byte[] decompressed = new byte[expectedDecompressed];

        idatBuffer.Position = 0;
        using (var zlib = new ZLibStream(idatBuffer, CompressionMode.Decompress)) {
            int totalRead = 0;
            while (totalRead < expectedDecompressed) {
                int n = zlib.Read(decompressed, totalRead, expectedDecompressed - totalRead);
                if (n <= 0) {
                    break;
                }
                totalRead += n;
            }
        }

        byte[] unfiltered = new byte[height * stride];
        for (int y = 0; y < height; y++) {
            int srcOffset = y * (stride + 1);
            byte filterType = decompressed[srcOffset];
            int dstRow = y * stride;
            int prevRow = (y - 1) * stride;

            for (int x = 0; x < stride; x++) {
                byte raw = decompressed[srcOffset + 1 + x];
                byte a = x >= bpp ? unfiltered[dstRow + x - bpp] : (byte) 0;
                byte b = y > 0 ? unfiltered[prevRow + x] : (byte) 0;
                byte c = (x >= bpp && y > 0) ? unfiltered[prevRow + x - bpp] : (byte) 0;

                unfiltered[dstRow + x] = filterType switch {
                    0 => raw,
                    1 => unchecked((byte) (raw + a)),
                    2 => unchecked((byte) (raw + b)),
                    3 => unchecked((byte) (raw + ((a + b) >> 1))),
                    4 => unchecked((byte) (raw + PaethPredictor(a, b, c))),
                    _ => raw,
                };
            }
        }

        byte[] rgba = new byte[width * height * 4];
        int pixelCount = width * height;
        switch (colorType) {
            case 6: // RGBA
                Buffer.BlockCopy(unfiltered, 0, rgba, 0, rgba.Length);
                break;

            case 2: // RGB
                for (int i = 0; i < pixelCount; i++) {
                    int s = i * 3;
                    int d = i * 4;
                    rgba[d] = unfiltered[s];
                    rgba[d + 1] = unfiltered[s + 1];
                    rgba[d + 2] = unfiltered[s + 2];
                    rgba[d + 3] = 255;
                }
                break;

            case 4: // Grayscale + Alpha
                for (int i = 0; i < pixelCount; i++) {
                    byte g = unfiltered[i * 2];
                    byte a = unfiltered[i * 2 + 1];
                    int d = i * 4;
                    rgba[d] = g;
                    rgba[d + 1] = g;
                    rgba[d + 2] = g;
                    rgba[d + 3] = a;
                }
                break;

            case 0: // Grayscale
                for (int i = 0; i < pixelCount; i++) {
                    byte g = unfiltered[i];
                    int d = i * 4;
                    rgba[d] = g;
                    rgba[d + 1] = g;
                    rgba[d + 2] = g;
                    rgba[d + 3] = 255;
                }
                break;

            case 3: // Palette
                for (int i = 0; i < pixelCount; i++) {
                    int idx = unfiltered[i];
                    int d = i * 4;
                    if (palette is not null && idx * 3 + 2 < palette.Length) {
                        rgba[d] = palette[idx * 3];
                        rgba[d + 1] = palette[idx * 3 + 1];
                        rgba[d + 2] = palette[idx * 3 + 2];
                    }
                    rgba[d + 3] = (trns is not null && idx < trns.Length) ? trns[idx] : (byte) 255;
                }
                break;
        }

        return (width, height, rgba);
    }

    private static byte PaethPredictor(byte a, byte b, byte c) {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc) return a;
        return pb <= pc ? b : c;
    }

    private static void WriteChunk(Stream stream, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data) {
        Span<byte> lenBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(lenBytes, (uint) data.Length);
        stream.Write(lenBytes);
        stream.Write(type);
        if (!data.IsEmpty) {
            stream.Write(data);
        }
        uint crc = ComputeCrc(type, data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        stream.Write(crcBytes);
    }
}
