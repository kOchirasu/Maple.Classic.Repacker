using System.Buffers.Binary;
using System.IO.Compression;
using Maple.Classic.File.IO;

namespace Maple.Classic.Repacker.Engine.Canvas;

/// <summary>
/// Decodes and encodes WZ <see cref="WzValue.Canvas"/> pixel payloads across all MapleStory pixel formats
/// (<c>BGRA4444</c>, <c>BGRA8888</c>, <c>BGR565</c>, <c>Format517</c>, <c>DXT3</c>, <c>DXT5</c>) and PNG.
/// </summary>
public static class WzCanvasCodec {
    public static byte[] DecompressPayload(ReadOnlySpan<byte> payload, int expectedLength) {
        if (expectedLength <= 0) {
            return [];
        }
        var output = new byte[expectedLength];
        if (payload.IsEmpty) {
            return output;
        }

        // 1. Standard Zlib header (CMF = 0x78)
        if (payload.Length >= 2 && payload[0] == 0x78 &&
            (payload[1] == 0x01 || payload[1] == 0x5E || payload[1] == 0x9C || payload[1] == 0xDA)) {
            try {
                using var ms = new MemoryStream(payload.ToArray());
                using var zlib = new ZLibStream(ms, CompressionMode.Decompress);
                ReadAll(zlib, output);
                return output;
            } catch {
                // Fall through to raw/deflate fallback
            }
        }

        // 2. Try raw Deflate starting at offset 2 or 0
        if (payload.Length > 2) {
            try {
                using var ms = new MemoryStream(payload[2..].ToArray());
                using var deflate = new DeflateStream(ms, CompressionMode.Decompress);
                int read = ReadAll(deflate, output);
                if (read > 0) {
                    return output;
                }
            } catch {
                // Fall through
            }
        }

        // 3. Raw uncompressed buffer fallback (e.g. unit test fixtures)
        int copyLen = Math.Min(payload.Length, expectedLength);
        payload[..copyLen].CopyTo(output);
        return output;
    }

