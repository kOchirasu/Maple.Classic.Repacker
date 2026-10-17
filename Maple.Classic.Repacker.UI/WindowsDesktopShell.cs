#if WINDOWS
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Maple.Classic.Repacker.Engine.Workspace;
using Microsoft.AspNetCore.Builder;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Maple.Classic.Repacker.UI;

internal static class WindowsDesktopShell {
    public static void Run(
        WebApplication app,
        RepackerWorkspace workspace,
        UserSettingsStore settingsStore,
        string listenUrl,
        CancellationTokenSource broadcastCts) {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        app.StartAsync().GetAwaiter().GetResult();
        try {
            using var form = new RepackerDesktopForm(listenUrl, settingsStore);
            Application.Run(form);
        } finally {
            try {
                broadcastCts.Cancel();
            } catch {
                // Ignore cancellation errors during shutdown
            }
            try {
                using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                app.StopAsync(stopCts.Token).GetAwaiter().GetResult();
            } catch {
                // Ignore shutdown errors
            }
            try {
                workspace.Dispose();
            } catch {
                // Ignore disposal errors
            }
            try {
                app.DisposeAsync().AsTask().GetAwaiter().GetResult();
            } catch {
                // Ignore host disposal errors
            }
        }
    }
}

internal sealed class RepackerDesktopForm : Form {
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    private readonly string backendUrl;
    private readonly UserSettingsStore settingsStore;
    private readonly WebView2 webView;
    private readonly Label loadingLabel;
    private Rectangle lastNormalBounds;

    public RepackerDesktopForm(
        string backendUrl,
        UserSettingsStore settingsStore,
        int width = 1580,
        int height = 940,
        int minWidth = 1120,
        int minHeight = 700,
        string initialTitle = "Maple.Classic.Repacker // WZ & IMG Studio") {
        this.backendUrl = backendUrl;
        this.settingsStore = settingsStore;

        Text = initialTitle;
        MinimumSize = new Size(minWidth, minHeight);

        SavedWindowBoundsDto? savedBounds = settingsStore.GetWindowBounds();
        int initialWidth = savedBounds != null && savedBounds.Width >= minWidth ? savedBounds.Width : width;
        int initialHeight = savedBounds != null && savedBounds.Height >= minHeight ? savedBounds.Height : height;
        Size = new Size(initialWidth, initialHeight);

        if (savedBounds is { X: int savedX, Y: int savedY } &&
            IsVisibleOnAnyScreen(new Rectangle(savedX, savedY, initialWidth, initialHeight))) {
            StartPosition = FormStartPosition.Manual;
            Location = new Point(savedX, savedY);
        } else {
            StartPosition = FormStartPosition.CenterScreen;
        }

        lastNormalBounds = new Rectangle(Location, Size);

        if (savedBounds?.Maximized == true) {
            WindowState = FormWindowState.Maximized;
        }

        BackColor = Color.FromArgb(24, 26, 31);
        ForeColor = Color.FromArgb(212, 212, 212);

        loadingLabel = new Label {
            Text = $"Starting Maple.Classic.Repacker ({backendUrl})...",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            BackColor = Color.FromArgb(24, 26, 31),
            ForeColor = Color.FromArgb(157, 157, 157),
            Font = new Font("Consolas", 11f, FontStyle.Regular),
        };

        webView = new WebView2 {
            Dock = DockStyle.Fill,
            DefaultBackgroundColor = Color.FromArgb(255, 24, 26, 31),
            Visible = false,
        };

        Controls.Add(webView);
        Controls.Add(loadingLabel);

        Load += OnFormLoadAsync;
        ResizeEnd += (_, _) => CaptureAndSaveWindowBounds();
        LocationChanged += (_, _) => {
            if (WindowState == FormWindowState.Normal) {
                lastNormalBounds = Bounds;
            }
        };
        SizeChanged += (_, _) => {
            if (WindowState == FormWindowState.Normal) {
                lastNormalBounds = Bounds;
            }
        };
        FormClosing += (_, _) => CaptureAndSaveWindowBounds();
    }

    private static bool IsVisibleOnAnyScreen(Rectangle rect) {
        foreach (Screen screen in Screen.AllScreens) {
            Rectangle intersect = Rectangle.Intersect(screen.WorkingArea, rect);
            if (intersect is { Width: >= 80, Height: >= 60 }) {
                return true;
            }
        }
        return false;
    }

    private void CaptureAndSaveWindowBounds() {
        try {
            Rectangle normal = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            if (normal.Width < MinimumSize.Width || normal.Height < MinimumSize.Height) {
                normal = lastNormalBounds;
            }
            if (normal.Width >= MinimumSize.Width && normal.Height >= MinimumSize.Height) {
                settingsStore.SaveWindowBounds(
                    normal.Width,
                    normal.Height,
                    normal.X,
                    normal.Y,
                    WindowState == FormWindowState.Maximized);
            }
        } catch {
            // Ignore persistence errors during window resize/close
        }
    }

    protected override void OnHandleCreated(EventArgs e) {
        base.OnHandleCreated(e);
        TryEnableDarkTitleBar(Handle);
    }

    private async void OnFormLoadAsync(object? sender, EventArgs e) {
        try {
            string userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Maple.Classic.Repacker",
                "WebView2");
            Directory.CreateDirectory(userDataFolder);

            CoreWebView2Environment env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
            await webView.EnsureCoreWebView2Async(env);

            CoreWebView2Settings settings = webView.CoreWebView2.Settings;
            settings.AreDefaultContextMenusEnabled = false;
            settings.IsStatusBarEnabled = false;
            settings.AreDevToolsEnabled = true;

            webView.CoreWebView2.NavigationCompleted += (_, _) => {
                if (!IsDisposed) {
                    webView.Visible = true;
                    loadingLabel.Visible = false;
                }
            };
            webView.CoreWebView2.DocumentTitleChanged += (_, _) => {
                if (!IsDisposed && !string.IsNullOrWhiteSpace(webView.CoreWebView2.DocumentTitle)) {
                    Text = webView.CoreWebView2.DocumentTitle;
                }
            };

            _ = webView.CoreWebView2.CallDevToolsProtocolMethodAsync("Network.enable", "{}");
            _ = webView.CoreWebView2.CallDevToolsProtocolMethodAsync("Network.setCacheDisabled", "{\"cacheDisabled\":true}");
            webView.CoreWebView2.Navigate(backendUrl);
        } catch (Exception ex) {
            loadingLabel.Text =
                "Microsoft Edge WebView2 Runtime is required to render the native window.\r\n" +
                $"Backend is running on {backendUrl}\r\n\r\n" +
                $"Details: {ex.Message}";
        }
    }

    private static void TryEnableDarkTitleBar(IntPtr handle) {
        if (handle == IntPtr.Zero) return;
        try {
            int enabled = 1;
            if (DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref enabled, sizeof(int)) != 0) {
                _ = DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref enabled, sizeof(int));
            }
        } catch {
            // Ignore on older OS versions
        }
    }
}
#endif
