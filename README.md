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
  - Oodle: `oo2core_7_win64.dll` (default `C:\bayonetta_tools\pkztools`)

```powershell
dotnet build Xt1Tool.sln
dotnet test  tests\Xt1.Core.Tests\Xt1.Core.Tests.csproj
dotnet run   --project src\Xt1.App\Xt1.App.csproj
```

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
`glfw3.dll` (OpenGL windowing) and the optional `oo2core_7_win64.dll` sit next to it, plus `.pdb`
symbols; `/bundle-natives` folds the natives in too so the folder contains only the executable.
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
| Cube imageSize | `6 * align4096(faceSize)`; verified on three independent cube groups |
| Anomalies (warn, never fatal) | Multi-mip ASTC 5×5/6×6 at large dimensions (915456 / 1385472 / 4596736) are **not** explained by pitch or by 64-block width padding; they stay flagged and copied verbatim (**51 records**, 39 of which leak surface padding). The single-mip form of the same class and the tiny-cube class are both solved |
| DAT container | 4-byte descriptor = sub-file size; tables 4-byte aligned; sub-files packed contiguously; `.dtt` payload base is 0x1000. Offsets are **not always monotonic**: stub entries are `0` and some sub-files alias a region nested inside another (`es*.dat` keeps its `"wta"` inside its embedded `"eff"` DAT), so a sub-file range comes from the declared size when it fits |
| GOB swizzle | Tegra block-linear. A GOB is 64x8 bytes; `blockHeight` stacks GOBs vertically and **`blockDepth` stacks slices**, so 3D Z is interleaved. Addressing follows the `tegra_swizzle` reference (Ryujinx / Tegra TRM) and `Swizzle`/`Deswizzle` are exact inverses |
| `textureLayout` | Low 3 bits = `blockHeightLog2`; **bits 4..6 = `blockDepthLog2`** (0 for 2D, 4 = block depth 16 for every 3D record) |
| Recovery pitches | A few large-dimension ASTC records store a **wider row pitch** than the formula predicts. The pitch is recovered from the stored `imageSize` and **must be fed to the swizzle**, otherwise padding leaks into the image (47 records) |
| Cube faces | Each face is aligned to 4096 **only when it exceeds one page**; an 8x8 BC3s face is stored densely at 512 bytes (`6*512`, not `6*4096`) |
| Formats | `0x01` (`"af"`) is a single uncompressed 8-bit channel at 1 byte/px (not compressed — corrected, see below); `0x6D` is uncompressed 32 bpp (decode-only, channel order unconfirmed) |
| Hash | `crc32(name) & 0x7FFFFFFF`, masked, never auto-overwritten |

## Layout

```
src/Xt1.Core   net8.0, no UI: containers, XT1 model, swizzle, layout, codecs, NVTT/Oodle interop, writers, editing
src/Xt1.App    net8.0-windows: WinForms shell, GDI+ 2D viewer, OpenGL cubemap/volume viewport, dialogs
tests/Xt1.Core.Tests  257 tests, real-file validation against D:\Bayonetta3\romfs when present
```

`Xt1.Core` is headless and fully testable; the app only consumes it.

## Features

- Open `.wta`, `.dat`, `.dtt`, `.wtb`, `.xt1`; a `.dat` pairs automatically with its `.dtt`
  (and vice versa), and a split effect `.wta` automatically pairs with its sibling `.wtp`.- Collapsible container tree. Banks reach it under two tags: `"wta"` (whose payload is paired from
  the `"wtp"` sub-files) and `"wtb"` (a standalone bank carrying its payload inline). A container
  with several banks gets one branch per bank, labeled with the DAT's own sub-file name, and the
  toolbar selector appears when there is more than one. Single-bank containers keep a short
  `wta`/`wtb` label. Texture leaves show the `table3` hash after the index when the bank has one:

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
    texture decodes, red with the exact reason when it does not (the `imageSize` anomaly classes
    above are reported here, using the same notes the layout produced).
  - Hash: the `table3` hash as an editable hex field that accepts a paste with or without `0x`, and a
    **Calculate CRC32** button that asks for the name and writes `crc32(name) & 0x7FFFFFFF`.
  - Flags: the confirmed `table2` bits as named toggles — Model / UI bank entry, sRGB color space,
    Cube map usage — with the raw 4 bytes shown muted underneath for anything the toggles do not
    cover. Bit `0x08` is deliberately not offered: it was first read as "compressed", but per
    `XT1_FORMAT.md` §4 it marks the `0x01` (`"af"`) single-channel format, and Oodle is a property of
    the container sub-file rather than of a texture.
  - **Show advanced fields** reveals the developer view: the technical summary (decoded header,
    `table0/1/2/3`, per-mip layout offsets), the scrollable typed editor for every raw field, and the
    DAT sub-file table. That table is virtualized (some containers hold thousands of sub-files —
    `shader.dat` has 5588 — and building a control per row made the panel take minutes to lay out).
  - Raw bytes of the XT1 header (or any table blob) open in their own hex dialog; unknown bytes
    survive a round trip.
