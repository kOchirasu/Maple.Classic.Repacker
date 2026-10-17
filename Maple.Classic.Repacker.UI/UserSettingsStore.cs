using System.Text.Json;
using System.Text.Json.Serialization;
using Maple.Classic.Repacker.Engine.Models;

namespace Maple.Classic.Repacker.UI;

public sealed record SavedWindowBoundsDto {
    public int Width { get; set; }
    public int Height { get; set; }
    public int? X { get; set; }
    public int? Y { get; set; }
    public bool Maximized { get; set; }
}

public sealed record SavedRepackerLayoutDto {
    public double? TreePaneWidth { get; set; }
    public double? InspectorPaneWidth { get; set; }
    public bool? InspectorCollapsed { get; set; }
    public double? WorkbenchHeight { get; set; }
    public bool? WorkbenchCollapsed { get; set; }
}

public sealed record SavedRepackerUiStateDto {
    public SavedRepackerLayoutDto? Layout { get; set; }
    public RepackerWorkbenchTab? ActiveWorkbenchTab { get; set; }
    public RepackerCanvasBackground? CanvasBackground { get; set; }
    public bool? ShowOriginCrosshair { get; set; }
    public bool? ShowVectorBounds { get; set; }
    public bool? ShowPixelGrid { get; set; }
    public int? DefaultWzVersion { get; set; }
}

public sealed class PersistedRepackerSettings {
    public SavedWindowBoundsDto? MainWindow { get; set; }
    public SavedRepackerUiStateDto UiState { get; set; } = new();
    public List<string> RecentPaths { get; set; } = [];
}

public sealed class UserSettingsStore {
    private static readonly JsonSerializerOptions SerializerOptions = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly Lock syncLock = new();
    private readonly string settingsFilePath;
    private PersistedRepackerSettings settings;

    public UserSettingsStore(string? customPath = null) {
        settingsFilePath = customPath ?? ResolveDefaultSettingsFilePath();
        settings = LoadFromDisk();
    }

    /// <summary>
    /// Settings live next to the executable by default (portable Windows build). Launchers whose install directory is
    /// read-only (e.g. the Electron shell inside a macOS <c>.app</c> or Linux AppImage) set
    /// <c>MAPLE_REPACKER_SETTINGS_PATH</c> to a per-user location instead.
    /// </summary>
    private static string ResolveDefaultSettingsFilePath() {
        string? overridePath = Environment.GetEnvironmentVariable("MAPLE_REPACKER_SETTINGS_PATH");
        if (!string.IsNullOrWhiteSpace(overridePath)) {
            return Path.GetFullPath(overridePath);
        }
        string exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        return Path.Combine(exeDir, "repacker-settings.json");
    }

    private PersistedRepackerSettings LoadFromDisk() {
        try {
            if (System.IO.File.Exists(settingsFilePath)) {
                string json = System.IO.File.ReadAllText(settingsFilePath);
                PersistedRepackerSettings? loaded = JsonSerializer.Deserialize<PersistedRepackerSettings>(json, SerializerOptions);
                if (loaded is not null) {
                    return loaded;
                }
            }
        } catch {
            // Fall back to default settings
        }
        return new PersistedRepackerSettings();
    }

    private void SaveToDiskUnsafe() {
        try {
            string? dir = Path.GetDirectoryName(settingsFilePath);
            if (!string.IsNullOrEmpty(dir)) {
                Directory.CreateDirectory(dir);
            }
            string json = JsonSerializer.Serialize(settings, SerializerOptions);
            System.IO.File.WriteAllText(settingsFilePath, json);
        } catch {
            // Ignore write errors in read-only directories
        }
    }

    public SavedWindowBoundsDto? GetWindowBounds() {
        lock (syncLock) {
            if (settings.MainWindow is { Width: >= 400, Height: >= 300 } bounds) {
                return bounds with { };
            }
            return null;
        }
    }

    public void SaveWindowBounds(int width, int height, int? x, int? y, bool maximized) {
        lock (syncLock) {
            settings.MainWindow = new SavedWindowBoundsDto {
                Width = width,
                Height = height,
                X = x,
                Y = y,
                Maximized = maximized,
            };
            SaveToDiskUnsafe();
        }
    }

    public SavedRepackerUiStateDto GetUiState() {
        lock (syncLock) {
            return settings.UiState with {
                Layout = settings.UiState.Layout is null ? null : settings.UiState.Layout with { },
            };
        }
    }

    public void UpdateUiState(SavedRepackerUiStateDto patch) {
        lock (syncLock) {
            SavedRepackerUiStateDto cur = settings.UiState;
            SavedRepackerLayoutDto? mergedLayout = cur.Layout;
            if (patch.Layout is not null) {
                SavedRepackerLayoutDto baseLayout = cur.Layout ?? new SavedRepackerLayoutDto();
                mergedLayout = baseLayout with {
                    TreePaneWidth = patch.Layout.TreePaneWidth ?? baseLayout.TreePaneWidth,
                    InspectorPaneWidth = patch.Layout.InspectorPaneWidth ?? baseLayout.InspectorPaneWidth,
                    InspectorCollapsed = patch.Layout.InspectorCollapsed ?? baseLayout.InspectorCollapsed,
                    WorkbenchHeight = patch.Layout.WorkbenchHeight ?? baseLayout.WorkbenchHeight,
                    WorkbenchCollapsed = patch.Layout.WorkbenchCollapsed ?? baseLayout.WorkbenchCollapsed,
                };
            }

            settings.UiState = cur with {
                Layout = mergedLayout,
                ActiveWorkbenchTab = patch.ActiveWorkbenchTab ?? cur.ActiveWorkbenchTab,
                CanvasBackground = patch.CanvasBackground ?? cur.CanvasBackground,
                ShowOriginCrosshair = patch.ShowOriginCrosshair ?? cur.ShowOriginCrosshair,
                ShowVectorBounds = patch.ShowVectorBounds ?? cur.ShowVectorBounds,
                ShowPixelGrid = patch.ShowPixelGrid ?? cur.ShowPixelGrid,
                DefaultWzVersion = patch.DefaultWzVersion ?? cur.DefaultWzVersion,
            };
            SaveToDiskUnsafe();
        }
    }

    public string[] GetRecentPaths() {
        lock (syncLock) {
            return [.. settings.RecentPaths];
        }
    }

    public void RecordRecentPath(string path) {
        if (string.IsNullOrWhiteSpace(path)) return;
        string clean = path.Trim();
        lock (syncLock) {
            settings.RecentPaths.RemoveAll(p => string.Equals(p, clean, StringComparison.OrdinalIgnoreCase));
            settings.RecentPaths.Insert(0, clean);
            if (settings.RecentPaths.Count > 12) {
                settings.RecentPaths.RemoveRange(12, settings.RecentPaths.Count - 12);
            }
            SaveToDiskUnsafe();
        }
    }
}
