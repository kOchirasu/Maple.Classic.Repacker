using System.Globalization;
using Maple.Classic.File.IO;
using Maple.Classic.Repacker.Engine.Canvas;
using Maple.Classic.Repacker.Engine.Models;
using static Maple.Classic.Repacker.Engine.Workspace.WzPropertyPaths;

namespace Maple.Classic.Repacker.Engine.Workspace;

/// <summary>
/// Resolves <c>_inlink</c>/<c>_outlink</c> Canvas references across open archives, builds Canvas previews, inlines linked sprites and extracts animation frames.
/// </summary>
internal static class CanvasLinkResolver {
    internal static CanvasPreviewDto BuildCanvasPreview(
        IReadOnlyList<ArchiveSession> archives,
        ArchiveSession session,
        string imgPath,
        WzValue imgRoot,
        WzValue.Canvas canvas) {
        string? inlink = canvas["_inlink"]?.AsString();
        string? outlink = canvas["_outlink"]?.AsString();
        string? zLayer = canvas["z"]?.AsString() ?? (canvas["z"]?.AsInt() is { } zi ? zi.ToString(CultureInfo.InvariantCulture) : null);
        int delayMs = canvas["delay"]?.AsInt() ?? 100;
        VectorPointDto origin = canvas["origin"] is WzValue.Vector(var ox, var oy) ? new VectorPointDto(ox, oy) : new VectorPointDto(0, 0);
        VectorPointDto? lt = canvas["lt"] is WzValue.Vector(var ltx, var lty) ? new VectorPointDto(ltx, lty) : null;
        VectorPointDto? rb = canvas["rb"] is WzValue.Vector(var rbx, var rby) ? new VectorPointDto(rbx, rby) : null;
        VectorPointDto? head = canvas["head"] is WzValue.Vector(var hx, var hy) ? new VectorPointDto(hx, hy) : null;

        WzValue.Canvas visualCanvas = canvas;
        string? resolvedLinkPath = null;
        bool isLinkStub = (!string.IsNullOrEmpty(inlink) || !string.IsNullOrEmpty(outlink)) && canvas.Width <= 1 && canvas.Height <= 1;

        if (!string.IsNullOrEmpty(inlink)) {
            if (ResolvePropertyNode(imgRoot, inlink) is WzValue.Canvas inCanvas) {
                visualCanvas = inCanvas;
                resolvedLinkPath = $"{imgPath}/{inlink}";
            }
        } else if (!string.IsNullOrEmpty(outlink)) {
            (WzValue.Canvas? outCanvas, string? outResolved) = ResolveOutlinkCanvas(archives, session, outlink);
            if (outCanvas is not null) {
                visualCanvas = outCanvas;
                resolvedLinkPath = outResolved;
            }
        }

        byte[] pngBytes = WzCanvasCodec.DecodeToPng(visualCanvas);
        WzCanvasFormat canvasFormat = WzCanvasFormatExtensions.FromFormatCode(visualCanvas.Format);
        return new CanvasPreviewDto(
            visualCanvas.Width,
            visualCanvas.Height,
            canvasFormat,
            canvasFormat.GetDisplayName(),
            visualCanvas.Payload.Length,
            pngBytes,
            origin,
            lt,
            rb,
            head,
            delayMs,
            zLayer,
            inlink,
            outlink,
            resolvedLinkPath,
            isLinkStub);
    }

    internal static (WzValue.Canvas? Canvas, string? ResolvedDesc) ResolveOutlinkCanvas(
        IReadOnlyList<ArchiveSession> archives,
        ArchiveSession currentSession,
        string outlink) {
        (string? targetImgPart, string innerSubPath) = ArchiveSession.SplitImageAndSubPath(outlink);
        if (targetImgPart is null || string.IsNullOrEmpty(innerSubPath)) {
            return (null, null);
        }

        // Build candidate .img paths by progressively stripping leading directory segments
        // (e.g., "Character/Coat/_Canvas/01040000.img" -> "Character/Coat/_Canvas/01040000.img",
        // "Coat/_Canvas/01040000.img", "_Canvas/01040000.img", "01040000.img")
        // as well as "_Canvas" variants when "_Canvas" is not already present.
        var candidateImgPaths = new List<string>(8);
        void AddCandidate(string p) {
            if (!string.IsNullOrEmpty(p) && !candidateImgPaths.Contains(p, StringComparer.OrdinalIgnoreCase)) {
                candidateImgPaths.Add(p);
            }
        }

        string[] segments = targetImgPart.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < segments.Length; i++) {
            string sub = string.Join('/', segments[i..]);
            AddCandidate(sub);
            if (i < segments.Length - 1 && !string.Equals(segments[^2], "_Canvas", StringComparison.OrdinalIgnoreCase)) {
                string withCanvas = string.Join('/', [.. segments[i..^1], "_Canvas", segments[^1]]);
                AddCandidate(withCanvas);
            }
        }
        if (segments.Length >= 1) {
            AddCandidate($"_Canvas/{segments[^1]}");
        }

        // Check currentSession first, then all other open ArchiveSessions
        var orderedSessions = new List<ArchiveSession>(archives.Count) { currentSession };
        foreach (ArchiveSession s in archives) {
            if (!ReferenceEquals(s, currentSession)) {
                orderedSessions.Add(s);
            }
        }

