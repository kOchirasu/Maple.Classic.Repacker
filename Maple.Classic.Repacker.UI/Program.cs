using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using Maple.Classic.Repacker.Engine.Models;
using Maple.Classic.Repacker.Engine.Workspace;
using Maple.Classic.Repacker.UI.Rpc;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Maple.Classic.Repacker.UI;

public class Program {
    [STAThread]
    public static void Main(string[] args) {
        string webRoot = ResolveWebRootPath();
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions {
            Args = args,
            ContentRootPath = Path.GetDirectoryName(webRoot) ?? AppContext.BaseDirectory,
            WebRootPath = webRoot,
        });

        string listenUrl = ResolveListenUrl(args);
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")) &&
            !args.Any(a => a.StartsWith("--urls", StringComparison.OrdinalIgnoreCase))) {
            builder.WebHost.UseUrls(listenUrl);
        }

        var workspace = new RepackerWorkspace();
        var settingsStore = new UserSettingsStore();

        // Load any .wz files or directories passed on the command line
        foreach (string arg in args) {
            if (arg.StartsWith('-')) continue;
            try {
                if (System.IO.File.Exists(arg) && arg.EndsWith(".wz", StringComparison.OrdinalIgnoreCase)) {
                    workspace.OpenArchiveFromFile(arg);
                    settingsStore.RecordRecentPath(Path.GetFullPath(arg));
                } else if (Directory.Exists(arg)) {
                    ArchiveSummaryDto[] opened = workspace.OpenDataDirectory(arg);
                    if (opened.Length > 0) {
                        settingsStore.RecordRecentPath(Path.GetFullPath(arg));
                    }
                }
            } catch (Exception ex) {
                workspace.AppendLog($"Startup open failed for \"{arg}\": {ex.Message}", RepackerLogLevel.Warning);
            }
        }

        builder.Services.AddSingleton(workspace);
        builder.Services.AddSingleton(settingsStore);

        WebApplication app = builder.Build();
        app.UseDefaultFiles();
        var noCacheStaticOptions = new StaticFileOptions {
            OnPrepareResponse = ctx => {
                ctx.Context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
                ctx.Context.Response.Headers.Pragma = "no-cache";
                ctx.Context.Response.Headers.Expires = "0";
            },
        };
        app.UseStaticFiles(noCacheStaticOptions);

        string? tsOutWebRoot = ResolveCompiledTsWebRootPath(webRoot);
        if (tsOutWebRoot != null) {
            app.UseStaticFiles(new StaticFileOptions {
                FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(tsOutWebRoot),
                OnPrepareResponse = noCacheStaticOptions.OnPrepareResponse,
            });
        }

        // Fallback to embedded wwwroot/* manifest resources
        app.Use(async (ctx, next) => {
            await next().ConfigureAwait(false);
            if (ctx.Response.StatusCode == 404 &&
                HttpMethods.IsGet(ctx.Request.Method) &&
                ctx.Request.Path.HasValue) {
                string relPath = ctx.Request.Path.Value.TrimStart('/');
                if (!string.IsNullOrEmpty(relPath) && !relPath.StartsWith("api/", StringComparison.OrdinalIgnoreCase)) {
                    string resourceName = $"wwwroot/{relPath}";
                    await using Stream? resStream = typeof(Program).Assembly.GetManifestResourceStream(resourceName)
                        ?? typeof(Program).Assembly.GetManifestResourceStream($"wwwroot/{relPath.Replace('/', '\\')}");
                    if (resStream != null) {
                        ctx.Response.StatusCode = 200;
                        ctx.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
                        ctx.Response.ContentType = Path.GetExtension(relPath).ToLowerInvariant() switch {
                            ".js" => "application/javascript; charset=utf-8",
                            ".css" => "text/css; charset=utf-8",
                            ".html" => "text/html; charset=utf-8",
                            _ => "application/octet-stream",
                        };
                        await resStream.CopyToAsync(ctx.Response.Body).ConfigureAwait(false);
                    }
                }
            }
        });

        app.MapGet("/api/export", (HttpContext ctx) => {
            string archiveId = ctx.Request.Query["archiveId"].ToString();
            string path = ctx.Request.Query["path"].ToString();
            string format = ctx.Request.Query["format"].ToString();
            (bool ok, string? error, string fileName, string mimeType, byte[] data) =
                workspace.ExportNode(archiveId, path, string.IsNullOrWhiteSpace(format) ? "auto" : format);
            if (!ok) {
                return Results.BadRequest(new { ok = false, error });
            }
            return Results.File(data, mimeType, fileName);
        });

        app.UseWebSockets();

        var wsClients = new ConcurrentDictionary<Guid, WsClientConnection>();

        using var broadcastCts = new CancellationTokenSource();
        _ = Task.Run(async () => {
            long lastSentVersion = 0;
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
            while (await timer.WaitForNextTickAsync(broadcastCts.Token).ConfigureAwait(false)) {
                try {
                    long versionSnapshot = workspace.StateVersion;
                    if (versionSnapshot == lastSentVersion || wsClients.IsEmpty) {
                        continue;
                    }

                    byte[] payload = RpcSerializer.Encode(BuildStatusPayload(workspace, settingsStore));
                    foreach (KeyValuePair<Guid, WsClientConnection> kvp in wsClients) {
                        WsClientConnection conn = kvp.Value;
                        if (conn.Socket.State == WebSocketState.Open) {
                            try {
                                await conn.SendBinaryAsync(payload, broadcastCts.Token).ConfigureAwait(false);
                            } catch {
                                if (conn.Socket.State != WebSocketState.Open && wsClients.TryRemove(kvp.Key, out WsClientConnection? removed)) {
                                    removed.Dispose();
                                }
                            }
                        } else if (wsClients.TryRemove(kvp.Key, out WsClientConnection? removed)) {
                            removed.Dispose();
                        }
                    }
                    lastSentVersion = versionSnapshot;
                } catch (OperationCanceledException) when (broadcastCts.IsCancellationRequested) {
                    break;
                } catch {
                    // Keep broadcast loop alive on transient errors
                }
            }
        }, broadcastCts.Token);

        app.Map("/ws", async (HttpContext ctx) => {
            if (!ctx.WebSockets.IsWebSocketRequest) {
                ctx.Response.StatusCode = 400;
                return;
            }
            using WebSocket ws = await ctx.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            using var conn = new WsClientConnection(ws);
            var id = Guid.NewGuid();

            try {
                byte[] initial = RpcSerializer.Encode(BuildStatusPayload(workspace, settingsStore));
                await conn.SendBinaryAsync(initial, ctx.RequestAborted).ConfigureAwait(false);
                wsClients[id] = conn;

                byte[] buf = new byte[65536];
                using var frameMs = new MemoryStream();
                while (ws.State == WebSocketState.Open && !ctx.RequestAborted.IsCancellationRequested) {
                    frameMs.SetLength(0);
                    WebSocketReceiveResult res;
                    do {
                        res = await ws.ReceiveAsync(buf, ctx.RequestAborted).ConfigureAwait(false);
                        if (res.MessageType == WebSocketMessageType.Close) {
                            break;
                        }
                        if (res.Count > 0) {
                            frameMs.Write(buf, 0, res.Count);
                        }
                    } while (!res.EndOfMessage);

                    if (res.MessageType == WebSocketMessageType.Close) {
                        break;
                    }
                    if (frameMs.Length == 0 || res.MessageType != WebSocketMessageType.Binary) {
                        continue;
                    }

                    byte[] msgBytes = frameMs.ToArray();
                    _ = Task.Run(async () => {
                        try {
                            (long rpcId, string method, ReadOnlyMemory<byte> rawParams) = RpcSerializer.ReadRequestFrame(msgBytes);
                            if (rpcId > 0 && !string.IsNullOrWhiteSpace(method)) {
                                (bool ok, object? data) = ExecuteRpcMethod(
                                    method,
                                    rawParams,
                                    workspace,
                                    settingsStore);
                                byte[] replyBytes = RpcSerializer.Encode(new RpcResponseEnvelope("rpc", rpcId, ok, data));
                                await conn.SendBinaryAsync(replyBytes, ctx.RequestAborted).ConfigureAwait(false);
                            }
                        } catch {
                            // Ignore malformed RPC frame or disconnected socket
                        }
                    }, ctx.RequestAborted);
                }
            } catch {
                // Disconnected
            } finally {
                wsClients.TryRemove(id, out _);
            }
        });

