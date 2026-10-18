using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Threading.Channels;
using Maple.Classic.Repacker.Engine.Models;
using Maple.Classic.Repacker.Engine.Workspace;
using Maple.Classic.Repacker.UI.Rpc;

namespace Maple.Classic.Repacker.UI;

public class Program {
    /// <summary>Switches handled by this program; they take no value, so keep them away from ASP.NET's
    /// "--key value" command-line parser, which would otherwise swallow the next argument (e.g. "--urls").</summary>
    private static readonly HashSet<string> ShellSwitches = new(StringComparer.OrdinalIgnoreCase) { "--headless", "--server" };

    [STAThread]
    public static void Main(string[] args) {
        string webRoot = ResolveWebRootPath();
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions {
            Args = args.Where(a => !ShellSwitches.Contains(a)).ToArray(),
            ContentRootPath = Path.GetDirectoryName(webRoot) ?? AppContext.BaseDirectory,
            WebRootPath = webRoot,
        });

        // ResolveListenUrl already honours --urls and ASPNETCORE_URLS, then falls back to a free loopback port.
        string listenUrl = ResolveListenUrl(args);
        builder.WebHost.UseUrls(listenUrl);

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

        var rpcContext = new RpcContext(workspace, settingsStore);
        var rpcRouter = new RpcRouter(rpcContext);

        WebApplication app = builder.Build();
        StopWhenParentProcessExits(app);
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
            string formatRaw = ctx.Request.Query["format"].ToString();
            RepackerExportFormat parsedFormat = Enum.TryParse<RepackerExportFormat>(formatRaw, ignoreCase: true, out RepackerExportFormat f)
                ? f
                : RepackerExportFormat.Auto;
            (string? error, string fileName, string mimeType, byte[] data) =
                workspace.ExportNode(archiveId, path, parsedFormat);
            return error is not null
                ? Results.BadRequest(new { ok = false, error })
                : Results.File(data, mimeType, fileName);
        });

        app.UseWebSockets();

        var wsClients = new ConcurrentDictionary<Guid, WsClientConnection>();

        using var broadcastCts = new CancellationTokenSource();
        _ = Task.Run(async () => {
            long lastSentVersion = 0;
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
            try {
                while (await timer.WaitForNextTickAsync(broadcastCts.Token).ConfigureAwait(false)) {
                    long versionSnapshot = workspace.StateVersion;
                    if (versionSnapshot == lastSentVersion) {
                        continue;
                    }
                    foreach ((Guid id, WsClientConnection conn) in wsClients) {
                        if (conn.Socket.State == WebSocketState.Open) {
                            conn.QueueStatusDelta();
                        } else {
                            wsClients.TryRemove(id, out _);
                        }
                    }
                    lastSentVersion = versionSnapshot;
                }
            } catch (OperationCanceledException) {
                // Shutting down
            }
        }, broadcastCts.Token);

        app.Map("/ws", async ctx => {
            if (!ctx.WebSockets.IsWebSocketRequest) {
                ctx.Response.StatusCode = 400;
                return;
            }
            using WebSocket ws = await ctx.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            await using var conn = new WsClientConnection(ws, rpcContext, ctx.RequestAborted);
            var id = Guid.NewGuid();

            try {
                // First status for a new connection is a full snapshot
                conn.QueueStatusDelta();
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
                    _ = Task.Run(() => {
                        try {
                            (long rpcId, string method, ReadOnlyMemory<byte> rawParams) = RpcSerializer.ReadRequestFrame(msgBytes);
                            if (rpcId > 0 && !string.IsNullOrWhiteSpace(method)) {
                                (bool ok, object? data) = rpcRouter.Dispatch(method, rawParams);
                                byte[] replyBytes = RpcSerializer.Encode(new RpcResponseEnvelope("rpc", rpcId, ok, data));
                                // Push this operation's state changes first, so the client is up to date when the reply resolves
                                conn.QueueStatusDelta();
                                conn.QueueSend(replyBytes);
                            }
                        } catch {
                            // Ignore malformed RPC frame or disconnected socket
                        }
                        return Task.CompletedTask;
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

    /// <summary>
    /// When hosted by the Electron shell (which sets <c>MAPLE_REPACKER_PARENT_PID</c>), shut down if that shell exits
    /// without stopping us (e.g. it crashed), so no orphaned backend keeps running.
    /// </summary>
    private static void StopWhenParentProcessExits(WebApplication app) {
        if (!int.TryParse(Environment.GetEnvironmentVariable("MAPLE_REPACKER_PARENT_PID"), out int parentPid)) {
            return;
        }
        Process parent;
        try {
            parent = Process.GetProcessById(parentPid);
        } catch (ArgumentException) {
            Environment.Exit(0); // Parent is already gone
            return;
        }
        _ = Task.Run(async () => {
            await parent.WaitForExitAsync().ConfigureAwait(false);
            await app.StopAsync().ConfigureAwait(false);
        });
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

    /// <summary>
    /// One WebSocket client. Every outgoing message goes through a single-reader channel drained by one send loop, so
    /// sends never overlap (WebSocket allows one send at a time), messages keep their queue order, and the status
    /// tracker is only ever touched by that loop.
    /// </summary>
    private sealed class WsClientConnection : IAsyncDisposable {
        /// <summary>A queued send: a ready payload, or (with no payload) "send whatever status changed since the last one".</summary>
        private readonly record struct OutgoingMessage(byte[]? Payload) {
            public static OutgoingMessage StatusDelta => new(null);
        }

        private readonly Channel<OutgoingMessage> outbox = Channel.CreateUnbounded<OutgoingMessage>(new UnboundedChannelOptions { SingleReader = true });
        private readonly StatusDeltaTracker statusTracker = new();
        private readonly RpcContext rpcContext;
        private readonly Task sendLoop;

        public WsClientConnection(WebSocket socket, RpcContext rpcContext, CancellationToken connectionAborted) {
            Socket = socket;
            this.rpcContext = rpcContext;
            sendLoop = RunSendLoopAsync(connectionAborted);
        }

        public WebSocket Socket { get; }

        public void QueueSend(byte[] payload) => outbox.Writer.TryWrite(new OutgoingMessage(payload));

        /// <summary>Queues this client's status delta; it is computed when sent, so it reflects everything queued before it.</summary>
        public void QueueStatusDelta() => outbox.Writer.TryWrite(OutgoingMessage.StatusDelta);

        private async Task RunSendLoopAsync(CancellationToken ct) {
            try {
                await foreach (OutgoingMessage message in outbox.Reader.ReadAllAsync(ct).ConfigureAwait(false)) {
                    byte[]? payload = message.Payload
                        ?? (statusTracker.NextDelta(rpcContext) is { } delta ? RpcSerializer.Encode(delta) : null);
                    if (payload is not null && Socket.State == WebSocketState.Open) {
                        await Socket.SendAsync(payload, WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
                    }
                }
            } catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException) {
                // Client went away; the receive loop notices and disposes the connection
            }
        }

        public async ValueTask DisposeAsync() {
            outbox.Writer.TryComplete();
            await sendLoop.ConfigureAwait(false);
        }
    }
}