    public static byte[] CompressPayload(ReadOnlySpan<byte> rawPixelBytes) {
        using var ms = new MemoryStream(rawPixelBytes.Length / 2 + 32);
        using (var zlib = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true)) {
            zlib.Write(rawPixelBytes);
        }
        return ms.ToArray();
    }

    public static byte[] DecodeToRgba(WzValue.Canvas canvas) {
        ArgumentNullException.ThrowIfNull(canvas);
        return DecodeToRgba(canvas.Width, canvas.Height, (WzCanvasFormat) canvas.Format, canvas.Payload.Span);
    }

    public static byte[] DecodeToPng(WzValue.Canvas canvas) {
        ArgumentNullException.ThrowIfNull(canvas);
        int w = Math.Max(1, canvas.Width);
        int h = Math.Max(1, canvas.Height);
        byte[] rgba = DecodeToRgba(w, h, (WzCanvasFormat) canvas.Format, canvas.Payload.Span);
        return PngCodec.EncodeRgba(w, h, rgba);
    }

    public static byte[] DecodeToRgba(int width, int height, int format, ReadOnlySpan<byte> payload) =>
        DecodeToRgba(width, height, (WzCanvasFormat) format, payload);

    public static byte[] DecodeToRgba(int width, int height, WzCanvasFormat format, ReadOnlySpan<byte> payload) {
        if (width <= 0 || height <= 0) {
            return [];
        }

        int expectedLen = WzCanvasFormatExtensions.GetExpectedUncompressedLength(width, height, format);
        byte[] raw = DecompressPayload(payload, expectedLen);
        byte[] rgba = new byte[width * height * 4];

        switch (format) {
            case WzCanvasFormat.Bgra4444: // 2 bytes/pixel
                DecodeBgra4444(raw, rgba, width * height);
                break;

            case WzCanvasFormat.Bgra8888: // 4 bytes/pixel
                DecodeBgra8888(raw, rgba, width * height);
                break;

            case WzCanvasFormat.Bgr565: // 2 bytes/pixel
                DecodeBgr565(raw, rgba, width * height);
                break;

            case WzCanvasFormat.Bgr565Block16: // 16x16 Block BGR565
                DecodeFormat517(raw, rgba, width, height);
                break;

            case WzCanvasFormat.Dxt3: // DXT3 (BC2)
                DecodeDxt3(raw, rgba, width, height);
                break;

            case WzCanvasFormat.Dxt5: // DXT5 (BC3)
                DecodeDxt5(raw, rgba, width, height);
                break;

            default:
                DecodeBgra8888(raw, rgba, width * height);
                break;
        }

        return rgba;
    }

    public static byte[] EncodeFromRgba(int width, int height, int format, ReadOnlySpan<byte> rgba) =>
        EncodeFromRgba(width, height, (WzCanvasFormat) format, rgba);

    public static byte[] EncodeFromRgba(int width, int height, WzCanvasFormat format, ReadOnlySpan<byte> rgba) {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(width, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(height, 0);
        int expectedRgba = width * height * 4;
        if (rgba.Length < expectedRgba) {
            throw new ArgumentException($"RGBA buffer too small ({rgba.Length} < {expectedRgba}).", nameof(rgba));
        }

        byte[] raw = EncodeRawPixelsFromRgba(width, height, format, rgba);
        return CompressPayload(raw);
    }

    public static (int Width, int Height, byte[] CompressedPayload) EncodePngToCanvasPayload(
        ReadOnlySpan<byte> pngBytes,
        WzCanvasFormat format = WzCanvasFormat.Bgra8888) {
        (int width, int height, byte[] rgba) = PngCodec.DecodeRgba(pngBytes);
        byte[] compressed = EncodeFromRgba(width, height, format, rgba);
        return (width, height, compressed);
    }

    public static byte[] EncodeRawPixelsFromRgba(int width, int height, int format, ReadOnlySpan<byte> rgba) =>
        EncodeRawPixelsFromRgba(width, height, (WzCanvasFormat) format, rgba);

    public static byte[] EncodeRawPixelsFromRgba(int width, int height, WzCanvasFormat format, ReadOnlySpan<byte> rgba) {
        int pixelCount = width * height;
        switch (format) {
            case WzCanvasFormat.Bgra4444: {
                byte[] dst = new byte[pixelCount * 2];
                for (int i = 0; i < pixelCount; i++) {
                    int s = i * 4;
                    byte r = (byte) (rgba[s] >> 4);
                    byte g = (byte) (rgba[s + 1] >> 4);
                    byte b = (byte) (rgba[s + 2] >> 4);
                    byte a = (byte) (rgba[s + 3] >> 4);
                    int d = i * 2;
                    dst[d] = (byte) (b | (g << 4));
                    dst[d + 1] = (byte) (r | (a << 4));
                }
                return dst;
            }

            case WzCanvasFormat.Bgra8888: {
                byte[] dst = new byte[pixelCount * 4];
                for (int i = 0; i < pixelCount; i++) {
                    int o = i * 4;
                    dst[o] = rgba[o + 2];     // B
                    dst[o + 1] = rgba[o + 1]; // G
                    dst[o + 2] = rgba[o];     // R
                    dst[o + 3] = rgba[o + 3]; // A
                }
                return dst;
            }

            case WzCanvasFormat.Bgr565: {
                byte[] dst = new byte[pixelCount * 2];
                for (int i = 0; i < pixelCount; i++) {
                    int s = i * 4;
                    ushort r = (ushort) (rgba[s] >> 3);
                    ushort g = (ushort) (rgba[s + 1] >> 2);
                    ushort b = (ushort) (rgba[s + 2] >> 3);
                    ushort u = (ushort) ((r << 11) | (g << 5) | b);
                    BinaryPrimitives.WriteUInt16LittleEndian(dst.AsSpan(i * 2, 2), u);
                }
                return dst;
            }

            case WzCanvasFormat.Bgr565Block16: {
                int blocksX = Math.Max(1, width >> 4);
                int blocksY = Math.Max(1, height >> 4);
                byte[] dst = new byte[blocksX * blocksY * 2];
                int idx = 0;
                for (int by = 0; by < blocksY; by++) {
                    for (int bx = 0; bx < blocksX; bx++) {
                        int px = Math.Min(width - 1, bx * 16);
                        int py = Math.Min(height - 1, by * 16);
                        int s = (py * width + px) * 4;
                        ushort r = (ushort) (rgba[s] >> 3);
                        ushort g = (ushort) (rgba[s + 1] >> 2);
                        ushort b = (ushort) (rgba[s + 2] >> 3);
                        ushort u = (ushort) ((r << 11) | (g << 5) | b);
                        BinaryPrimitives.WriteUInt16LittleEndian(dst.AsSpan(idx * 2, 2), u);
                        idx++;
                    }
                }
                return dst;
            }

            case WzCanvasFormat.Dxt3:
                return EncodeDxt3(rgba, width, height);

            case WzCanvasFormat.Dxt5:
                return EncodeDxt5(rgba, width, height);

            default:
                goto case WzCanvasFormat.Bgra8888;
        }
    }

    private static void DecodeBgra4444(ReadOnlySpan<byte> src, Span<byte> rgba, int pixelCount) {
        int count = Math.Min(pixelCount, src.Length / 2);
        for (int i = 0; i < count; i++) {
            byte b0 = src[i * 2];
            byte b1 = src[i * 2 + 1];
            int b = (b0 & 0x0F) * 17;
            int g = ((b0 >> 4) & 0x0F) * 17;
            int r = (b1 & 0x0F) * 17;
            int a = ((b1 >> 4) & 0x0F) * 17;

            int d = i * 4;
            rgba[d] = (byte) r;
            rgba[d + 1] = (byte) g;
            rgba[d + 2] = (byte) b;
            rgba[d + 3] = (byte) a;
        }
    }

    private static void DecodeBgra8888(ReadOnlySpan<byte> src, Span<byte> rgba, int pixelCount) {
        int count = Math.Min(pixelCount, src.Length / 4);
        for (int i = 0; i < count; i++) {
            int o = i * 4;
            rgba[o] = src[o + 2];     // R
            rgba[o + 1] = src[o + 1]; // G
            rgba[o + 2] = src[o];     // B
            rgba[o + 3] = src[o + 3]; // A
        }
    }

    private static void DecodeBgr565(ReadOnlySpan<byte> src, Span<byte> rgba, int pixelCount) {
        int count = Math.Min(pixelCount, src.Length / 2);
        for (int i = 0; i < count; i++) {
            ushort u = BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(i * 2, 2));
            UnpackRgb565(u, out byte r, out byte g, out byte b);
            int d = i * 4;
            rgba[d] = r;
            rgba[d + 1] = g;
            rgba[d + 2] = b;
            rgba[d + 3] = 255;
        }
    }

    private static void DecodeFormat517(ReadOnlySpan<byte> src, Span<byte> rgba, int width, int height) {
        int blocksX = Math.Max(1, width >> 4);
        int blocksY = Math.Max(1, height >> 4);
        int idx = 0;
        for (int by = 0; by < blocksY; by++) {
            for (int bx = 0; bx < blocksX; bx++) {
                if (idx * 2 + 2 > src.Length) {
                    return;
                }
                ushort u = BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(idx * 2, 2));
                idx++;
                UnpackRgb565(u, out byte r, out byte g, out byte b);

                for (int py = 0; py < 16; py++) {
                    int y = by * 16 + py;
                    if (y >= height) break;
                    for (int px = 0; px < 16; px++) {
                        int x = bx * 16 + px;
                        if (x >= width) break;
                        int d = (y * width + x) * 4;
                        rgba[d] = r;
                        rgba[d + 1] = g;
                        rgba[d + 2] = b;
                        rgba[d + 3] = 255;
                    }
                }
            }
        }
    }

    private static void DecodeDxt3(ReadOnlySpan<byte> src, Span<byte> rgba, int width, int height) {
        int blocksX = (width + 3) / 4;
        int blocksY = (height + 3) / 4;
        Span<(byte R, byte G, byte B)> colors = stackalloc (byte R, byte G, byte B)[4];
        Span<byte> alphas = stackalloc byte[16];

        int blockOffset = 0;
        for (int by = 0; by < blocksY; by++) {
            for (int bx = 0; bx < blocksX; bx++) {
                if (blockOffset + 16 > src.Length) {
                    return;
                }
                ReadOnlySpan<byte> block = src.Slice(blockOffset, 16);
                blockOffset += 16;

                for (int i = 0; i < 8; i++) {
                    byte ab = block[i];
                    alphas[i * 2] = (byte) ((ab & 0x0F) * 17);
                    alphas[i * 2 + 1] = (byte) (((ab >> 4) & 0x0F) * 17);
                }

                ushort c0 = BinaryPrimitives.ReadUInt16LittleEndian(block.Slice(8, 2));
                ushort c1 = BinaryPrimitives.ReadUInt16LittleEndian(block.Slice(10, 2));
                uint colorIndices = BinaryPrimitives.ReadUInt32LittleEndian(block.Slice(12, 4));
                ExpandDxtColorTable(c0, c1, colors);

                for (int py = 0; py < 4; py++) {
                    int y = by * 4 + py;
                    if (y >= height) continue;
                    for (int px = 0; px < 4; px++) {
                        int x = bx * 4 + px;
                        if (x >= width) continue;
                        int pIdx = py * 4 + px;
                        int cIdx = (int) ((colorIndices >> (pIdx * 2)) & 0x03);
                        (byte r, byte g, byte b) = colors[cIdx];
                        int d = (y * width + x) * 4;
                        rgba[d] = r;
                        rgba[d + 1] = g;
                        rgba[d + 2] = b;
                        rgba[d + 3] = alphas[pIdx];
                    }
                }
            }
        }
    }

    private static void DecodeDxt5(ReadOnlySpan<byte> src, Span<byte> rgba, int width, int height) {
        int blocksX = (width + 3) / 4;
        int blocksY = (height + 3) / 4;
        Span<(byte R, byte G, byte B)> colors = stackalloc (byte R, byte G, byte B)[4];
        Span<byte> alphaLut = stackalloc byte[8];

        int blockOffset = 0;
        for (int by = 0; by < blocksY; by++) {
            for (int bx = 0; bx < blocksX; bx++) {
                if (blockOffset + 16 > src.Length) {
                    return;
                }
                ReadOnlySpan<byte> block = src.Slice(blockOffset, 16);
                blockOffset += 16;

                byte a0 = block[0];
                byte a1 = block[1];
                alphaLut[0] = a0;
                alphaLut[1] = a1;
                if (a0 > a1) {
                    alphaLut[2] = (byte) ((6 * a0 + 1 * a1) / 7);
                    alphaLut[3] = (byte) ((5 * a0 + 2 * a1) / 7);
                    alphaLut[4] = (byte) ((4 * a0 + 3 * a1) / 7);
                    alphaLut[5] = (byte) ((3 * a0 + 4 * a1) / 7);
                    alphaLut[6] = (byte) ((2 * a0 + 5 * a1) / 7);
                    alphaLut[7] = (byte) ((1 * a0 + 6 * a1) / 7);
                } else {
                    alphaLut[2] = (byte) ((4 * a0 + 1 * a1) / 5);
                    alphaLut[3] = (byte) ((3 * a0 + 2 * a1) / 5);
                    alphaLut[4] = (byte) ((2 * a0 + 3 * a1) / 5);
                    alphaLut[5] = (byte) ((1 * a0 + 4 * a1) / 5);
                    alphaLut[6] = 0;
                    alphaLut[7] = 255;
                }

                ulong alphaBits = 0;
                for (int i = 0; i < 6; i++) {
                    alphaBits |= ((ulong) block[2 + i]) << (8 * i);
                }

                ushort c0 = BinaryPrimitives.ReadUInt16LittleEndian(block.Slice(8, 2));
                ushort c1 = BinaryPrimitives.ReadUInt16LittleEndian(block.Slice(10, 2));
                uint colorIndices = BinaryPrimitives.ReadUInt32LittleEndian(block.Slice(12, 4));
                ExpandDxtColorTable(c0, c1, colors);

                for (int py = 0; py < 4; py++) {
                    int y = by * 4 + py;
                    if (y >= height) continue;
                    for (int px = 0; px < 4; px++) {
                        int x = bx * 4 + px;
                        if (x >= width) continue;
                        int pIdx = py * 4 + px;
                        int aIdx = (int) ((alphaBits >> (pIdx * 3)) & 0x07);
                        int cIdx = (int) ((colorIndices >> (pIdx * 2)) & 0x03);
                        (byte r, byte g, byte b) = colors[cIdx];
                        int d = (y * width + x) * 4;
                        rgba[d] = r;
                        rgba[d + 1] = g;
                        rgba[d + 2] = b;
                        rgba[d + 3] = alphaLut[aIdx];
                    }
                }
            }
        }
    }

    private static byte[] EncodeDxt3(ReadOnlySpan<byte> rgba, int width, int height) {
        int blocksX = (width + 3) / 4;
        int blocksY = (height + 3) / 4;
        byte[] dst = new byte[blocksX * blocksY * 16];
        Span<(byte R, byte G, byte B, byte A)> tile = stackalloc (byte R, byte G, byte B, byte A)[16];

        int blockOffset = 0;
        for (int by = 0; by < blocksY; by++) {
            for (int bx = 0; bx < blocksX; bx++) {
                Extract4x4Tile(rgba, width, height, bx, by, tile);
                Span<byte> block = dst.AsSpan(blockOffset, 16);
                blockOffset += 16;

                for (int i = 0; i < 8; i++) {
                    byte aLow = (byte) (tile[i * 2].A >> 4);
                    byte aHigh = (byte) (tile[i * 2 + 1].A >> 4);
                    block[i] = (byte) (aLow | (aHigh << 4));
                }

                EncodeDxtColorBlock(tile, block[8..]);
            }
        }
        return dst;
    }

    private static byte[] EncodeDxt5(ReadOnlySpan<byte> rgba, int width, int height) {
        int blocksX = (width + 3) / 4;
        int blocksY = (height + 3) / 4;
        byte[] dst = new byte[blocksX * blocksY * 16];
        Span<(byte R, byte G, byte B, byte A)> tile = stackalloc (byte R, byte G, byte B, byte A)[16];
        Span<byte> alphaLut = stackalloc byte[8];

        int blockOffset = 0;
        for (int by = 0; by < blocksY; by++) {
            for (int bx = 0; bx < blocksX; bx++) {
                Extract4x4Tile(rgba, width, height, bx, by, tile);
                Span<byte> block = dst.AsSpan(blockOffset, 16);
                blockOffset += 16;

                byte minA = 255;
                byte maxA = 0;
                for (int i = 0; i < 16; i++) {
                    byte a = tile[i].A;
                    if (a < minA) minA = a;
                    if (a > maxA) maxA = a;
                }

                block[0] = maxA;
                block[1] = minA;
                alphaLut[0] = maxA;
                alphaLut[1] = minA;
                if (maxA > minA) {
                    alphaLut[2] = (byte) ((6 * maxA + 1 * minA) / 7);
                    alphaLut[3] = (byte) ((5 * maxA + 2 * minA) / 7);
                    alphaLut[4] = (byte) ((4 * maxA + 3 * minA) / 7);
                    alphaLut[5] = (byte) ((3 * maxA + 4 * minA) / 7);
                    alphaLut[6] = (byte) ((2 * maxA + 5 * minA) / 7);
                    alphaLut[7] = (byte) ((1 * maxA + 6 * minA) / 7);
                } else {
                    for (int k = 2; k < 8; k++) alphaLut[k] = maxA;
                }

                ulong alphaBits = 0;
                for (int i = 0; i < 16; i++) {
                    byte a = tile[i].A;
                    int bestIdx = 0;
                    int bestDist = int.MaxValue;
                    for (int k = 0; k < 8; k++) {
                        int dist = Math.Abs(a - alphaLut[k]);
                        if (dist < bestDist) {
                            bestDist = dist;
                            bestIdx = k;
                        }
                    }
                    alphaBits |= ((ulong) bestIdx) << (i * 3);
                }
                for (int i = 0; i < 6; i++) {
                    block[2 + i] = (byte) ((alphaBits >> (8 * i)) & 0xFF);
                }

                EncodeDxtColorBlock(tile, block[8..]);
            }
        }
        return dst;
    }

    private static void Extract4x4Tile(
        ReadOnlySpan<byte> rgba,
        int width,
        int height,
        int bx,
        int by,
        Span<(byte R, byte G, byte B, byte A)> tile) {
        for (int py = 0; py < 4; py++) {
            int y = Math.Min(height - 1, by * 4 + py);
            for (int px = 0; px < 4; px++) {
                int x = Math.Min(width - 1, bx * 4 + px);
                int s = (y * width + x) * 4;
                tile[py * 4 + px] = (rgba[s], rgba[s + 1], rgba[s + 2], rgba[s + 3]);
            }
        }
    }

    private static void EncodeDxtColorBlock(ReadOnlySpan<(byte R, byte G, byte B, byte A)> tile, Span<byte> dst8) {
        byte minR = 255, minG = 255, minB = 255;
        byte maxR = 0, maxG = 0, maxB = 0;
        for (int i = 0; i < 16; i++) {
            var p = tile[i];
            if (p.R < minR) minR = p.R;
            if (p.G < minG) minG = p.G;
            if (p.B < minB) minB = p.B;
            if (p.R > maxR) maxR = p.R;
            if (p.G > maxG) maxG = p.G;
            if (p.B > maxB) maxB = p.B;
        }

        ushort c0 = PackRgb565(maxR, maxG, maxB);
        ushort c1 = PackRgb565(minR, minG, minB);
        if (c0 < c1) {
            (c0, c1) = (c1, c0);
        }

        Span<(byte R, byte G, byte B)> palette = stackalloc (byte R, byte G, byte B)[4];
        ExpandDxtColorTable(c0, c1, palette);

        uint indices = 0;
        for (int i = 0; i < 16; i++) {
            var p = tile[i];
            int bestIdx = 0;
            int bestDist = int.MaxValue;
            for (int k = 0; k < 4; k++) {
                int dr = p.R - palette[k].R;
                int dg = p.G - palette[k].G;
                int db = p.B - palette[k].B;
                int dist = dr * dr + dg * dg + db * db;
                if (dist < bestDist) {
                    bestDist = dist;
                    bestIdx = k;
                }
            }
            indices |= ((uint) bestIdx) << (i * 2);
        }

        BinaryPrimitives.WriteUInt16LittleEndian(dst8[..2], c0);
        BinaryPrimitives.WriteUInt16LittleEndian(dst8.Slice(2, 2), c1);
        BinaryPrimitives.WriteUInt32LittleEndian(dst8.Slice(4, 4), indices);
    }

    private static void ExpandDxtColorTable(ushort c0, ushort c1, Span<(byte R, byte G, byte B)> colors) {
        UnpackRgb565(c0, out byte r0, out byte g0, out byte b0);
        UnpackRgb565(c1, out byte r1, out byte g1, out byte b1);
        colors[0] = (r0, g0, b0);
        colors[1] = (r1, g1, b1);
        if (c0 > c1) {
            colors[2] = ((byte) ((2 * r0 + r1) / 3), (byte) ((2 * g0 + g1) / 3), (byte) ((2 * b0 + b1) / 3));
            colors[3] = ((byte) ((r0 + 2 * r1) / 3), (byte) ((g0 + 2 * g1) / 3), (byte) ((b0 + 2 * b1) / 3));
        } else {
            colors[2] = ((byte) ((r0 + r1) / 2), (byte) ((g0 + g1) / 2), (byte) ((b0 + b1) / 2));
            colors[3] = (0, 0, 0);
        }
    }

    private static void UnpackRgb565(ushort u, out byte r, out byte g, out byte b) {
        int b5 = u & 0x1F;
        int g6 = (u >> 5) & 0x3F;
        int r5 = (u >> 11) & 0x1F;
        r = (byte) ((r5 << 3) | (r5 >> 2));
        g = (byte) ((g6 << 2) | (g6 >> 4));
        b = (byte) ((b5 << 3) | (b5 >> 2));
    }

    private static ushort PackRgb565(byte r, byte g, byte b) =>
        (ushort) (((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3));

    private static int ReadAll(Stream stream, byte[] buffer) {
        int total = 0;
        while (total < buffer.Length) {
            int n = stream.Read(buffer, total, buffer.Length - total);
            if (n <= 0) {
                break;
            }
            total += n;
        }
        return total;
    }
}