- Right-click menu in the container tree for the edit/export actions, so they sit where a texture is
  selected instead of in the menu bar: import image/DDS (replace), replace raw payload, export PNG /
  all mips / DDS / raw XT1, add texture from image, delete texture, recompute image size, copy hash,
  validate this texture, plus the bank-level export, batch-force and validate entries. Items are
  enabled per selection: DDS export only for formats that can be written, bank actions only when a
  bank is loaded, and so on.
- 2D viewer: pan/zoom, mip selection, R/G/B/A isolation, alpha checkerboard, optional gamma. For a 3D
  texture it shows the currently selected slice (with pan/zoom preserved while scrubbing), so one
  slice can be inspected while the GL tab shows the whole volume.
- GL viewport:
  - **Cubemap**: an inside-out skybox. Drag to turn, wheel to change the field of view, double-click
    to reset.
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
  - **Channel isolation** (`R`/`G`/`B`/`A`) applies to the volume and its slices, not just the 2D
    viewer.
  - **Slice planes** (off by default) overlay the three axis cross-sections; the slice control moves
    them and switches the 2D tab to that slice.
  - Volumes are assembled whole; only a volume larger than 512 slices or 64M texels is truncated, and
    the status line says so when that happens.

- Import image/DDS (replace or add), replace raw payload, delete texture, batch-force `table2`
  byte or `textureFlag`, recompute `imageSize`, per-texture export to PNG / all mips / DDS /
  raw `.xt1`, and a bank- or container-wide PNG export. All of it is in the tree's right-click menu.
- Batch build over a folder of banks, with format/backend/quality/mips options and a live log.
  It writes to a separate output folder by default; overwriting originals in place requires an
  explicit opt-in and a confirmation, and files where nothing was converted are not rewritten.
- Undo/redo across field edits, raw byte edits, add/delete and re-encode.
- Save is diff-preserving and atomic: unchanged sub-files, tables and reserved bytes are written
  back byte-for-byte, all outputs are staged next to their destinations first, and the payload
  stream plus `table0`/`table1` are laid out from the same recomputed state (verified by tests).
- Failed encodes are never committed: a texture whose re-encode fails keeps its original format
  and payload.

## Encoders

Priority per the plan: NVTT in-process → `nvtt_export` CLI → managed.

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

- **Oodle**: decompression is wired and validated against a real `.pkz` payload. The installed
  `oo2core_7_win64.dll` is a **decompress-only** build (`OodleLZ_Compress` always returns 0), so
  recompression is offered but disabled when probed as unsupported; saves then write raw payloads.
- **3D editing** is view-only. Volumes decode correctly (depth-interleaved) and preview assembled in
  the 3D viewport, but re-encoding a volume is rejected rather than approximated.
- `astcenc.exe` / `texconv.exe` paths are configurable but no CLI wrapper is wired; NVTT plus the
  managed ASTC path cover ASTC/BC encoding.
- Alpha / binary-alpha and the `0x2C` textureFlag semantics are still unknown; they are exposed as
  free-form editable fields and preserved verbatim rather than interpreted.
- The two documented `imageSize` anomaly classes are surfaced as warnings with an override; the
  base mip still previews correctly because the stored size is larger than the computed one.
- Payloads shorter than the computed surface decode with zero-fill and a warning rather than
  throwing (malformed/truncated banks stay loadable).
- `Xt1.App.exe --selfcheck` runs the headless integration check (native tools, codecs, golden
  bank load, byte-identical no-edit save, and a save→reload validation that re-encodes a texture
  at a new size and verifies every `table0` offset).
