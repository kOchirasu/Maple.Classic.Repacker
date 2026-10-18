using System.Globalization;
using Maple.Classic.File.IO;
using Maple.Classic.Repacker.Engine.Canvas;
using Maple.Classic.Repacker.Engine.Models;

namespace Maple.Classic.Repacker.Engine.Workspace;

/// <summary>
/// Formatting helpers that describe <see cref="WzValue"/> nodes for the UI (kinds, display values, byte sizes, hex slices).
/// </summary>
public static class WzValueFormatter {
    public static (RepackerPropertyKind Kind, string DisplayValue) DescribeWzValue(WzValue v) {
        return v switch {
            WzValue.Null => (RepackerPropertyKind.Null, "null"),
            WzValue.Short(var x) => (RepackerPropertyKind.Short, x.ToString(CultureInfo.InvariantCulture)),
            WzValue.Int(var x) => (RepackerPropertyKind.Int, x.ToString(CultureInfo.InvariantCulture)),
            WzValue.Long(var x) => (RepackerPropertyKind.Long, x.ToString(CultureInfo.InvariantCulture)),
            WzValue.Float(var x) => (RepackerPropertyKind.Float, x.ToString("G", CultureInfo.InvariantCulture)),
            WzValue.Double(var x) => (RepackerPropertyKind.Double, x.ToString("G", CultureInfo.InvariantCulture)),
            WzValue.String(var s) => (RepackerPropertyKind.String, s),
            WzValue.Vector(var x, var y) => (RepackerPropertyKind.Vector, $"({x}, {y})"),
            WzValue.Uol(var s) => (RepackerPropertyKind.Uol, $"-> {s}"),
            WzValue.Object(var props) => (RepackerPropertyKind.Object, $"{props.Count} properties"),
            WzValue.Canvas c => (RepackerPropertyKind.Canvas, $"{c.Width}x{c.Height} ({c.Format.GetDisplayName()})"),
            WzValue.Sound(var len, var dur) => (RepackerPropertyKind.Sound, $"{dur} ms ({FormatByteSize(len)})"),
            WzValue.Convex(var items) => (RepackerPropertyKind.Convex, $"{items.Count} shapes"),
            WzValue.RawData(var len) => (RepackerPropertyKind.RawData, FormatByteSize(len)),
            _ => (RepackerPropertyKind.None, string.Empty),
        };
    }

    internal static WzValue CreatePrimitiveOrStructuredValue(
        RepackerPropertyKind kind,
        string? rawValue,
        int? vecX,
        int? vecY,
        WzValue? existing) {
        string text = rawValue ?? string.Empty;
        return kind switch {
            RepackerPropertyKind.Null => WzValue.Null.Instance,
            RepackerPropertyKind.Short => new WzValue.Short(short.TryParse(text, CultureInfo.InvariantCulture, out short s) ? s : (short) 0),
            RepackerPropertyKind.Int => new WzValue.Int(int.TryParse(text, CultureInfo.InvariantCulture, out int i) ? i : 0),
            RepackerPropertyKind.Long => new WzValue.Long(long.TryParse(text, CultureInfo.InvariantCulture, out long l) ? l : 0L),
            RepackerPropertyKind.Float => new WzValue.Float(float.TryParse(text, CultureInfo.InvariantCulture, out float f) ? f : 0f),
            RepackerPropertyKind.Double => new WzValue.Double(double.TryParse(text, CultureInfo.InvariantCulture, out double d) ? d : 0.0),
            RepackerPropertyKind.String => new WzValue.String(text),
            RepackerPropertyKind.Uol => new WzValue.Uol(text),
            RepackerPropertyKind.Vector => new WzValue.Vector(vecX ?? 0, vecY ?? 0),
            RepackerPropertyKind.Object => existing is WzValue.Object existingObj ? existingObj : new WzValue.Object(),
            RepackerPropertyKind.Convex => existing is WzValue.Convex existingConv ? existingConv : new WzValue.Convex(),
            _ => new WzValue.String(text),
        };
    }

    internal static byte[] ExtractBinarySliceForHexView(
        ArchiveSession session,
        string imgPath,
        bool isImageRoot,
        WzValue target) {
        const int maxHexBytes = 65536;
        if (isImageRoot) {
            ReadOnlySpan<byte> span = session.GetRawImageMemory(imgPath).Span;
            return span[..Math.Min(span.Length, maxHexBytes)].ToArray();
        }
        switch (target) {
            case WzValue.Canvas c: {
                ReadOnlySpan<byte> span = c.Payload.Span;
                return span[..Math.Min(span.Length, maxHexBytes)].ToArray();
            }
            case WzValue.Sound s: {
                ReadOnlySpan<byte> hSpan = s.Header.Span;
                ReadOnlySpan<byte> pSpan = s.Payload.Span;
                int total = Math.Min(maxHexBytes, hSpan.Length + pSpan.Length);
                byte[] combined = new byte[total];
                int hCopy = Math.Min(total, hSpan.Length);
                hSpan[..hCopy].CopyTo(combined);
                int pCopy = Math.Min(total - hCopy, pSpan.Length);
                if (pCopy > 0) {
                    pSpan[..pCopy].CopyTo(combined.AsSpan(hCopy, pCopy));
                }
                return combined;
            }
            case WzValue.RawData r: {
                ReadOnlySpan<byte> span = r.Payload.Span;
                return span[..Math.Min(span.Length, maxHexBytes)].ToArray();
            }
            default: {
                var wrapper = new WzValue.Object([new WzProperty("value", target)]);
                byte[] serialized = wrapper.ToBytes();
                return serialized.Length <= maxHexBytes ? serialized : serialized[..maxHexBytes];
            }
        }
    }

    /// <summary>Size of the binary payload carried by Canvas, Sound and RawData values; 0 for everything else.</summary>
    internal static int GetPayloadSize(WzValue v) => v switch {
        WzValue.Canvas c => c.Payload.Length,
        WzValue.Sound s => s.DataLength,
        WzValue.RawData r => r.DataLength,
        _ => 0,
    };

    /// <summary>Number of direct (live) children: named properties of Object/Canvas, or items of a Convex.</summary>
    internal static int GetChildCount(WzValue v) => v switch {
        WzValue.Object(var props) => props.Count,
        WzValue.Canvas c => c.Properties.Count,
        WzValue.Convex(var items) => items.Count,
        _ => 0,
    };

    internal static string FormatByteSize(int bytes) => bytes switch {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes / (1024.0 * 1024.0):F2} MB",
    };

    internal static string EscapeJson(string s) =>
        s.Replace(@"\", @"\\").Replace("\"", "\\\"");
}