        foreach (ArchiveSession candidate in orderedSessions) {
            foreach (string tryImgPath in candidateImgPaths) {
                VirtualFsNode? vNode = candidate.ResolveVirtualNode(tryImgPath);
                if (vNode is { IsImage: true }) {
                    try {
                        WzValue parsed = candidate.GetOrParseImage(tryImgPath);
                        if (ResolvePropertyNode(parsed, innerSubPath) is WzValue.Canvas found &&
                            !(found.Width <= 1 && found.Height <= 1 && found["_outlink"] is not null)) {
                            return (found, $"{candidate.Name}:{tryImgPath}/{innerSubPath}");
                        }
                    } catch {
                        // Ignore and keep searching
                    }
                }
            }
        }

        return (null, null);
    }

    internal static int InlineLinksRecursive(IReadOnlyList<ArchiveSession> archives, ArchiveSession session, WzValue imgRoot, WzValue node) {
        int count = 0;
        if (node is WzValue.Canvas canvas) {
            string? inlink = canvas["_inlink"]?.AsString();
            string? outlink = canvas["_outlink"]?.AsString();
            WzValue.Canvas? resolved = null;

            if (!string.IsNullOrEmpty(inlink)) {
                resolved = ResolvePropertyNode(imgRoot, inlink) as WzValue.Canvas;
            } else if (!string.IsNullOrEmpty(outlink)) {
                (resolved, _) = ResolveOutlinkCanvas(archives, session, outlink);
            }

            if (resolved is not null) {
                canvas.Width = resolved.Width;
                canvas.Height = resolved.Height;
                canvas.Format = resolved.Format;
                canvas.Payload = resolved.Payload;
                canvas.Properties.RemoveAll(static p => p.Name is "_inlink" or "_outlink");
                count++;
            }
        }

        IReadOnlyList<WzProperty>? kids = node.Children;
        if (kids is not null) {
            for (int i = 0; i < kids.Count; i++) {
                count += InlineLinksRecursive(archives, session, imgRoot, kids[i].Value);
            }
        }
        return count;
    }

    internal static AnimationFrameDto[] ExtractAnimationFrames(
        IReadOnlyList<ArchiveSession> archives,
        ArchiveSession session,
        string imgPath,
        WzValue imgRoot,
        string subPath,
        WzValue selectedValue) {
        // Only extract animation frames when the parent container node of numbered frames ("0", "1", ...) is selected
        if (selectedValue is not WzValue.Object || !HasNumberedFrameChildren(selectedValue) || selectedValue.Children is null) {
            return [];
        }
        WzValue animContainer = selectedValue;
        string containerSubPath = subPath;

        var numberedProps = new List<(int Index, WzProperty Prop)>();
        foreach (WzProperty prop in animContainer.Children) {
            if (int.TryParse(prop.Name, out int frameIdx) && frameIdx >= 0) {
                numberedProps.Add((frameIdx, prop));
            }
        }
        numberedProps.Sort(static (a, b) => a.Index.CompareTo(b.Index));

        var frames = new List<AnimationFrameDto>(numberedProps.Count);
        foreach ((int idx, WzProperty prop) in numberedProps) {
            string frameSubPath = string.IsNullOrEmpty(containerSubPath) ? prop.Name : $"{containerSubPath}/{prop.Name}";
            WzValue frameVal = prop.Value;
            if (frameVal is WzValue.Uol(var uolStr)) {
                (WzValue? resolved, _) = ResolveUolTarget(imgRoot, frameSubPath, uolStr);
                if (resolved is not null) {
                    frameVal = resolved;
                }
            }

            WzValue.Canvas? frameCanvas = frameVal as WzValue.Canvas;
            int? overrideDelay = null;
            if (frameCanvas is null && frameVal is WzValue.Object frameObj && frameObj.Children is not null) {
                // Character equipment frame object (e.g. stand1/0/mail)
                overrideDelay = frameObj["delay"]?.AsInt();
                foreach (WzProperty sub in frameObj.Children) {
                    WzValue subVal = sub.Value;
                    if (subVal is WzValue.Uol(var subUol)) {
                        (WzValue? resSub, _) = ResolveUolTarget(imgRoot, $"{frameSubPath}/{sub.Name}", subUol);
                        if (resSub is not null) subVal = resSub;
                    }
                    if (subVal is WzValue.Canvas c) {
                        frameCanvas = c;
                        break;
                    }
                }
            }

            if (frameCanvas is null) {
                continue;
            }

            CanvasPreviewDto preview = BuildCanvasPreview(archives, session, imgPath, imgRoot, frameCanvas);
            int delay = overrideDelay ?? preview.DelayMs;
            int a0 = frameCanvas["a0"]?.AsInt() ?? 255;
            int a1 = frameCanvas["a1"]?.AsInt() ?? 255;

            frames.Add(new AnimationFrameDto(
                idx,
                prop.Name,
                $"{imgPath}/{frameSubPath}",
                preview.Width,
                preview.Height,
                preview.Format,
                Math.Max(30, delay),
                preview.Origin,
                preview.Lt,
                preview.Rb,
                preview.Head,
                a0,
                a1,
                preview.PngBytes));
        }

        return frames.Count > 1 ? [.. frames] : [];
    }

    internal static bool HasNumberedFrameChildren(WzValue val) {
        if (val.Children is null || val.Children.Count == 0) {
            return false;
        }
        foreach (WzProperty p in val.Children) {
            if (p.Name == "0") {
                return true;
            }
        }
        return false;
    }
}
