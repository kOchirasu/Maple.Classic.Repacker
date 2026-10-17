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
    public static WzCanvasFormat FromFormatCode(int format) => (WzCanvasFormat) format switch {
        WzCanvasFormat.Bgra4444
            or WzCanvasFormat.Bgra8888
            or WzCanvasFormat.Bgr565
            or WzCanvasFormat.Bgr565Block16
            or WzCanvasFormat.Dxt3
            or WzCanvasFormat.Dxt5 => (WzCanvasFormat) format,
        _ => WzCanvasFormat.Bgra8888,
    };

    public static string GetDisplayName(this WzCanvasFormat format) => format switch {
        WzCanvasFormat.Bgra4444 => "BGRA4444 (16-bit)",
        WzCanvasFormat.Bgra8888 => "BGRA8888 (32-bit)",
        WzCanvasFormat.Bgr565 => "BGR565 (16-bit)",
        WzCanvasFormat.Bgr565Block16 => "BGR565 Block16 (4-bit scale)",
        WzCanvasFormat.Dxt3 => "DXT3 / BC2",
        WzCanvasFormat.Dxt5 => "DXT5 / BC3",
        _ => $"Format {(int) format}",
    };

    public static string GetDisplayName(int format) => ((WzCanvasFormat) format).GetDisplayName();

    public static int GetExpectedUncompressedLength(int width, int height, WzCanvasFormat format) {
        if (width <= 0 || height <= 0) {
            return 0;
        }
        return format switch {
            WzCanvasFormat.Bgra4444 => width * height * 2,
            WzCanvasFormat.Bgra8888 => width * height * 4,
            WzCanvasFormat.Bgr565 => width * height * 2,
            WzCanvasFormat.Bgr565Block16 => Math.Max(1, width >> 4) * Math.Max(1, height >> 4) * 2,
            WzCanvasFormat.Dxt3 or WzCanvasFormat.Dxt5 => ((width + 3) / 4) * ((height + 3) / 4) * 16,
            _ => width * height * 4,
        };
    }

    public static int GetExpectedUncompressedLength(int width, int height, int format) =>
        GetExpectedUncompressedLength(width, height, (WzCanvasFormat) format);
}
