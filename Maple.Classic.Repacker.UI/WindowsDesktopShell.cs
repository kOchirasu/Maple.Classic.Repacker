#if WINDOWS
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;
using Maple.Classic.Repacker.Engine.Models;
using Maple.Classic.Repacker.Engine.Workspace;
using Microsoft.AspNetCore.Builder;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Maple.Classic.Repacker.UI;

internal static class WindowsDesktopShell {
    internal static RepackerDesktopForm? ActiveForm { get; private set; }

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
            using var form = new RepackerDesktopForm(listenUrl, workspace, settingsStore);
            ActiveForm = form;
            Application.Run(form);
        } finally {
            ActiveForm = null;
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

    public static string[]? PickOpenWzFiles(string? initialDirectory = null) {
        return RunOnStaThread<string[]?>(owner => {
            using var dlg = new OpenFileDialog {
                Title = "Open MapleStory WZ Archive",
                Filter = "MapleStory WZ Archives (*.wz)|*.wz|All Files (*.*)|*.*",
                Multiselect = true,
                CheckFileExists = true,
            };
            string? validDir = ResolveValidDirectory(initialDirectory);
            if (!string.IsNullOrEmpty(validDir)) {
                dlg.InitialDirectory = validDir;
            }
            DialogResult result = owner is not null ? dlg.ShowDialog(owner) : dlg.ShowDialog();
            return result == DialogResult.OK && dlg.FileNames.Length > 0 ? dlg.FileNames : null;
        });
    }

    public static string? PickOpenDirectory(string? initialDirectory = null) {
        return RunOnStaThread<string?>(owner => {
            using var dlg = new FolderBrowserDialog {
                Description = "Select MapleStory Data Directory",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = false,
            };
            string? validDir = ResolveValidDirectory(initialDirectory);
            if (!string.IsNullOrEmpty(validDir)) {
                dlg.InitialDirectory = validDir;
            }
            DialogResult result = owner is not null ? dlg.ShowDialog(owner) : dlg.ShowDialog();
            return result == DialogResult.OK && !string.IsNullOrWhiteSpace(dlg.SelectedPath) ? dlg.SelectedPath : null;
        });
    }

    public static string? PickSaveWzFile(string? defaultFileName = null, string? initialDirectory = null) {
        return RunOnStaThread<string?>(owner => {
            using var dlg = new SaveFileDialog {
                Title = "Save MapleStory WZ Archive",
                Filter = "MapleStory WZ Archives (*.wz)|*.wz|All Files (*.*)|*.*",
                DefaultExt = "wz",
                AddExtension = true,
                OverwritePrompt = true,
                FileName = string.IsNullOrWhiteSpace(defaultFileName) ? "Archive.wz" : defaultFileName,
            };
            string? validDir = ResolveValidDirectory(initialDirectory);
            if (!string.IsNullOrEmpty(validDir)) {
                dlg.InitialDirectory = validDir;
            }
            DialogResult result = owner is not null ? dlg.ShowDialog(owner) : dlg.ShowDialog();
            return result == DialogResult.OK && !string.IsNullOrWhiteSpace(dlg.FileName) ? dlg.FileName : null;
        });
    }

    private static string? ResolveValidDirectory(string? candidatePath) {
        if (string.IsNullOrWhiteSpace(candidatePath)) {
            return null;
        }
        try {
            if (Directory.Exists(candidatePath)) {
                return Path.GetFullPath(candidatePath);
            }
            string? parent = Path.GetDirectoryName(candidatePath);
            if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent)) {
                return Path.GetFullPath(parent);
            }
        } catch {
            // Ignore invalid paths
        }
        return null;
    }

    private static T? RunOnStaThread<T>(Func<IWin32Window?, T?> dialogAction) {
        RepackerDesktopForm? form = ActiveForm;
        if (form is { IsDisposed: false, IsHandleCreated: true }) {
            if (form.InvokeRequired) {
                return form.Invoke(() => dialogAction(form));
            }
            return dialogAction(form);
        }

        T? result = default;
        var thread = new Thread(() => {
            try {
                result = dialogAction(null);
            } catch {
                result = default;
            }
        }) {
            IsBackground = true,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return result;
    }
}

