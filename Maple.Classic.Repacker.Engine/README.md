# Maple.Classic.Repacker.Engine

Core workspace, codec, and serialization library for inspecting, editing, converting, and repacking MapleStory Classic World (`.wz` and `.img`) archives.

## Components

- **`Workspace/RepackerWorkspace.cs` & `ArchiveSession.cs`**:
  - Thread-safe multi-archive session manager supporting on-demand `.img` parsing, virtual directory/image overlays, per-image undo/redo history stacks, cross-archive clipboard copy/paste, global search, and batch asset export.
  - Resolves `UOL` links, intra-image `_inlink` references, and cross-archive `_outlink` references (e.g. `Character_000.wz` $\rightarrow$ `Character_Canvas_000.wz`).
- **`Canvas/WzCanvasCodec.cs` & `PngCodec.cs`**:
  - Decompresses and compresses WZ `Canvas` Zlib/Deflate payloads and converts between 32-bit `RGBA8888` / `.png` and all 6 MapleStory WZ pixel formats:
    - `1`: `BGRA4444` (16-bit, 4 bits per channel)
    - `2`: `BGRA8888` (32-bit truecolor with alpha)
    - `513`: `BGR565` (16-bit opaque RGB)
    - `517`: `BGR565 Block16` (16×16 downscaled block thumbnail)
    - `1026`: `DXT3` (`BC2` block compression with explicit 4-bit alpha)
    - `2050`: `DXT5` (`BC3` block compression with interpolated alpha)
- **`Audio/WzSoundCodec.cs`**:
  - Parses `Sound_DX8` headers (`WAVEFORMATEX` / `MPEGLAYER3WAVEFORMAT`), wraps raw PCM streams in standard RIFF `WAVE` containers for playback, and builds `Sound_DX8` properties from `.mp3` and `.wav` files.
- **`Serialization/JsonWzCodec.cs`**:
  - Deserializes JSON trees produced by `WzValue.ToJson()` back into `WzValue` hierarchies while preserving existing binary payloads (`Canvas`, `Sound`, `RawData`) and exact numeric types (`Short`, `Int`, `Long`, `Float`, `Double`).
