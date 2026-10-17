using System.Text.Json;
using Maple.Classic.File.IO;
using Maple.Classic.Repacker.Engine.Canvas;

namespace Maple.Classic.Repacker.Engine.Serialization;

/// <summary>
/// Deserializes JSON produced by <see cref="WzValue.ToJson(int)"/> back into a <see cref="WzValue"/> property tree,
/// preserving binary payloads (<see cref="WzValue.Canvas"/>, <see cref="WzValue.Sound"/>, <see cref="WzValue.RawData"/>)
/// and exact primitive types when <paramref name="existing"/> is provided.
/// </summary>
public static class JsonWzCodec {
    public static WzValue Parse(string json, WzValue? existing = null) {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        });
        return ConvertElement(doc.RootElement, existing);
    }

    private static WzValue ConvertElement(JsonElement el, WzValue? existing) {
        switch (el.ValueKind) {
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return WzValue.Null.Instance;

            case JsonValueKind.True:
                return new WzValue.Int(1);

            case JsonValueKind.False:
                return new WzValue.Int(0);

            case JsonValueKind.String: {
                string s = el.GetString() ?? string.Empty;
                if (existing is WzValue.Uol) {
                    return new WzValue.Uol(s);
                }
                return new WzValue.String(s);
            }

            case JsonValueKind.Number: {
                string rawText = el.GetRawText();
                bool hasDecimalOrExp = rawText.Contains('.') || rawText.Contains('e') || rawText.Contains('E');
                if (!hasDecimalOrExp && el.TryGetInt64(out long lVal)) {
                    if (existing is WzValue.Short && lVal is >= short.MinValue and <= short.MaxValue) {
                        return new WzValue.Short((short) lVal);
                    }
                    if (existing is WzValue.Long || lVal is < int.MinValue or > int.MaxValue) {
                        return new WzValue.Long(lVal);
                    }
                    return new WzValue.Int((int) lVal);
                }

                double dVal = el.GetDouble();
                if (existing is WzValue.Float) {
                    return new WzValue.Float((float) dVal);
                }
                if (existing is WzValue.Double) {
                    return new WzValue.Double(dVal);
                }
                return new WzValue.Float((float) dVal);
            }

            case JsonValueKind.Array: {
                var items = new List<WzValue>();
                WzValue.Convex? existingConvex = existing as WzValue.Convex;
                int idx = 0;
                foreach (JsonElement item in el.EnumerateArray()) {
                    WzValue? prevItem = existingConvex is not null && idx < existingConvex.Items.Count
                        ? existingConvex.Items[idx]
                        : null;
                    items.Add(ConvertElement(item, prevItem));
                    idx++;
                }
                return new WzValue.Convex(items);
            }

            case JsonValueKind.Object:
                return ConvertObject(el, existing);

            default:
                return WzValue.Null.Instance;
        }
    }

    private static WzValue ConvertObject(JsonElement obj, WzValue? existing) {
        // 1. Check explicit UOL object { "_uol": "..." }
        if (obj.TryGetProperty("_uol", out JsonElement uolEl) && uolEl.ValueKind == JsonValueKind.String) {
            return new WzValue.Uol(uolEl.GetString() ?? string.Empty);
        }

        // 2. Check Vector2D { "x": <int>, "y": <int> } with exactly 2 properties
        if (IsVectorObject(obj, out int vx, out int vy)) {
            return new WzValue.Vector(vx, vy);
        }

        // 3. Check Canvas { "_canvas": true, ... }
        if (obj.TryGetProperty("_canvas", out JsonElement canvasFlag) && canvasFlag.ValueKind == JsonValueKind.True) {
            WzValue.Canvas? prevCanvas = existing as WzValue.Canvas;
            int width = obj.TryGetProperty("width", out JsonElement wEl) && wEl.TryGetInt32(out int w) ? w : (prevCanvas?.Width ?? 1);
            int height = obj.TryGetProperty("height", out JsonElement hEl) && hEl.TryGetInt32(out int h) ? h : (prevCanvas?.Height ?? 1);
            int format = obj.TryGetProperty("format", out JsonElement fEl) && fEl.TryGetInt32(out int f) ? f : (prevCanvas?.Format ?? 2);

            ReadOnlyMemory<byte> payload = prevCanvas?.Payload ?? ReadOnlyMemory<byte>.Empty;
            if (payload.IsEmpty) {
                byte[] blankRgba = new byte[Math.Max(1, width) * Math.Max(1, height) * 4];
                payload = WzCanvasCodec.EncodeFromRgba(Math.Max(1, width), Math.Max(1, height), format, blankRgba);
            }

            var children = new List<WzProperty>();
            foreach (JsonProperty prop in obj.EnumerateObject()) {
                if (prop.Name is "_canvas" or "width" or "height" or "format" or "bytes") {
                    continue;
                }
                WzValue? prevChild = prevCanvas?[prop.Name];
                children.Add(new WzProperty(prop.Name, ConvertElement(prop.Value, prevChild)));
            }

            return new WzValue.Canvas(width, height, format, payload, children);
        }

        // 4. Check Sound_DX8 { "_sound": true, ... }
        if (obj.TryGetProperty("_sound", out JsonElement soundFlag) && soundFlag.ValueKind == JsonValueKind.True) {
            WzValue.Sound? prevSound = existing as WzValue.Sound;
            int bytes = obj.TryGetProperty("bytes", out JsonElement bEl) && bEl.TryGetInt32(out int b) ? b : (prevSound?.DataLength ?? 0);
            int duration = obj.TryGetProperty("duration", out JsonElement dEl) && dEl.TryGetInt32(out int d) ? d : (prevSound?.Duration ?? 0);
            return new WzValue.Sound(
                bytes,
                duration,
                prevSound?.Payload ?? ReadOnlyMemory<byte>.Empty,
                prevSound?.Header ?? ReadOnlyMemory<byte>.Empty);
        }

        // 5. Check RawData { "_rawdata": true, ... }
        if (obj.TryGetProperty("_rawdata", out JsonElement rawFlag) && rawFlag.ValueKind == JsonValueKind.True) {
            WzValue.RawData? prevRaw = existing as WzValue.RawData;
            int bytes = obj.TryGetProperty("bytes", out JsonElement bEl) && bEl.TryGetInt32(out int b) ? b : (prevRaw?.DataLength ?? 0);
            return new WzValue.RawData(bytes) {
                Payload = prevRaw?.Payload ?? ReadOnlyMemory<byte>.Empty,
            };
        }

        // 6. Standard WzValue.Object property list
        var props = new List<WzProperty>();
        foreach (JsonProperty prop in obj.EnumerateObject()) {
            WzValue? prevChild = existing?[prop.Name];
            props.Add(new WzProperty(prop.Name, ConvertElement(prop.Value, prevChild)));
        }
        return new WzValue.Object(props);
    }

    private static bool IsVectorObject(JsonElement obj, out int x, out int y) {
        x = 0;
        y = 0;
        int count = 0;
        bool hasX = false;
        bool hasY = false;
        foreach (JsonProperty prop in obj.EnumerateObject()) {
            count++;
            if (count > 2) {
                return false;
            }
            if (prop.Name == "x" && prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetInt32(out int px)) {
                x = px;
                hasX = true;
            } else if (prop.Name == "y" && prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetInt32(out int py)) {
                y = py;
                hasY = true;
            } else {
                return false;
            }
        }
        return count == 2 && hasX && hasY;
    }
}
