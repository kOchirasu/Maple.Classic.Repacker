namespace Maple.Classic.Repacker.Engine.Canvas;

public enum WzCanvasFormat {
    Bgra4444 = 1,
    Bgra8888 = 2,
    Bgr565 = 513,
    Bgr565Block16 = 517,
    Dxt3 = 1026,
    Dxt5 = 2050,
}

public static class WzCanvasFormatExtensions {
    public static string GetDisplayName(int format) => format switch {
        1 => "BGRA4444 (16-bit)",
        2 => "BGRA8888 (32-bit)",
        513 => "BGR565 (16-bit)",
        517 => "BGR565 Block16 (4-bit scale)",
        1026 => "DXT3 / BC2",
        2050 => "DXT5 / BC3",
        _ => $"Format {format}",
    };

    public static int GetExpectedUncompressedLength(int width, int height, int format) {
        if (width <= 0 || height <= 0) {
            return 0;
        }
        return format switch {
            1 => width * height * 2,
            2 => width * height * 4,
            513 => width * height * 2,
            517 => Math.Max(1, width >> 4) * Math.Max(1, height >> 4) * 2,
            1026 or 2050 => ((width + 3) / 4) * ((height + 3) / 4) * 16,
            _ => width * height * 4,
        };
    }
}