#if WINDOWS
        bool headless = args.Any(a =>
                            string.Equals(a, "--headless", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(a, "--server", StringComparison.OrdinalIgnoreCase)) ||
                        string.Equals(Environment.GetEnvironmentVariable("MAPLE_REPACKER_HEADLESS"), "1", StringComparison.OrdinalIgnoreCase);

        if (!headless) {
            WindowsDesktopShell.Run(app, workspace, settingsStore, listenUrl, broadcastCts);
            return;
        }
#endif
        app.RunAsync().GetAwaiter().GetResult();
        broadcastCts.Cancel();
    }

    private static WorkspaceStatusPayloadDto BuildStatusPayload(RepackerWorkspace workspace, UserSettingsStore settingsStore) {
        return new WorkspaceStatusPayloadDto(
            "status",
            workspace.StateVersion,
            workspace.SelectedArchiveId,
            workspace.SelectedNodePath,
            workspace.GetArchiveSummaries(),
            workspace.GetClipboardState(),
            workspace.GetLogs().ToArray(),
            settingsStore.GetUiState(),
            settingsStore.GetRecentPaths());
    }

    private static (bool Ok, object? Data) ExecuteRpcMethod(
        string method,
        ReadOnlyMemory<byte> rawParams,
        RepackerWorkspace workspace,
        UserSettingsStore settingsStore) {
        try {
            switch (method) {
                case "workspace.status":
                    return (true, BuildStatusPayload(workspace, settingsStore));

                case "workspace.openFile": {
                    OpenArchiveFileRequest req = RpcSerializer.DecodeParams<OpenArchiveFileRequest>(rawParams);
                    if (string.IsNullOrWhiteSpace(req.FilePath)) {
                        return (false, new RpcOperationResponse(false, "File path is required."));
                    }
                    ArchiveSummaryDto summary = workspace.OpenArchiveFromFile(req.FilePath, req.Version);
                    settingsStore.RecordRecentPath(summary.FilePath);
                    return (true, new RpcOperationResponse(
                        Ok: true,
                        ArchiveId: summary.ArchiveId,
                        Path: "",
                        Archive: summary,
                        Status: BuildStatusPayload(workspace, settingsStore)));
                }

                case "workspace.openBytes": {
                    OpenArchiveBytesRequest req = RpcSerializer.DecodeParams<OpenArchiveBytesRequest>(rawParams);
                    if (req.Data is not { Length: > 0 }) {
                        return (false, new RpcOperationResponse(false, "Uploaded archive buffer is empty."));
                    }
                    ArchiveSummaryDto summary = workspace.OpenArchiveFromBytes(
                        req.FileName ?? "Uploaded.wz",
                        req.Data,
                        req.FilePath,
                        req.Version);
                    return (true, new RpcOperationResponse(
                        Ok: true,
                        ArchiveId: summary.ArchiveId,
                        Path: "",
                        Archive: summary,
                        Status: BuildStatusPayload(workspace, settingsStore)));
                }

                case "workspace.openDirectory": {
                    OpenDirectoryRequest req = RpcSerializer.DecodeParams<OpenDirectoryRequest>(rawParams);
                    if (string.IsNullOrWhiteSpace(req.DirectoryPath)) {
                        return (false, new RpcOperationResponse(false, "Directory path is required."));
                    }
                    ArchiveSummaryDto[] opened = workspace.OpenDataDirectory(req.DirectoryPath, req.Version);
                    if (opened.Length > 0) {
                        settingsStore.RecordRecentPath(Path.GetFullPath(req.DirectoryPath.Trim()));
                    }
                    return (true, new RpcOperationResponse(
                        Ok: opened.Length > 0,
                        Error: opened.Length == 0 ? "No readable .wz files found in the specified directory." : null,
                        ArchiveId: workspace.SelectedArchiveId,
                        Path: workspace.SelectedNodePath,
                        Archives: opened,
                        Status: BuildStatusPayload(workspace, settingsStore)));
                }

                case "workspace.createArchive": {
                    CreateArchiveRequest req = RpcSerializer.DecodeParams<CreateArchiveRequest>(rawParams);
                    ArchiveSummaryDto summary = workspace.CreateNewArchive(
                        req.Name ?? "Custom_000.wz",
                        req.Version ?? 779);
                    return (true, new RpcOperationResponse(
                        Ok: true,
                        ArchiveId: summary.ArchiveId,
                        Path: "",
                        Archive: summary,
                        Status: BuildStatusPayload(workspace, settingsStore)));
                }

                case "workspace.saveArchive": {
                    SaveArchiveRequest req = RpcSerializer.DecodeParams<SaveArchiveRequest>(rawParams);
                    string targetArchiveId = req.ArchiveId ?? workspace.SelectedArchiveId ?? string.Empty;
                    (bool ok, string? err, ArchiveSummaryDto? summary) =
                        workspace.SaveArchiveToDisk(targetArchiveId, req.TargetFilePath, req.Version);
                    if (ok && summary is not null && !string.IsNullOrEmpty(summary.FilePath)) {
                        settingsStore.RecordRecentPath(summary.FilePath);
                    }
                    return (ok, new RpcOperationResponse(
                        Ok: ok,
                        Error: err,
                        ArchiveId: summary?.ArchiveId ?? targetArchiveId,
                        Archive: summary,
                        Status: BuildStatusPayload(workspace, settingsStore)));
                }

                case "workspace.exportArchive": {
                    SaveArchiveRequest req = RpcSerializer.DecodeParams<SaveArchiveRequest>(rawParams);
                    string targetArchiveId = req.ArchiveId ?? workspace.SelectedArchiveId ?? string.Empty;
                    (bool ok, string? err, string fileName, byte[] bytes) =
                        workspace.ExportArchiveBytes(targetArchiveId, req.Version, commitClean: false);
                    return (ok, new BinaryExportResponseDto(ok, err, fileName, "application/octet-stream", bytes));
                }

                case "workspace.closeArchive": {
                    CloseArchiveRequest req = RpcSerializer.DecodeParams<CloseArchiveRequest>(rawParams);
                    string targetArchiveId = req.ArchiveId ?? workspace.SelectedArchiveId ?? string.Empty;
                    bool closed = workspace.CloseArchive(targetArchiveId);
                    return (closed, new RpcOperationResponse(
                        Ok: closed,
                        Error: closed ? null : "Archive not found.",
                        ArchiveId: workspace.SelectedArchiveId,
                        Path: workspace.SelectedNodePath,
                        Status: BuildStatusPayload(workspace, settingsStore)));
                }

                case "workspace.selectNode": {
                    NodeRefRequest req = RpcSerializer.DecodeParams<NodeRefRequest>(rawParams);
                    workspace.SelectNode(req.ArchiveId, req.Path);
                    return (true, new RpcOperationResponse(
                        Ok: true,
                        ArchiveId: workspace.SelectedArchiveId,
                        Path: workspace.SelectedNodePath));
                }

                case "tree.children": {
                    NodeRefRequest req = RpcSerializer.DecodeParams<NodeRefRequest>(rawParams);
                    string archiveId = req.ArchiveId ?? workspace.SelectedArchiveId ?? string.Empty;
                    string parentPath = req.Path ?? string.Empty;
                    TreeNodeDto[] children = workspace.GetTreeChildren(archiveId, parentPath);
                    return (true, new TreeChildrenResponse(true, null, archiveId, parentPath, children));
                }

                case "node.inspect": {
                    NodeRefRequest req = RpcSerializer.DecodeParams<NodeRefRequest>(rawParams);
                    string archiveId = req.ArchiveId ?? workspace.SelectedArchiveId ?? string.Empty;
                    string path = req.Path ?? string.Empty;
                    workspace.SelectNode(archiveId, path);
                    NodeInspectionDto dto = workspace.InspectNode(archiveId, path);
                    return (dto.Ok, dto);
                }

                case "node.setValue": {
                    SetPropertyValueRequest req = RpcSerializer.DecodeParams<SetPropertyValueRequest>(rawParams);
                    string archiveId = req.ArchiveId ?? workspace.SelectedArchiveId ?? string.Empty;
                    string path = req.Path ?? workspace.SelectedNodePath;
                    (bool ok, string? err) = workspace.SetPropertyValue(
                        archiveId,
                        path,
                        req.Kind,
                        req.RawValue,
                        req.VecX,
                        req.VecY);
                    return (ok, new RpcOperationResponse(
                        Ok: ok,
                        Error: err,
                        ArchiveId: archiveId,
                        Path: path,
                        Status: BuildStatusPayload(workspace, settingsStore)));
                }

                case "node.addProperty": {
                    AddPropertyRequest req = RpcSerializer.DecodeParams<AddPropertyRequest>(rawParams);
                    string archiveId = req.ArchiveId ?? workspace.SelectedArchiveId ?? string.Empty;
                    string parentPath = req.ParentPath ?? workspace.SelectedNodePath;
                    (bool ok, string? err, string createdPath) = workspace.AddProperty(
                        archiveId,
                        parentPath,
                        req.Name ?? string.Empty,
                        req.Kind,
                        req.RawValue,
                        req.VecX,
                        req.VecY,
                        req.BinaryPayload,
                        req.CanvasFormat);
                    return (ok, new RpcOperationResponse(
                        Ok: ok,
                        Error: err,
                        ArchiveId: archiveId,
                        Path: createdPath,
                        Status: BuildStatusPayload(workspace, settingsStore)));
                }

                case "node.addImage": {
                    AddImageRequest req = RpcSerializer.DecodeParams<AddImageRequest>(rawParams);
                    string archiveId = req.ArchiveId ?? workspace.SelectedArchiveId ?? string.Empty;
                    (bool ok, string? err, string createdPath) = workspace.AddImage(
                        archiveId,
                        req.ParentPath ?? workspace.SelectedNodePath,
                        req.ImageName ?? string.Empty,
                        req.RawImgBytes,
                        req.JsonContent);
                    return (ok, new RpcOperationResponse(
                        Ok: ok,
                        Error: err,
                        ArchiveId: archiveId,
                        Path: createdPath,
                        Status: BuildStatusPayload(workspace, settingsStore)));
                }

                case "node.addDirectory": {
                    AddDirectoryRequest req = RpcSerializer.DecodeParams<AddDirectoryRequest>(rawParams);
                    string archiveId = req.ArchiveId ?? workspace.SelectedArchiveId ?? string.Empty;
                    (bool ok, string? err, string createdPath) = workspace.AddDirectory(
                        archiveId,
                        req.ParentPath ?? workspace.SelectedNodePath,
                        req.DirName ?? string.Empty);
                    return (ok, new RpcOperationResponse(
                        Ok: ok,
                        Error: err,
                        ArchiveId: archiveId,
                        Path: createdPath,
                        Status: BuildStatusPayload(workspace, settingsStore)));
                }

                case "node.rename": {
                    RenameNodeRequest req = RpcSerializer.DecodeParams<RenameNodeRequest>(rawParams);
                    string archiveId = req.ArchiveId ?? workspace.SelectedArchiveId ?? string.Empty;
                    string path = req.Path ?? workspace.SelectedNodePath;
                    (bool ok, string? err, string newPath) = workspace.RenameNode(archiveId, path, req.NewName ?? string.Empty);
                    return (ok, new RpcOperationResponse(
                        Ok: ok,
                        Error: err,
                        ArchiveId: archiveId,
                        Path: newPath,
                        Status: BuildStatusPayload(workspace, settingsStore)));
                }

                case "node.delete": {
                    NodeRefRequest req = RpcSerializer.DecodeParams<NodeRefRequest>(rawParams);
                    string archiveId = req.ArchiveId ?? workspace.SelectedArchiveId ?? string.Empty;
                    string path = req.Path ?? workspace.SelectedNodePath;
                    (bool ok, string? err, string parentPath) = workspace.DeleteNode(archiveId, path);
                    return (ok, new RpcOperationResponse(
                        Ok: ok,
                        Error: err,
                        ArchiveId: archiveId,
                        Path: parentPath,
                        Status: BuildStatusPayload(workspace, settingsStore)));
                }

                case "node.duplicate": {
                    NodeRefRequest req = RpcSerializer.DecodeParams<NodeRefRequest>(rawParams);
                    string archiveId = req.ArchiveId ?? workspace.SelectedArchiveId ?? string.Empty;
                    string path = req.Path ?? workspace.SelectedNodePath;
                    (bool ok, string? err, string newPath) = workspace.DuplicateNode(archiveId, path);
                    return (ok, new RpcOperationResponse(
                        Ok: ok,
                        Error: err,
                        ArchiveId: archiveId,
                        Path: newPath,
                        Status: BuildStatusPayload(workspace, settingsStore)));
                }

                case "node.copy": {
                    NodeRefRequest req = RpcSerializer.DecodeParams<NodeRefRequest>(rawParams);
                    string archiveId = req.ArchiveId ?? workspace.SelectedArchiveId ?? string.Empty;
                    string path = req.Path ?? workspace.SelectedNodePath;
                    (bool ok, string? err, ClipboardStateDto clip) = workspace.CopyNode(archiveId, path);
                    return (ok, new RpcOperationResponse(
                        Ok: ok,
                        Error: err,
                        ArchiveId: archiveId,
                        Path: path,
                        Clipboard: clip,
                        Status: BuildStatusPayload(workspace, settingsStore)));
                }

                case "node.paste": {
                    PasteNodeRequest req = RpcSerializer.DecodeParams<PasteNodeRequest>(rawParams);
                    string archiveId = req.ArchiveId ?? workspace.SelectedArchiveId ?? string.Empty;
                    string targetPath = req.TargetPath ?? workspace.SelectedNodePath;
                    (bool ok, string? err, string pastedPath) = workspace.PasteNode(archiveId, targetPath, req.OverrideName);
                    return (ok, new RpcOperationResponse(
                        Ok: ok,
                        Error: err,
                        ArchiveId: archiveId,
                        Path: pastedPath,
                        Status: BuildStatusPayload(workspace, settingsStore)));
                }

                case "node.replaceCanvas": {
                    ReplaceCanvasRequest req = RpcSerializer.DecodeParams<ReplaceCanvasRequest>(rawParams);
                    string archiveId = req.ArchiveId ?? workspace.SelectedArchiveId ?? string.Empty;
                    string path = req.Path ?? workspace.SelectedNodePath;
                    (bool ok, string? err) = workspace.ReplaceCanvasPng(
                        archiveId,
                        path,
                        req.PngBytes ?? [],
                        req.Format,
                        req.OriginX,
                        req.OriginY,
                        req.DelayMs);
                    return (ok, new RpcOperationResponse(
                        Ok: ok,
                        Error: err,
                        ArchiveId: archiveId,
                        Path: path,
                        Status: BuildStatusPayload(workspace, settingsStore)));
                }

                case "node.changeCanvasFormat": {
                    ChangeCanvasFormatRequest req = RpcSerializer.DecodeParams<ChangeCanvasFormatRequest>(rawParams);
                    string archiveId = req.ArchiveId ?? workspace.SelectedArchiveId ?? string.Empty;
                    string path = req.Path ?? workspace.SelectedNodePath;
                    (bool ok, string? err) = workspace.ChangeCanvasFormat(archiveId, path, req.Format);
                    return (ok, new RpcOperationResponse(
                        Ok: ok,
                        Error: err,
                        ArchiveId: archiveId,
                        Path: path,
                        Status: BuildStatusPayload(workspace, settingsStore)));
                }

                case "node.replaceSound": {
                    ReplaceSoundRequest req = RpcSerializer.DecodeParams<ReplaceSoundRequest>(rawParams);
                    string archiveId = req.ArchiveId ?? workspace.SelectedArchiveId ?? string.Empty;
                    string path = req.Path ?? workspace.SelectedNodePath;
                    (bool ok, string? err) = workspace.ReplaceSound(
                        archiveId,
                        path,
                        req.AudioBytes ?? [],
                        req.DurationMs);
                    return (ok, new RpcOperationResponse(
                        Ok: ok,
                        Error: err,
                        ArchiveId: archiveId,
                        Path: path,
                        Status: BuildStatusPayload(workspace, settingsStore)));
                }

                case "node.applyJson": {
                    ApplyJsonRequest req = RpcSerializer.DecodeParams<ApplyJsonRequest>(rawParams);
                    string archiveId = req.ArchiveId ?? workspace.SelectedArchiveId ?? string.Empty;
                    string path = req.Path ?? workspace.SelectedNodePath;
                    (bool ok, string? err) = workspace.ApplyJsonSubtree(archiveId, path, req.JsonText ?? string.Empty);
                    return (ok, new RpcOperationResponse(
                        Ok: ok,
                        Error: err,
                        ArchiveId: archiveId,
                        Path: path,
                        Status: BuildStatusPayload(workspace, settingsStore)));
                }

                case "node.inlineOutlinks": {
                    NodeRefRequest req = RpcSerializer.DecodeParams<NodeRefRequest>(rawParams);
                    string archiveId = req.ArchiveId ?? workspace.SelectedArchiveId ?? string.Empty;
                    string path = req.Path ?? workspace.SelectedNodePath;
                    (bool ok, string? err, int count) = workspace.InlineOutlinks(archiveId, path);
                    return (ok, new RpcOperationResponse(
                        Ok: ok,
                        Error: err,
                        ArchiveId: archiveId,
                        Path: path,
                        Count: count,
                        Status: BuildStatusPayload(workspace, settingsStore)));
                }

                case "node.undo": {
                    NodeRefRequest req = RpcSerializer.DecodeParams<NodeRefRequest>(rawParams);
                    string archiveId = req.ArchiveId ?? workspace.SelectedArchiveId ?? string.Empty;
                    string path = req.Path ?? workspace.SelectedNodePath;
                    (bool ok, string? err) = workspace.Undo(archiveId, path);
                    return (ok, new RpcOperationResponse(
                        Ok: ok,
                        Error: err,
                        ArchiveId: archiveId,
                        Path: path,
                        Status: BuildStatusPayload(workspace, settingsStore)));
                }

                case "node.redo": {
                    NodeRefRequest req = RpcSerializer.DecodeParams<NodeRefRequest>(rawParams);
                    string archiveId = req.ArchiveId ?? workspace.SelectedArchiveId ?? string.Empty;
                    string path = req.Path ?? workspace.SelectedNodePath;
                    (bool ok, string? err) = workspace.Redo(archiveId, path);
                    return (ok, new RpcOperationResponse(
                        Ok: ok,
                        Error: err,
                        ArchiveId: archiveId,
                        Path: path,
                        Status: BuildStatusPayload(workspace, settingsStore)));
                }

                case "node.export": {
                    ExportNodeRequest req = RpcSerializer.DecodeParams<ExportNodeRequest>(rawParams);
                    string archiveId = req.ArchiveId ?? workspace.SelectedArchiveId ?? string.Empty;
                    string path = req.Path ?? workspace.SelectedNodePath;
                    (bool ok, string? err, string fileName, string mimeType, byte[] data) =
                        workspace.ExportNode(archiveId, path, req.Format);
                    return (ok, new BinaryExportResponseDto(ok, err, fileName, mimeType, data));
                }

                case "node.batchExport": {
                    BatchExportRequest req = RpcSerializer.DecodeParams<BatchExportRequest>(rawParams);
                    string archiveId = req.ArchiveId ?? workspace.SelectedArchiveId ?? string.Empty;
                    string path = req.Path ?? workspace.SelectedNodePath;
                    (bool ok, string? err, int count) = workspace.BatchExportToDirectory(
                        archiveId,
                        path,
                        req.OutputDirectory ?? string.Empty,
                        req.Mode ?? "png");
                    return (ok, new RpcOperationResponse(
                        Ok: ok,
                        Error: err,
                        ArchiveId: archiveId,
                        Path: path,
                        Count: count,
                        Status: BuildStatusPayload(workspace, settingsStore)));
                }

                case "search.query": {
                    SearchNodesRequest req = RpcSerializer.DecodeParams<SearchNodesRequest>(rawParams);
                    SearchResultItemDto[] hits = workspace.Search(new SearchQueryDto {
                        ArchiveId = req.ArchiveId,
                        Query = req.Query,
                        ValueQuery = req.ValueQuery,
                        PropertyKindFilter = req.PropertyKindFilter,
                        CaseSensitive = req.CaseSensitive,
                        ExactMatch = req.ExactMatch,
                        MaxResults = req.MaxResults > 0 ? req.MaxResults : 250,
                    });
                    return (true, new SearchResponseDto(true, null, hits));
                }

                case "logs.clear": {
                    workspace.ClearLogs();
                    return (true, new RpcOperationResponse(Ok: true, Status: BuildStatusPayload(workspace, settingsStore)));
                }

                case "ui.saveState": {
                    SaveUiStateRequest req = RpcSerializer.DecodeParams<SaveUiStateRequest>(rawParams);
                    if (req.UiState is not null) {
                        settingsStore.UpdateUiState(req.UiState);
                    }
                    return (true, new RpcOperationResponse(Ok: true));
                }

                default:
                    return (false, new RpcOperationResponse(false, $"Unknown RPC method: {method}"));
            }
        } catch (Exception ex) {
            return (false, new RpcOperationResponse(false, ex.Message));
        }
    }

    private static string ResolveListenUrl(string[] args) {
        for (int i = 0; i < args.Length; i++) {
            if (string.Equals(args[i], "--urls", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) {
                return args[i + 1].Split(';', StringSplitOptions.RemoveEmptyEntries)[0];
            }
            if (args[i].StartsWith("--urls=", StringComparison.OrdinalIgnoreCase)) {
                return args[i]["--urls=".Length..].Split(';', StringSplitOptions.RemoveEmptyEntries)[0];
            }
        }

        string? envUrls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
        if (!string.IsNullOrWhiteSpace(envUrls)) {
            return envUrls.Split(';', StringSplitOptions.RemoveEmptyEntries)[0];
        }

        string? envPort = Environment.GetEnvironmentVariable("MAPLE_REPACKER_PORT");
        if (!string.IsNullOrWhiteSpace(envPort) && int.TryParse(envPort, out int explicitPort) && explicitPort > 0) {
            return $"http://127.0.0.1:{explicitPort}";
        }

        int port = SelectAvailableLoopbackPort(5189);
        return $"http://127.0.0.1:{port}";
    }

    private static int SelectAvailableLoopbackPort(int preferredPort) {
        try {
            using var preferred = new TcpListener(IPAddress.Loopback, preferredPort);
            preferred.Start();
            preferred.Stop();
            return preferredPort;
        } catch {
            try {
                using var ephemeral = new TcpListener(IPAddress.Loopback, 0);
                ephemeral.Start();
                int port = ((IPEndPoint) ephemeral.LocalEndpoint).Port;
                ephemeral.Stop();
                return port;
            } catch {
                return preferredPort;
            }
        }
    }

    private static string ResolveWebRootPath() {
        string processDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        string[] candidates = [
            Path.Combine(Directory.GetCurrentDirectory(), "wwwroot"),
            Path.Combine(Directory.GetCurrentDirectory(), "Maple.Classic.Repacker.UI", "wwwroot"),
            Path.Combine(processDir, "wwwroot"),
            Path.Combine(AppContext.BaseDirectory, "wwwroot"),
        ];

        foreach (string candidate in candidates) {
            if (Directory.Exists(candidate) && System.IO.File.Exists(Path.Combine(candidate, "index.html"))) {
                return candidate;
            }
        }

        string fallbackDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Maple.Classic.Repacker",
            "wwwroot");
        EnsureEmbeddedWebRootExtracted(fallbackDir);
        if (Directory.Exists(fallbackDir) && System.IO.File.Exists(Path.Combine(fallbackDir, "index.html"))) {
            return fallbackDir;
        }

        return Path.Combine(AppContext.BaseDirectory, "wwwroot");
    }

    private static void EnsureEmbeddedWebRootExtracted(string targetWebRoot) {
        try {
            Assembly asm = typeof(Program).Assembly;
            const string prefix = "wwwroot/";
            foreach (string resourceName in asm.GetManifestResourceNames()) {
                if (!resourceName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) {
                    continue;
                }
                string relativePath = resourceName[prefix.Length..]
                    .Replace('\\', Path.DirectorySeparatorChar)
                    .Replace('/', Path.DirectorySeparatorChar);
                string destinationPath = Path.Combine(targetWebRoot, relativePath);
                string? dir = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrEmpty(dir)) {
                    Directory.CreateDirectory(dir);
                }
                using Stream? stream = asm.GetManifestResourceStream(resourceName);
                if (stream != null) {
                    using FileStream fs = System.IO.File.Create(destinationPath);
                    stream.CopyTo(fs);
                }
            }
        } catch {
            // Ignore extraction failure
        }
    }

    private static string? ResolveCompiledTsWebRootPath(string webRoot) {
        string uiRoot = Path.GetDirectoryName(webRoot) ?? Directory.GetCurrentDirectory();
        string processDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        foreach (string startDir in (ReadOnlySpan<string>) [uiRoot, processDir, AppContext.BaseDirectory, Directory.GetCurrentDirectory()]) {
            string? current = startDir;
            for (int i = 0; i < 6 && !string.IsNullOrEmpty(current); i++) {
                string candidate1 = Path.Combine(current, "obj", "ts", "wwwroot");
                if (System.IO.File.Exists(Path.Combine(candidate1, "app.js"))) {
                    return candidate1;
                }
                string candidate2 = Path.Combine(current, "Maple.Classic.Repacker.UI", "obj", "ts", "wwwroot");
                if (System.IO.File.Exists(Path.Combine(candidate2, "app.js"))) {
                    return candidate2;
                }
                current = Path.GetDirectoryName(current);
            }
        }
        return null;
    }

    private sealed class WsClientConnection(WebSocket socket) : IDisposable {
        public WebSocket Socket { get; } = socket;
        private readonly SemaphoreSlim sendLock = new(1, 1);

        public async Task SendBinaryAsync(ReadOnlyMemory<byte> payload, CancellationToken ct) {
            await sendLock.WaitAsync(ct).ConfigureAwait(false);
            try {
                if (Socket.State == WebSocketState.Open) {
                    await Socket.SendAsync(payload, WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
                }
            } finally {
                sendLock.Release();
            }
        }

        public void Dispose() => sendLock.Dispose();
    }
}
