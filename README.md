# Maple.Classic.Repacker

MapleStory Classic World (`mscw`) `.wz` and `.img` repacker, sprite/animation studio, and property editor inspired by [HaRepacker-resurrected](https://github.com/lastbattle/Harepacker-resurrected), built on C# (`.NET 10`), MessagePack-over-WebSockets RPC, and an Electron / WebView2 frontend.

## Projects

- **[`Maple.Classic.File`](Maple.Classic.File)** *(submodule)*: Core zero-copy `.wz` archive and `.img` property parser, reader, and writer (`Maple.Classic.File.IO`).
- **[`Maple.Classic.Repacker.Engine`](Maple.Classic.Repacker.Engine)**: Thread-safe multi-archive workspace (`RepackerWorkspace`, `ArchiveSession`), WZ pixel format codecs (`WzCanvasCodec`, `PngCodec`), DirectSound8 audio codec (`WzSoundCodec`), and JSON subtree round-trip serializer (`JsonWzCodec`).
- **[`Maple.Classic.Repacker.UI`](Maple.Classic.Repacker.UI)**: Desktop and web studio featuring a native Windows WebView2 shell (`WindowsDesktopShell`), cross-platform Electron wrapper (`electron/main.ts`), MessagePack WebSocket RPC server (`Program.cs`), and TypeScript + CodeMirror 6 frontend (`wwwroot/`).
- **[`Maple.Classic.Repacker.Tests`](Maple.Classic.Repacker.Tests)**: xUnit test suite covering PNG and WZ canvas codecs across all 6 pixel formats, `Sound_DX8` audio extraction/synthesis, JSON round-tripping, cross-archive `_outlink` resolution and inlining, undo/redo, CRUD, search, and archive repacking.

## Key Features

- **Multi-Archive WZ Explorer**: Open individual `.wz` archives, split `_000.wz` / `_Canvas` trees, or an entire MapleStory `Data` folder with lazy `.img` expansion and instant filtering.
- **Interactive Canvas & Animation Studio**:
  - Decodes and encodes all MapleStory WZ pixel formats (`1` BGRA4444, `2` BGRA8888, `513` BGR565, `517` Block16 BGR565, `1026` DXT3/BC2, `2050` DXT5/BC3).
  - Pan/zoom viewport with origin crosshair (`origin`), bounding box overlays (`lt`, `rb`, `head`), pixel grid, and multi-frame animation playback.
  - Automatic cross-archive `_outlink`, intra-image `_inlink`, and relative `UOL` resolution with one-click sprite inlining.
- **`Sound_DX8` Audio Player**: Plays embedded MP3 and PCM WAV audio streams with waveform visualization, telemetry, and `.mp3` / `.wav` import & export.
- **Property CRUD & JSON Subtree Editor**:
  - Edit primitive, `Vector2D`, `UOL`, and `Canvas` properties; add, rename, duplicate, copy/paste, and delete properties, `.img` images, and directories with per-image Undo/Redo (`Ctrl+Z` / `Ctrl+Y`).
  - Edit any `.img` or property subtree as syntax-highlighted JSON in CodeMirror 6 (`Ctrl+S` to apply) while preserving binary payloads.
- **Global Search, Hex Inspector & Bulk Export**:
  - Search across all open `.wz` archives by node name, path, value, and property type.
  - Inspect raw binary slices in a 16-byte Hex & ASCII dump with a live `Int8`/`UInt8`/`Int16`/`Int32`/`Float32`/`Float64` byte inspector.
  - Batch-export sprites (`.png`), audio (`.wav`/`.mp3`), `.json`, or `.img` files to disk, and repack modified `.wz` archives.

## Building & Running

**Prerequisites:**
- **.NET 10 SDK**
- **Node.js & `npm`** (invoked automatically by [`Maple.Classic.Repacker.UI.csproj`](Maple.Classic.Repacker.UI/Maple.Classic.Repacker.UI.csproj) during `dotnet build` / `dotnet publish` to install frontend dependencies and bundle `@codemirror/*` and `@msgpack/msgpack` via [`build-vendor.mjs`](Maple.Classic.Repacker.UI/scripts/build-vendor.mjs))

### Option 1: Run Desktop / Local Web Application (.NET 10)
```bash
# Windows (launches native WebView2 desktop shell by default)
dotnet run --project Maple.Classic.Repacker.UI/Maple.Classic.Repacker.UI.csproj

# Cross-platform headless web server (open http://127.0.0.1:5189 in browser or Electron)
dotnet run --project Maple.Classic.Repacker.UI/Maple.Classic.Repacker.UI.csproj -- --headless --urls http://127.0.0.1:5189
```

### Option 2: Publish Standalone Windows Executable (`Maple.Classic.Repacker.UI.exe`)
Publishing embeds all managed libraries (`Maple.Classic.File.IO`, `Maple.Classic.Repacker.Engine`, `MessagePack`, `WebView2`), the native `WebView2Loader.dll` (`Costura.Fody`), and all compiled `wwwroot/` frontend assets into a single portable `Maple.Classic.Repacker.UI.exe`:
```bash
dotnet publish Maple.Classic.Repacker.UI/Maple.Classic.Repacker.UI.csproj -c Release -r win-x64 -o dist/win-x64
```

### Option 3: Run with Electron
```bash
npx electron Maple.Classic.Repacker.UI/obj/ts/electron/main.js
```

### Build & Run Tests
```bash
dotnet build Maple.Classic.Repacker.sln
dotnet test Maple.Classic.Repacker.sln
```

## Programmatic Engine Usage

```csharp
using Maple.Classic.Repacker.Engine.Models;
using Maple.Classic.Repacker.Engine.Workspace;

using var workspace = new RepackerWorkspace();

// Open a single archive or an entire MapleStory Data directory
ArchiveSummaryDto archive = workspace.OpenArchiveFromFile("Data/Character/Character_000.wz");
workspace.OpenArchiveFromFile("Data/Character/Character_Canvas_000.wz");

// Inspect a Canvas node (automatically resolves _outlink across open _Canvas archives)
NodeInspectionDto node = workspace.InspectNode(archive.ArchiveId, "Coat/01040000.img/stand1/0");

// Inline external _outlink sprites directly into the image
workspace.InlineOutlinks(archive.ArchiveId, "Coat/01040000.img");

// Modify a property and repack the archive to disk
workspace.SetPropertyValue(archive.ArchiveId, "Coat/01040000.img/info/reqLevel", RepackerPropertyKind.Int, "15", null, null);
workspace.SaveArchiveToDisk(archive.ArchiveId, "Character_000_repacked.wz");
```
