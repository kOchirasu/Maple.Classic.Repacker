# Maple.Classic.Repacker.UI

Desktop and web UI for `Maple.Classic.Repacker`, using the same hybrid architecture as `Maple.PE.UI` (ASP.NET Core loopback host + MessagePack over WebSockets RPC + WebView2 / Electron shell).

## Architecture

- **`Program.cs`**: Loopback Kestrel host serving static and embedded frontend assets (`wwwroot/`), `/api/export` binary downloads, and the `/ws` MessagePack WebSocket endpoint.
- **`WindowsDesktopShell.cs`**: Native Windows Forms + WebView2 desktop shell with dark title bar support and persistent window geometry (`repacker-settings.json`).
- **`electron/main.ts`**: Cross-platform Electron launcher that spawns the local `.NET` backend in `--headless` mode and hosts the studio UI inside an Electron `BrowserWindow`.
- **`wwwroot/`**:
  - `rpc.ts`: Binary MessagePack WebSocket client and TypeScript DTO contracts.
  - `layout.ts`: Resizable 3-pane + bottom workbench splitter controller.
  - `app.ts`: WZ tree explorer, interactive HTML5 canvas & animation player, `Sound_DX8` waveform player, property inspector, CodeMirror 6 JSON editor, global search, and hex/binary data inspector.

## Building & Publishing

### Run Locally
```bash
dotnet run --project Maple.Classic.Repacker.UI.csproj
```

### Publish Standalone Single-File Executable (`Maple.Classic.Repacker.UI.exe`)
`Maple.Classic.Repacker.UI.csproj` uses `PublishSingleFile` + `Costura.Fody` (`FodyWeavers.xml`) and embedded manifest resources (`wwwroot/**`) to bundle all managed dependencies, `WebView2Loader.dll`, and compiled TypeScript/CodeMirror assets into a single standalone `.exe`:
```bash
dotnet publish Maple.Classic.Repacker.UI.csproj -c Release -r win-x64 -o ../dist/win-x64
```

## Keyboard Shortcuts

| Shortcut | Action |
| :--- | :--- |
| `Ctrl+O` | Open `.wz` file from disk or browser upload |
| `Ctrl+Shift+S` | Repack and save active `.wz` archive |
| `Ctrl+F` | Focus Global WZ Search |
| `Ctrl+S` | Apply JSON edits in the CodeMirror 6 JSON editor |
| `Ctrl+Z` / `Ctrl+Y` | Undo / Redo edit in the current `.img` |
| `Ctrl+C` / `Ctrl+V` | Copy / Paste selected `.img` or property node |
| `Ctrl+D` | Duplicate selected `.img` or property node |
| `Insert` | Add new property to selected `.img` or container |
| `F2` | Rename selected node |
| `Delete` | Delete selected node |
