# XT1 Studio

A Windows viewer/editor for PlatinumGames Bayonetta 3 `WTB`/`XT1` texture banks
(`.wta` + `.wtp`, `.dat` + `.dtt`, standalone `.wtb`, raw `.xt1`).

Project root: `C:\Users\egorb\Downloads\exefs\Xt1Tool` (the plan's proposed root was used unchanged).
`XT1_FORMAT.md` is the authoritative format spec and wins over the legacy `prototype.py` in all
cases.

## Requirements

- Windows x64
- .NET 8 SDK (developed against the .NET 9 SDK building `net8.0` / `net8.0-windows`)
- Optional native dependencies, auto-detected, all overridable in **Tools → Settings**:
  - NVIDIA Texture Tools 3.2.5: `nvtt30205.dll` / `nvtt_export.exe`
    (default `C:\Program Files\NVIDIA Corporation\NVIDIA Texture Tools`)

```powershell
dotnet build Xt1Tool.sln
dotnet test  tests\Xt1.Core.Tests\Xt1.Core.Tests.csproj
dotnet run   --project src\Xt1.App\Xt1.App.csproj
```

The tool is portable: `settings.json` (backend, defaults, tool paths, last opened folder) lives in the
same directory as the executable. A settings file left in the old `%APPDATA%\Xt1Studio` location is
migrated on first launch.

### Release publish

`publish-release.bat` reproduces the Visual Studio `FolderProfile` (Release, `net8.0-windows`,
framework-dependent, `win-x64`, single file, ReadyToRun off) and writes to
`src\Xt1.App\bin\Release\net8.0-windows\publish\win-x64\`:

```powershell
.\publish-release.bat                     # profile settings; runs a self-check on the result
.\publish-release.bat /clean              # empty the output folder first
.\publish-release.bat /bundle-natives     # one self-contained .exe (natives extract on first run)
.\publish-release.bat /nopause            # no "press any key" at the end
```

All managed assemblies are bundled into `Xt1.App.exe`. In the default (profile) mode the native
`glfw3.dll` (OpenGL windowing) sits next to it, plus `.pdb` symbols; `/bundle-natives` folds the
natives in too so the folder contains only the executable.
Framework-dependent output requires the .NET 8 Desktop Runtime on the target machine.

### Headless self-check

```powershell
.\publish-release.bat /nopause     # runs it automatically after publishing
src\Xt1.App\bin\Debug\net8.0-windows\Xt1.App.exe --selfcheck
src\Xt1.App\bin\Debug\net8.0-windows\Xt1.App.exe --dump-tree D:\Bayonetta3\romfs\ui\ui_core.dat
src\Xt1.App\bin\Debug\net8.0-windows\Xt1.App.exe --uitest D:\Bayonetta3\romfs\em\em0000.dat
src\Xt1.App\bin\Debug\net8.0-windows\Xt1.App.exe --export-decode <file> <out.png> --index 3 --slice 8
src\Xt1.App\bin\Debug\net8.0-windows\Xt1.App.exe --export-decode <file> x --hash 0x482960F9 --scan-layouts
src\Xt1.App\bin\Debug\net8.0-windows\Xt1.App.exe --gltest D:\Bayonetta3\romfs\core\core.dat --out %TEMP%\gl
src\Xt1.App\bin\Debug\net8.0-windows\Xt1.App.exe --panelshot D:\Bayonetta3\romfs\core\core.dat %TEMP%\panel.png --tree
```

`--export-decode` writes a decode as PNG so a layout can be inspected by eye (`--mip`, `--slice`,
`--bank`, `--index`, `--hash`, `--all`), and `--scan-layouts` sweeps `pitch` × `blockHeight`
candidates scoring each one. The score is the surface's **padding fill**, which some atlases write
as magenta (255,0,255): a wrong stride drags padding into the image. That objective is how the
`ui_core.dat` 1026x1026 atlas was pinned down (3072-byte pitch → 6 magenta pixels; 2752 → 78,594).

`--gltest` assembles every 3D texture (or, with `--bank`, every cubemap) in the real viewport,
renders it offscreen and checks the result numerically, writing the frames as PNGs: that the volume
draws and sits centered, that orbiting changes the image, that channel isolation and the opacity
source reach the shader, that the slice planes stay inside the volume's projected box, and that the
skybox draws and responds to turning and zoom. It reports `n/a` rather than a failure where the data
cannot show a difference (a flat skybox, a gray volume). Options: `--index`, `--hash`, `--bank`,
`--mip`, `--out`, `--mode rgb|alpha|mip`, `--channel rgba|r|g|b|a`, `--yaw`, `--pitch`,
`--distance`, `--size`, `--density`.


`--panelshot <file> <out.png> [--index N] [--advanced] [--tree] [--height N]` renders the inspector
(optionally beside the tree) to a PNG and prints the inspector state, its validation verdict and the
right-click menu for the selection, so the UI itself can be reviewed and regression-checked without a
human at the keyboard.

`--uitest` builds the real control tree, binds a texture to the inspector and pumps the message loop,
printing a timestamp per step and failing if any step exceeds 5 s. It also drives the new surfaces the
way clicks would and checks the result: the hash field parses a pasted value, **Calculate CRC32** matches
`crc32(name) & 0x7FFFFFFF`, and the tree menu exposes (and correctly enables) import/export/delete. It
exists because a layout blow-up (one control set per DAT sub-file) made the old properties tab take
112 s on `em0000.dat` with no way to notice it headlessly.

Loads the native tools, round-trips the codecs, loads a golden bank, proves a no-edit save is
byte-identical, and re-encodes a texture at a new size then reloads the bank to verify every
`table0` offset.

## Format facts confirmed against real data

These were re-derived from `D:\Bayonetta3\romfs` and are locked by tests:

| Area | Result |
| --- | --- |
| 2D imageSize | Per-mip `align512(pitch * round_up(heightBlocks, lines))`; verified over the whole `pl/` + `em/` census |
| 3D imageSize | Per-mip `align512(prev) + surface2d(w_m, h_m) * max(1, depth >> m)` (mip-major, slice-minor); verified on all `core.dat` 3D records incl. 128³/8 mips |
| Cube imageSize | `6 * faceStride`, where `faceStride = align4096(faceSize)` only when the base mip surface is >= 4096 bytes; verified on every cube group |
| Wide pitch (`tile_width_spacing = 4`) | A record with `blockHeightLog2 == 3` whose base-mip row is not 1024-aligned widens every non-"small" mip to a 1024-byte pitch. This explains the former multi-mip ASTC 5×5/6×6 class exactly (915456 / 1385472 / 4596736), so **no record is left flagged**; the layout exposes it as `WidePitchApplied` and the tree dump counts `widePitch` |
| DAT container | 4-byte descriptor = sub-file size; tables 4-byte aligned; sub-files packed contiguously; `.dtt` payload base is 0x1000. Offsets are **not always monotonic**: stub entries are `0` and some sub-files alias a region nested inside another (`es*.dat` keeps its `"wta"` inside its embedded `"eff"` DAT), so a sub-file range comes from the declared size when it fits |
| GOB swizzle | Tegra block-linear. A GOB is 64x8 bytes; `blockHeight` stacks GOBs vertically and **`blockDepth` stacks slices**, so 3D Z is interleaved. Addressing follows the `tegra_swizzle` reference (Ryujinx / Tegra TRM) and `Swizzle`/`Deswizzle` are exact inverses |
| `textureLayout` | Low 3 bits = `blockHeightLog2`; **bits 4..6 = `blockDepthLog2`** (0 for 2D, 4 = block depth 16 for every 3D record) |
| Wide-pitch chain | `em305c` #2 (ASTC 5×5 2050x2050, 11 mips) decomposes exactly: pitches `[7168,4096,2048,832,448,256,128,64,64,64,64]`, total 4596736. The pitch must be fed to the swizzle or the magenta padding leaks in (3072 → 6 pixels, 2752 → 78,594). The 47 single-mip cases are the same mechanism; the stored-size back-solve is now only a fallback and no longer fires |
| Cube faces | A face is `align4096`-ed **only when its base mip surface reaches 4096 bytes**, never when the whole chain merely exceeds a page: 8x8 BC3s → 512/face (dense), BC1 64x64/7 → 5120/face (dense, total 30720), BC3 64x64/7 → 8192/face (total 49152) |
| Formats | `0x01` (`"af"`) is a single uncompressed 8-bit channel at 1 byte/px (not compressed — corrected, see below); `0x6D` is uncompressed 32 bpp (decode-only, channel order unconfirmed) |
| Hash | `crc32(name) & 0x7FFFFFFF`, masked, never auto-overwritten |

## Layout

```
src/Xt1.Core   net8.0, no UI: containers, XT1 model, swizzle, layout, codecs, NVTT interop, writers, editing
src/Xt1.App    net8.0-windows: WinForms shell, GDI+ 2D viewer, OpenGL cubemap/volume viewport, dialogs
tests/Xt1.Core.Tests  418 tests, real-file validation against D:\Bayonetta3\romfs when present
```

`Xt1.Core` is headless and fully testable; the app only consumes it.

## Features

- Open `.wta`, `.dat`, `.dtt`, `.wtb`, `.xt1`; a `.dat` pairs automatically with its `.dtt`
  (and vice versa), and a split effect `.wta` automatically pairs with its sibling `.wtp`.- Collapsible container tree. Banks reach it under two tags: `"wta"` (whose payload is paired from
  the `"wtp"` sub-files) and `"wtb"` (a standalone bank carrying its payload inline). A container
  with several banks gets one branch per bank, labeled with the DAT's own sub-file name. There is no
  separate bank selector: every bank is already a branch of the same tree. Single-bank containers keep
  a short `wta`/`wtb` label. Texture leaves show the `table3` hash after the index when the bank has one:

  ```
  core.dat  (DAT)
    pre_texture.wta  (40 textures)
      #0  0x6B4C494D  BC1_UNORM  512x512  m10
      #1  0x40EC4FF8  RGBA8_UNORM  64x64  m1
      ...
    CubeMap.wtb  (11 textures)
      #0  0x3FAB232D  BC6H_UF16  64x64  m7  cube
      ...
    Grading.wtb  (3 textures)
      #0  0x07140F05  RGBA8_UNORM  256x16  m1
  ```

  `core.dat`/`core.dtt` is the interesting case: the `"wta"` bank lives in the `.dat` and its
  payload in the `.dtt`, while `CubeMap.wtb` and `Grading.wtb` are `"wtb"` sub-files inside the
  `.dtt` itself. Scanning only for `"wta"` used to hide two thirds of that container.

  The same hierarchy is available headlessly, for one file or a whole folder:

  ```powershell
  Xt1.App.exe --dump-tree <file>     # tree + per-texture decode check
  Xt1.App.exe --dump-tree <folder>   # scan every .wta/.wtb/.dat/.xt1 recursively
  ```

  A folder scan decodes every supported base mip, so it doubles as a validation pass. On a full
  unpacked `romfs` it reports: 1698 bank files, 1271 loaded (932 `ModelDat`, 335 `EffectWta`,
  4 `StandaloneWtb`), 427 skipped as non-texture containers (`wmb`/`bxm`/`hkx`/`epb`/`shader`…),
  0 failures, and 10341/10341 textures decoded — every readable texture in the game, with no
  format left undecoded. Every container is reported as skipped or loaded; nothing is silently
  dropped.
- Right-hand inspector, one panel rather than a set of tabs:
  - Identity: `Texture #23`, then the bank branch and position in it (`pre_texture.wta | 24 of 40`), the
    texture type, and a read-only spec line — format and its code, dimensions, type, mip levels and
    stored size. These describe the payload, so they are **not editable here**: to change the format
    or dimensions, import/replace the texture.
  - Validation verdict directly under it: green when the layout matches the stored size and the
    texture decodes, red with the exact reason when it does not (a stored `imageSize` that the
    layout cannot explain is reported here, using the same note the layout produced).
  - Hash: the `table3` hash as an editable hex field that accepts a paste with or without `0x`, and a
    **Calculate CRC32** button that asks for the name and writes `crc32(name) & 0x7FFFFFFF`.
  - Flags: the confirmed `table2` bits as named toggles — Model / UI bank entry, sRGB color space,
    Cube map usage — with the raw 4 bytes shown muted underneath for anything the toggles do not
    cover. Bit `0x08` is deliberately not offered: it was first read as "compressed", but per
    `XT1_FORMAT.md` §4 it marks the `0x01` (`"af"`) single-channel format rather than a texture
    property.
  - **Show advanced fields** reveals the developer view: the technical summary (decoded header,
    `table0/1/2/3`, per-mip layout offsets), the scrollable typed editor for every raw field, and the
    DAT sub-file table. That table is virtualized (some containers hold thousands of sub-files —
    `shader.dat` has 5588 — and building a control per row made the panel take minutes to lay out).
  - Raw bytes of the XT1 header (or any table blob) open in their own hex dialog; unknown bytes
    survive a round trip.
- Right-click menu in the container tree for the edit/export actions, so they sit where a texture is
  selected instead of in the menu bar: **Replace...** (an image is re-encoded through the import
  dialog; a `.dds` is copied in losslessly, mips included), **Replace with .xt1...**, an **Export**
  submenu (`.png` / `.dds` / `.xt1`), add texture from image or DDS, delete texture, copy hash, and
  the bank-level PNG export. Items are enabled per selection: DDS export only for formats that can be
  written (including ASTC), bank actions only when a bank is loaded, and so on.
- 2D viewer: pan/zoom, mip selection, R/G/B/A isolation, alpha checkerboard, optional gamma. For a 3D
  texture it shows the currently selected slice (with pan/zoom preserved while scrubbing), so one
  slice can be inspected while the GL tab shows the whole volume.
- GL viewport:
  - **Cubemap**: an inside-out skybox. Entering the view starts level and forward (it does not
    inherit the volume's orbit angles). Drag or the arrow keys look around (drag right turns right,
    drag up looks up). The look uses `Ry(yaw)*Rx(pitch)` - pitch in the camera's own frame, then yaw
    about world up - so the camera's right vector is always horizontal and the horizon cannot roll or
    flip; pitch is capped ~10 degrees short of vertical. (The volume's orbit camera keeps the
    `Rx(pitch)*Ry(yaw)` form it needs to swing an object upright.) A drag that reaches the viewport
    edge is wrapped back to the middle so a long turn does not stall; wheel changes the field of
    view; double-click or Home resets. The shared channel selector isolates R/G/B/A here too, and explicit
    **Gamma** / **Exposure** controls sit at the right of the slice bar.
  - **3D texture**: the whole volume assembled into one ray-marched object. The camera orbits it —
    the target stays at the origin for every angle, drag swings the object round, wheel dollies in
    and out, double-click resets. A faint bounding box is drawn so the object reads even where it is
    empty.
  - **Render modes** (`RGB`, `Alpha`, `MIP`): `RGB`/`Alpha` are alpha-blended volume renders whose
    opacity comes from the RGB luminance or from the stored alpha; `MIP` is a maximum-intensity
    projection. Blended opacity is normalized by the ray's path through the box, so a flat volume
    (256x256x16 crosses only a sliver of its box) is as visible as a cubic one. The toolbar
    `Opaque a` toggle and the render-mode combo are two views of the same "ignore alpha" choice, so
    the button applies in 3D too; a volume whose alpha is uniform defaults to `RGB`, because alpha
    carries no density there.
  - **Channel isolation** (`R`/`G`/`B`/`A`) applies to the cubemap, the volume and its slices, not
    just the 2D viewer.
  - **Slice planes** (off by default) overlay the three axis cross-sections; the slice control moves
    them and switches the 2D tab to that slice.
  - Volumes are assembled whole; only a volume larger than 512 slices or 64M texels is truncated, and
    the status line says so when that happens.

- Import/replace from an image or a DDS (a DDS is copied in losslessly with its own mips; adding a
  DDS works too), add/delete textures, per-texture export to PNG / DDS / raw `.xt1`, and a bank- or
  container-wide PNG export. All of it is in the tree's right-click menu.
- **File → New empty bank...** authors a fresh `.wta`+`.wtp` or standalone `.wtb` with no textures.
- **Tools → Batch recompress (ASTC to BC)**: re-encodes ASTC textures to BC1/BC3/BC7, keeping the
  existing mip levels (none are regenerated) and only touching textures that decode cleanly. Optional
  half-size downscale (drops mip 0 when a chain exists, otherwise box-filters a single-mip image,
  with a checkbox to skip images that have no mips), a choice of BC formats including an
  opacity-aware BC1/BC3 or BC1/BC7 rule, UNORM/sRGB respected. Runs on the loaded document or over a
  folder of `.dat`/`.wta`/`.wtb`/`.xt1` files with an optional recursive scan.
- WTB sRGB/cubemap flags can be kept in step with a texture's format on add/replace (on by default),
  and new textures default to the model/UI bank class.
- Undo/redo across field edits, raw byte edits, add/delete and re-encode.
- Save is diff-preserving and atomic: unchanged sub-files, tables and reserved bytes are written
  back byte-for-byte, all outputs are staged next to their destinations first, and the payload
  stream plus `table0`/`table1` are laid out from the same recomputed state (verified by tests).
- Failed encodes are never committed: a texture whose re-encode fails keeps its original format
  and payload.

## Encoders

NVTT is the default backend; when it is not installed (or cannot encode a format) the managed
encoder is the fallback.

- **NVTT** (`nvtt30205.dll`, in-process P/Invoke, output captured via an output handler; one
  surface at a time, swizzled and packed by us) — preferred, handles BC1–BC7, BC6H and all 14
  ASTC footprints.
- **nvtt_export CLI** — fallback; `--help` is probed once and the required flags are verified.
  `nvtt_export` 3.2.5 exposes no signed BC4/BC5 ids, so those two formats are in-process only;
  the capability table reports that explicitly instead of letting the backends disagree.
- **Managed** (`BCnEncoder.Net` for BC1–BC5/BC7, `AstcSharp` for ASTC, raw copy) — zero external
  dependencies; this is what decodes previews and what encodes when NVTT is missing.

Both backends share one XT1→format mapping (`ManagedFormatMap`), one transparency test
(`RgbaImage.HasTransparency`) and one sRGB curve (`Srgb`), so they cannot drift apart.

`Prototype.py`'s stale format table (e.g. `0x38` labeled ASTC) is **not** used; `0x38` is RGBA8
sRGB, and the real table comes from `XT1_FORMAT.md`.

## Known limitations

- **3D editing** is view-only. Volumes decode correctly (depth-interleaved) and preview assembled in
  the 3D viewport, but re-encoding a volume is rejected rather than approximated.
- Signed BC4/BC5 and BC6H encoding are NVTT-only: the managed path reports them as unsupported
  instead of writing blocks with the wrong signedness, so without NVTT those formats are refused
  rather than silently mis-encoded. Only the tool paths NVTT actually consumes are configurable
  (`nvtt30205.dll`, `nvtt_export.exe`).
- Alpha / binary-alpha and the `0x2C` textureFlag semantics are still unknown; they are exposed as
  free-form editable fields and preserved verbatim rather than interpreted.
- No known unexplained layout mismatches remain: the former wide-pitch (multi-mip ASTC) and
  tiny-cube classes are computed exactly, and the stored-size pitch back-solve no longer fires on
  the known corpus. A record whose stored size still disagrees is surfaced as a warning with an
  override, and the base mip still previews from the stored size.
- Payloads shorter than the computed surface decode with zero-fill and a warning rather than
  throwing (malformed/truncated banks stay loadable).
- `Xt1.App.exe --selfcheck` runs the headless integration check (native tools, codecs, golden
  bank load, byte-identical no-edit save, and a save→reload validation that re-encodes a texture
  at a new size and verifies every `table0` offset).
