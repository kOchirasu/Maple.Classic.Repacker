using System.Buffers.Binary;
using System.IO.Compression;
using System.IO.Hashing;
using Maple.Classic.Repacker.Engine.IO;

namespace Maple.Classic.Repacker.Engine.Canvas;

/// <summary>Chunk types, as their 4 ASCII bytes read as a big-endian <see cref="uint"/> (e.g. "IHDR").</summary>
internal enum PngChunkType : uint {
    Ihdr = 0x49484452, // "IHDR" image header
    Plte = 0x504C5445, // "PLTE" palette
    Trns = 0x74524E53, // "tRNS" transparency
    Idat = 0x49444154, // "IDAT" image data
    Iend = 0x49454E44, // "IEND" end of image
}

/// <summary>IHDR colour type: which channels each pixel has.</summary>
internal enum PngColorType : byte {
    Grayscale = 0,
    Truecolor = 2,
    Indexed = 3,
    GrayscaleAlpha = 4,
    TruecolorAlpha = 6,
}

/// <summary>Per-scanline filter applied before compression.</summary>
internal enum PngFilterType : byte {
    None = 0,
    Sub = 1,
    Up = 2,
    Average = 3,
    Paeth = 4,
}

/// <summary>IHDR compression method; PNG defines only deflate.</summary>
internal enum PngCompressionMethod : byte {
    Deflate = 0,
}

/// <summary>IHDR filter method; PNG defines only adaptive (per-scanline <see cref="PngFilterType"/>).</summary>
internal enum PngFilterMethod : byte {
    Adaptive = 0,
}

/// <summary>IHDR interlace method.</summary>
internal enum PngInterlaceMethod : byte {
    None = 0,
    Adam7 = 1,
}

/// <summary>
/// Cross-platform PNG encoder and decoder operating on 32-bit RGBA8888 pixel buffers.
/// </summary>
public static class PngCodec {
    private static ReadOnlySpan<byte> PngSignature => [137, 80, 78, 71, 13, 10, 26, 10];

    private const byte BIT_DEPTH = 8;

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
        var w = new SpanWriter(ihdr);
        w.WriteUInt32BigEndian((uint) width);
        w.WriteUInt32BigEndian((uint) height);
        w.WriteByte(BIT_DEPTH);
        w.WriteByte((byte) PngColorType.TruecolorAlpha);
        w.WriteByte((byte) PngCompressionMethod.Deflate);
        w.WriteByte((byte) PngFilterMethod.Adaptive);
        w.WriteByte((byte) PngInterlaceMethod.None);
        w.EnsureFilled();
        WriteChunk(ms, PngChunkType.Ihdr, ihdr);