internal sealed class RepackerDesktopForm : Form {
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    private readonly string backendUrl;
    private readonly RepackerWorkspace workspace;
    private readonly UserSettingsStore settingsStore;
    private readonly WebView2 webView;
    private readonly Label loadingLabel;
    private readonly ContextMenuStrip treeContextMenu;
    private readonly ToolStripMenuItem menuAddProp;
    private readonly ToolStripMenuItem menuCopy;
    private readonly ToolStripMenuItem menuDuplicate;
    private readonly ToolStripMenuItem menuRename;
    private readonly ToolStripMenuItem menuExportAuto;
    private readonly ToolStripMenuItem menuRestore;
    private readonly ToolStripMenuItem menuDelete;
    private readonly ToolStripMenuItem menuCloseArchive;
    private string contextTargetArchiveId = string.Empty;
    private string contextTargetPath = string.Empty;
    private Rectangle lastNormalBounds;
    private bool allowImmediateClose;

    public RepackerDesktopForm(
        string backendUrl,
        RepackerWorkspace workspace,
        UserSettingsStore settingsStore,
        int width = 1580,
        int height = 940,
        int minWidth = 1120,
        int minHeight = 700,
        string initialTitle = "Maple.Classic.Repacker // WZ & IMG Studio") {
        this.backendUrl = backendUrl;
        this.workspace = workspace;
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

        BackColor = Color.FromArgb(30, 30, 30);
        ForeColor = Color.FromArgb(212, 212, 212);

        loadingLabel = new Label {
            Text = $"Starting Maple.Classic.Repacker ({backendUrl})...",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            BackColor = Color.FromArgb(30, 30, 30),
            ForeColor = Color.FromArgb(157, 157, 157),
            Font = new Font("Consolas", 11f, FontStyle.Regular),
        };

        webView = new WebView2 {
            Dock = DockStyle.Fill,
            DefaultBackgroundColor = Color.FromArgb(255, 30, 30, 30),
            Visible = false,
        };

        treeContextMenu = new ContextMenuStrip {
            ShowImageMargin = false,
            ShowCheckMargin = false,
            BackColor = Color.FromArgb(45, 45, 48),
            ForeColor = Color.FromArgb(212, 212, 212),
            Font = new Font("Segoe UI", 9.25f, FontStyle.Regular),
            Padding = new Padding(4),
            Renderer = new DarkContextMenuRenderer(),
        };

        menuAddProp = CreateTreeContextMenuItem("+ Add Child Property...", "addProp");
        menuCopy = CreateTreeContextMenuItem("Copy", "copy", "Ctrl+C");
        menuDuplicate = CreateTreeContextMenuItem("Duplicate", "duplicate", "Ctrl+D");
        menuRename = CreateTreeContextMenuItem("Rename", "rename", "F2");
        menuExportAuto = CreateTreeContextMenuItem("Export Node...", "exportAuto");
        menuRestore = CreateTreeContextMenuItem("Restore Node", "restore", style: ContextMenuItemStyle.Success);
        menuDelete = CreateTreeContextMenuItem("Delete Node", "delete", "Del", ContextMenuItemStyle.Danger);
        menuCloseArchive = CreateTreeContextMenuItem("Close Archive", "closeArchive", "Del", ContextMenuItemStyle.Danger);

        treeContextMenu.Items.AddRange([
            CreateTreeContextMenuItem("Inspect Node", "inspect"),
            new ToolStripSeparator(),
            menuAddProp,
            CreateTreeContextMenuItem("+ Add .img Image...", "addImg"),
            CreateTreeContextMenuItem("+ Add Directory...", "addDir"),
            new ToolStripSeparator(),
            menuCopy,
            CreateTreeContextMenuItem("Paste", "paste", "Ctrl+V"),
            menuDuplicate,
            menuRename,
            new ToolStripSeparator(),
            CreateTreeContextMenuItem("Inline _outlink / _inlink Sprites", "inlineLinks"),
            menuExportAuto,
            new ToolStripSeparator(),
            menuRestore,
            menuDelete,
            menuCloseArchive,
        ]);

        Controls.Add(webView);
        Controls.Add(loadingLabel);

        Load += OnFormLoadAsync;
        Deactivate += (_, _) => CloseNativeContextMenu();
        ResizeEnd += (_, _) => CaptureAndSaveWindowBounds();
        LocationChanged += (_, _) => {
            CloseNativeContextMenu();
            if (WindowState == FormWindowState.Normal) {
                lastNormalBounds = Bounds;
            }
        };
        SizeChanged += (_, _) => {
            CloseNativeContextMenu();
            if (WindowState == FormWindowState.Normal) {
                lastNormalBounds = Bounds;
            }
        };
        FormClosing += OnFormClosing;
        Disposed += (_, _) => treeContextMenu.Dispose();
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e) {
        CaptureAndSaveWindowBounds();
        if (allowImmediateClose) {
            return;
        }

        ArchiveSummaryDto[] dirtyArchives = workspace
            .GetArchiveSummaries()
            .Where(a => a.ChangeState is RepackerChangeState.Modified or RepackerChangeState.Added)
            .ToArray();
        if (dirtyArchives.Length == 0) {
            return;
        }

        if (webView.Visible && webView.CoreWebView2 is not null) {
            e.Cancel = true;
            CloseNativeContextMenu();
            string payload = JsonSerializer.Serialize(new {
                type = "requestAppClose",
            });
            webView.CoreWebView2.PostWebMessageAsJson(payload);
            return;
        }

        string fileList = string.Join(Environment.NewLine, dirtyArchives.Select(a => $"• {a.Name}"));
        DialogResult choice = MessageBox.Show(
            this,
            $"The following WZ archive(s) have unsaved changes:{Environment.NewLine}{Environment.NewLine}{fileList}{Environment.NewLine}{Environment.NewLine}Do you want to save your changes before closing?",
            "Unsaved Changes",
            MessageBoxButtons.YesNoCancel,
            MessageBoxIcon.Warning);

        if (choice == DialogResult.Cancel) {
            e.Cancel = true;
            return;
        }

        if (choice == DialogResult.Yes) {
            foreach (ArchiveSummaryDto ar in dirtyArchives) {
                string? targetPath = ar.FilePath;
                if (string.IsNullOrWhiteSpace(targetPath)) {
                    targetPath = WindowsDesktopShell.PickSaveWzFile(
                        ar.Name,
                        settingsStore.GetRecentPaths().FirstOrDefault());
                    if (string.IsNullOrWhiteSpace(targetPath)) {
                        e.Cancel = true;
                        return;
                    }
                }

                (bool ok, string? err, ArchiveSummaryDto? summary) = workspace.SaveArchiveToDisk(ar.ArchiveId, targetPath, ar.Version);
                if (!ok) {
                    MessageBox.Show(
                        this,
                        $"Failed to save \"{ar.Name}\": {err}",
                        "Save Error",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                    e.Cancel = true;
                    return;
                }
                if (summary is not null && !string.IsNullOrEmpty(summary.FilePath)) {
                    settingsStore.RecordRecentPath(summary.FilePath);
                }
            }
        }
    }

    private enum ContextMenuItemStyle {
        Default,
        Danger,
        Success,
    }

    private sealed record ContextMenuItemMetadata(string CommandId, ContextMenuItemStyle Style);

    private ToolStripMenuItem CreateTreeContextMenuItem(
        string text,
        string commandId,
        string? shortcutDisplay = null,
        ContextMenuItemStyle style = ContextMenuItemStyle.Default) {
        Color foreColor = style switch {
            ContextMenuItemStyle.Danger => Color.FromArgb(248, 113, 113),
            ContextMenuItemStyle.Success => Color.FromArgb(74, 222, 128),
            _ => Color.FromArgb(212, 212, 212),
        };
        var item = new ToolStripMenuItem(text) {
            Tag = new ContextMenuItemMetadata(commandId, style),
            ShortcutKeyDisplayString = shortcutDisplay ?? string.Empty,
            Padding = new Padding(4, 3, 4, 3),
            ForeColor = foreColor,
        };
        item.Click += (_, _) => {
            if (IsDisposed || webView.CoreWebView2 == null) {
                return;
            }
            string payload = JsonSerializer.Serialize(new {
                type = "treeContextMenuCommand",
                cmd = commandId,
                archiveId = contextTargetArchiveId,
                path = contextTargetPath,
            });
            webView.CoreWebView2.PostWebMessageAsJson(payload);
        };
        return item;
    }

    private void CloseNativeContextMenu() {
        if (!IsDisposed && treeContextMenu.Visible) {
            treeContextMenu.Close();
        }
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
            webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

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

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e) {
        if (IsDisposed) {
            return;
        }
        try {
            string? raw = e.TryGetWebMessageAsString();
            if (string.IsNullOrWhiteSpace(raw)) {
                return;
            }
            using JsonDocument doc = JsonDocument.Parse(raw);
            JsonElement root = doc.RootElement;
            if (!root.TryGetProperty("type", out JsonElement typeEl)) {
                return;
            }
            string? msgType = typeEl.GetString();
            if (string.Equals(msgType, "openTreeContextMenu", StringComparison.Ordinal)) {
                contextTargetArchiveId = root.TryGetProperty("archiveId", out JsonElement arEl)
                    ? arEl.GetString() ?? string.Empty
                    : string.Empty;
                contextTargetPath = root.TryGetProperty("path", out JsonElement pathEl)
                    ? pathEl.GetString() ?? string.Empty
                    : string.Empty;
                bool isDeleted = (root.TryGetProperty("changeState", out JsonElement csEl) &&
                                  string.Equals(csEl.GetString(), nameof(RepackerChangeState.Deleted), StringComparison.OrdinalIgnoreCase)) ||
                                 (root.TryGetProperty("isDeleted", out JsonElement delEl) &&
                                  delEl.ValueKind is JsonValueKind.True);
                bool isArchiveRoot = string.IsNullOrEmpty(contextTargetPath);

                menuAddProp.Visible = !isArchiveRoot && !isDeleted;
                menuCopy.Visible = !isArchiveRoot && !isDeleted;
                menuDuplicate.Visible = !isArchiveRoot && !isDeleted;
                menuRename.Visible = !isArchiveRoot && !isDeleted;
                menuExportAuto.Text = isArchiveRoot ? "Export .wz Archive..." : "Export Node...";
                menuRestore.Visible = !isArchiveRoot && isDeleted;
                menuDelete.Visible = !isArchiveRoot && !isDeleted;
                menuCloseArchive.Visible = isArchiveRoot;

                treeContextMenu.Show(Cursor.Position);
            } else if (string.Equals(msgType, "closeTreeContextMenu", StringComparison.Ordinal)) {
                CloseNativeContextMenu();
            } else if (string.Equals(msgType, "confirmAppClose", StringComparison.Ordinal)) {
                allowImmediateClose = true;
                BeginInvoke(Close);
            }
        } catch {
            // Ignore malformed web messages
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

    private sealed class DarkContextMenuRenderer() : ToolStripProfessionalRenderer(new DarkContextMenuColorTable()) {
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) {
            using var brush = new SolidBrush(Color.FromArgb(45, 45, 48));
            e.Graphics.FillRectangle(brush, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e) {
            using var pen = new Pen(Color.FromArgb(0, 122, 204));
            var rect = new Rectangle(0, 0, e.AffectedBounds.Width - 1, e.AffectedBounds.Height - 1);
            e.Graphics.DrawRectangle(pen, rect);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e) {
            if (!e.Item.Selected) {
                return;
            }
            ContextMenuItemStyle style = (e.Item.Tag as ContextMenuItemMetadata)?.Style ?? ContextMenuItemStyle.Default;
            var rect = new Rectangle(2, 1, Math.Max(0, e.Item.Width - 4), Math.Max(0, e.Item.Height - 2));
            Color fill = style switch {
                ContextMenuItemStyle.Danger => Color.FromArgb(78, 36, 38),
                ContextMenuItemStyle.Success => Color.FromArgb(26, 68, 44),
                _ => Color.FromArgb(9, 71, 113),
            };
            using var brush = new SolidBrush(fill);
            e.Graphics.FillRectangle(brush, rect);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e) {
            ContextMenuItemStyle style = (e.Item.Tag as ContextMenuItemMetadata)?.Style ?? ContextMenuItemStyle.Default;
            var menuItem = e.Item as ToolStripMenuItem;
            bool isShortcutText = menuItem != null &&
                                  !string.IsNullOrEmpty(menuItem.ShortcutKeyDisplayString) &&
                                  string.Equals(e.Text, menuItem.ShortcutKeyDisplayString, StringComparison.Ordinal);

            e.TextColor = style switch {
                ContextMenuItemStyle.Danger => Color.FromArgb(248, 113, 113),
                ContextMenuItemStyle.Success => Color.FromArgb(74, 222, 128),
                _ when isShortcutText => e.Item.Selected ? Color.FromArgb(212, 212, 212) : Color.FromArgb(140, 140, 140),
                _ => e.Item.Selected ? Color.White : Color.FromArgb(212, 212, 212),
            };
            base.OnRenderItemText(e);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e) {
            int y = e.Item.Height / 2;
            using var pen = new Pen(Color.FromArgb(62, 62, 66));
            e.Graphics.DrawLine(pen, 6, y, e.Item.Width - 6, y);
        }
    }

    private sealed class DarkContextMenuColorTable : ProfessionalColorTable {
        public override Color ToolStripDropDownBackground => Color.FromArgb(45, 45, 48);
        public override Color MenuBorder => Color.FromArgb(0, 122, 204);
        public override Color MenuItemBorder => Color.Transparent;
        public override Color MenuItemSelected => Color.FromArgb(9, 71, 113);
        public override Color ImageMarginGradientBegin => Color.FromArgb(45, 45, 48);
        public override Color ImageMarginGradientMiddle => Color.FromArgb(45, 45, 48);
        public override Color ImageMarginGradientEnd => Color.FromArgb(45, 45, 48);
        public override Color SeparatorDark => Color.FromArgb(62, 62, 66);
        public override Color SeparatorLight => Color.Transparent;
    }
}
#endif
