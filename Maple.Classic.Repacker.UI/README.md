# Maple.Classic.Repacker.UI

Desktop and web UI for `Maple.Classic.Repacker`: an ASP.NET Core loopback host + MessagePack-over-WebSocket RPC, shown in a desktop shell (WebView2 on Windows, Electron on Linux/macOS) or a plain browser.

## Architecture

- **`Program.cs`**: Loopback Kestrel host serving static and embedded frontend assets (`wwwroot/`), `/api/export` binary downloads, and the `/ws` MessagePack WebSocket endpoint.
- **`Rpc/`**: `RpcMethod` enum, `RpcRouter`, and feature-grouped handlers in `Rpc/Handlers/`.
- **Desktop shells** (both speak the same `HostWebMessageType` protocol with the page via `desktopHost` in `wwwroot/rpc.ts`):
  - **`WindowsDesktopShell.cs`** (Windows, `net10.0-windows`): native Windows Forms + WebView2 window with native context menus, file dialogs, save-before-close prompt, dark title bar and persistent window geometry.
  - **`electron/`** (Linux, macOS, and optionally Windows): Electron shell that starts the self-contained cross-platform backend (`net10.0`) in headless mode and provides the same native features (see [Electron shell](#electron-shell-linux--macos)).
- **`wwwroot/`**:
  - `rpc.ts`: Binary MessagePack WebSocket client, TypeScript DTO contracts, and the `desktopHost` bridge.
  - `layout.ts`: Resizable 3-pane + bottom workbench splitter controller.
  - `app.ts`: WZ tree explorer, interactive HTML5 canvas & animation player, `Sound_DX8` waveform player, property inspector, CodeMirror 6 JSON editor, global search, and hex/binary data inspector.

### Target frameworks

The project builds twice:

| Target | Output | Used by |
| :--- | :--- | :--- |
| `net10.0-windows` | `WinExe` with the WebView2 shell (`WINDOWS` symbol defined) | Windows desktop app |
| `net10.0` | Cross-platform console backend (headless only) | Electron shell, or a browser via `--headless` |

## Building & Publishing

### Run Locally
```bash
# Windows desktop app (WebView2)
dotnet run --project Maple.Classic.Repacker.UI.csproj -f net10.0-windows

# Any OS, in a browser: prints the URL to open
dotnet run --project Maple.Classic.Repacker.UI.csproj -f net10.0 -- --headless
```

### Publish the Windows Single-File Executable (`Maple.Classic.Repacker.UI.exe`)
The Windows target uses `PublishSingleFile` + `Costura.Fody` (`FodyWeavers.xml`) and embedded manifest resources (`wwwroot/**`) to bundle all managed dependencies, `WebView2Loader.dll`, and compiled TypeScript/CodeMirror assets into a single `.exe`:
```bash
dotnet publish Maple.Classic.Repacker.UI.csproj -c Release -f net10.0-windows -r win-x64 -o ../dist/win-x64
```

## Electron Shell (Linux / macOS)

`electron/` is a separate npm package (so regular `dotnet build`s never download Electron):

- `src/main.ts`: window lifecycle, IPC, native dialogs, and save-before-close negotiation.
- `src/backend.ts`: picks a free loopback port, starts the backend, waits for it, and stops it on exit. The backend also exits on its own if Electron dies (`MAPLE_REPACKER_PARENT_PID`).
- `src/preload.ts`: exposes `window.mapleHost` (the Electron side of `desktopHost`).
- `src/treeContextMenu.ts`, `src/windowState.ts`: native tree context menu and remembered window geometry.

Settings are stored in Electron's per-user data directory (`MAPLE_REPACKER_SETTINGS_PATH`), since packaged app directories are read-only.

```bash
cd electron
npm install

# Development: builds the shell and runs the backend via `dotnet run -f net10.0` (requires the .NET 10 SDK)
npm start

# Package for the current OS (AppImage on Linux, .dmg on macOS, NSIS installer on Windows) into electron/release/.
# Publishes a self-contained single-file backend into electron/build/backend first, so end users don't need .NET.
npm run dist
```

Set `MAPLE_REPACKER_BACKEND=/path/to/Maple.Classic.Repacker.UI` to run the shell against a specific backend executable.

## Keyboard Shortcuts

| Shortcut | Action |
| :--- | :--- |
| `Ctrl+O` | Open `.wz` file from disk or browser upload |
| `Ctrl+Shift+S` | Repack and save active `.wz` archive |
| `Ctrl+F` | Focus Global WZ Search |
| `Ctrl+S` | Apply JSON edits in the CodeMirror 6 JSON editor |
| `Ctrl+Z` / `Ctrl+Y` | Undo / Redo the last edit |
| `Ctrl+C` / `Ctrl+V` | Copy / Paste selected `.img` or property node |
| `Ctrl+D` | Duplicate selected `.img` or property node |
| `Insert` | Add new property to selected `.img` or container |
| `F2` | Rename selected node |
| `Delete` | Delete selected node |