        // 2. IDAT chunk (filtered scanlines compressed with ZLibStream)
        int stride = width * 4;
        byte[] rawScanlines = new byte[height * (stride + 1)];
        for (int y = 0; y < height; y++) {
            int srcRow = y * stride;
            int dstRow = y * (stride + 1);
            // Sub filtering gives strong compression on pixel art sprites while remaining fast
            rawScanlines[dstRow] = (byte) PngFilterType.Sub;
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
        WriteChunk(ms, PngChunkType.Idat, compressedIdat);

        // 3. IEND chunk
        WriteChunk(ms, PngChunkType.Iend, []);
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
        var colorType = PngColorType.Grayscale;
        var interlace = PngInterlaceMethod.None;
        byte[]? palette = null;
        byte[]? trns = null;
        using var idatBuffer = new MemoryStream();

        while (pos + 8 <= pngBytes.Length) {
            int chunkLen = (int) BinaryPrimitives.ReadUInt32BigEndian(pngBytes.Slice(pos, 4));
            var chunkType = (PngChunkType) BinaryPrimitives.ReadUInt32BigEndian(pngBytes.Slice(pos + 4, 4));
            pos += 8;
            if (chunkLen < 0 || pos + chunkLen + 4 > pngBytes.Length) {
                break;
            }
            ReadOnlySpan<byte> chunkData = pngBytes.Slice(pos, chunkLen);
            pos += chunkLen + 4; // skip data + CRC

            switch (chunkType) {
                case PngChunkType.Ihdr:
                    if (chunkLen < 13) {
                        throw new InvalidDataException("Malformed PNG IHDR chunk.");
                    }
                    width = (int) BinaryPrimitives.ReadUInt32BigEndian(chunkData[..4]);
                    height = (int) BinaryPrimitives.ReadUInt32BigEndian(chunkData.Slice(4, 4));
                    bitDepth = chunkData[8];
                    colorType = (PngColorType) chunkData[9];
                    interlace = (PngInterlaceMethod) chunkData[12];
                    break;
                case PngChunkType.Plte:
                    palette = chunkData.ToArray();
                    break;
                case PngChunkType.Trns:
                    trns = chunkData.ToArray();
                    break;
                case PngChunkType.Idat:
                    idatBuffer.Write(chunkData);
                    break;
            }
            if (chunkType == PngChunkType.Iend) {
                break;
            }
        }

        if (width <= 0 || height <= 0) {
            throw new InvalidDataException("PNG IHDR missing or invalid dimensions.");
        }
        if (interlace != PngInterlaceMethod.None) {
            throw new InvalidDataException("Interlaced PNG images are not supported; please save as non-interlaced PNG.");
        }
        if (bitDepth != BIT_DEPTH) {
            throw new InvalidDataException($"Unsupported PNG bit depth {bitDepth} (only 8-bit per channel PNGs are supported).");
        }

        int bpp = colorType switch {
            PngColorType.Grayscale or PngColorType.Indexed => 1,
            PngColorType.GrayscaleAlpha => 2,
            PngColorType.Truecolor => 3,
            PngColorType.TruecolorAlpha => 4,
            _ => throw new InvalidDataException($"Unsupported PNG color type {(byte) colorType}."),
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
            var filterType = (PngFilterType) decompressed[srcOffset];
            int dstRow = y * stride;
            int prevRow = (y - 1) * stride;

            for (int x = 0; x < stride; x++) {
                byte raw = decompressed[srcOffset + 1 + x];
                byte a = x >= bpp ? unfiltered[dstRow + x - bpp] : (byte) 0;
                byte b = y > 0 ? unfiltered[prevRow + x] : (byte) 0;
                byte c = (x >= bpp && y > 0) ? unfiltered[prevRow + x - bpp] : (byte) 0;

                unfiltered[dstRow + x] = filterType switch {
                    PngFilterType.Sub => unchecked((byte) (raw + a)),
                    PngFilterType.Up => unchecked((byte) (raw + b)),
                    PngFilterType.Average => unchecked((byte) (raw + ((a + b) >> 1))),
                    PngFilterType.Paeth => unchecked((byte) (raw + PaethPredictor(a, b, c))),
                    _ => raw, // PngFilterType.None (unknown filters are passed through unchanged)
                };
            }
        }

        byte[] rgba = new byte[width * height * 4];
        int pixelCount = width * height;
        switch (colorType) {
            case PngColorType.TruecolorAlpha:
                Buffer.BlockCopy(unfiltered, 0, rgba, 0, rgba.Length);
                break;

            case PngColorType.Truecolor:
                for (int i = 0; i < pixelCount; i++) {
                    int s = i * 3;
                    int d = i * 4;
                    rgba[d] = unfiltered[s];
                    rgba[d + 1] = unfiltered[s + 1];
                    rgba[d + 2] = unfiltered[s + 2];
                    rgba[d + 3] = 255;
                }
                break;

            case PngColorType.GrayscaleAlpha:
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

            case PngColorType.Grayscale:
                for (int i = 0; i < pixelCount; i++) {
                    byte g = unfiltered[i];
                    int d = i * 4;
                    rgba[d] = g;
                    rgba[d + 1] = g;
                    rgba[d + 2] = g;
                    rgba[d + 3] = 255;
                }
                break;

            case PngColorType.Indexed:
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

    /// <summary>Writes one chunk: data length, type, data, then the CRC-32 of type + data (all big-endian).</summary>
    private static void WriteChunk(Stream stream, PngChunkType type, ReadOnlySpan<byte> data) {
        Span<byte> prefix = stackalloc byte[8];
        var w = new SpanWriter(prefix);
        w.WriteUInt32BigEndian((uint) data.Length);
        w.WriteUInt32BigEndian((uint) type);
        w.EnsureFilled();
        stream.Write(prefix);
        stream.Write(data);

        var crc = new Crc32();
        crc.Append(prefix[4..]);
        crc.Append(data);
        Span<byte> crcBytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc.GetCurrentHashAsUInt32());
        stream.Write(crcBytes);
    }
}
