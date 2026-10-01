# XT1 Studio

A Windows viewer/editor for PlatinumGames Bayonetta 3 `WTB`/`XT1` texture banks
(`.wta` + `.wtp`, `.dat` + `.dtt`, standalone `.wtb`, raw `.xt1`).

Project root: `C:\Users\egorb\Downloads\exefs\Xt1Tool` (the plan's proposed root was used unchanged).
`XT1_FORMAT.md` is the authoritative format spec and wins over the legacy `prototype.py` in all
cases.

## Requirements

- Windows x64
- .NET 8 SDK (developed against the .NET 9 SDK building `net8.0` / `net8.0-windows`)
- Optional native dependencies, auto-detected and overridable in **Tools → Settings**:
  - NVIDIA Texture Tools 3.2.5: `nvtt30205.dll`
    (default `C:\Program Files\NVIDIA Corporation\NVIDIA Texture Tools`)

```powershell
dotnet build Xt1Tool.sln
dotnet test  tests\Xt1.Core.Tests\Xt1.Core.Tests.csproj
dotnet run   --project src\Xt1.App.Wpf\Xt1.App.Wpf.csproj
```

The tool is portable: `settings.json` (backend, defaults, tool paths, last opened folder) lives in the
same directory as the executable. A settings file left in the old `%APPDATA%\Xt1Studio` location is
migrated on first launch.

### Release publish

`publish-release.bat` reproduces the Visual Studio `FolderProfile` (Release, `net8.0-windows`,
framework-dependent, `win-x64`, single file, ReadyToRun off) and writes to
`src\Xt1.App.Wpf\bin\Release\net8.0-windows\publish\win-x64\`:

```powershell
.\publish-release.bat                     # profile settings; runs a self-check on the result
.\publish-release.bat /clean              # empty the output folder first
.\publish-release.bat /bundle-natives     # one self-contained .exe (natives extract on first run)
.\publish-release.bat /nopause            # no "press any key" at the end
```

All managed assemblies are bundled into `XT1 Studio.exe`. In the default (profile) mode the native
`glfw3.dll` (OpenGL windowing) sits next to it and the Explorer thumbnail host lives in `shell\`;
symbols are stripped from the publish, so nothing else is shipped. `/bundle-natives` folds the
natives into the executable too, leaving only the exe and `shell\`.
Framework-dependent output requires the .NET 8 Desktop Runtime on the target machine.
NVTT is not bundled by default: the app looks for `nvtt30205.dll` at the path set in
**Tools → Settings**, then beside the exe, in the NVIDIA install directories, then on PATH, and
falls back to the built-in encoder (which cannot encode BC6H). `/bundle-nvtt` copies it into the publish output — from `third_party\nvtt\` if you keep a
copy there, otherwise from the installed NVIDIA Texture Tools — which adds ~100 MB.

### Headless self-check

```powershell
.\publish-release.bat /nopause     # runs it automatically after publishing
& "src\Xt1.App.Wpf\bin\Debug\net8.0-windows\XT1 Studio.exe" --selfcheck
& "src\Xt1.App.Wpf\bin\Debug\net8.0-windows\XT1 Studio.exe" --dump-tree D:\Bayonetta3\romfs\ui\ui_core.dat
& "src\Xt1.App.Wpf\bin\Debug\net8.0-windows\XT1 Studio.exe" --uitest D:\Bayonetta3\romfs\em\em0000.dat
& "src\Xt1.App.Wpf\bin\Debug\net8.0-windows\XT1 Studio.exe" --export-decode <file> <out.png> --index 3 --slice 8
& "src\Xt1.App.Wpf\bin\Debug\net8.0-windows\XT1 Studio.exe" --export-decode <file> x --hash 0x482960F9 --scan-layouts
& "src\Xt1.App.Wpf\bin\Debug\net8.0-windows\XT1 Studio.exe" --thumbnail <file.xt1> <out.png> --size 256
& "src\Xt1.App.Wpf\bin\Debug\net8.0-windows\XT1 Studio.exe" --register-shell     # .xt1 thumbnails in Explorer
& "src\Xt1.App.Wpf\bin\Debug\net8.0-windows\XT1 Studio.exe" --gltest D:\Bayonetta3\romfs\core\core.dat --out $env:TEMP\gl
& "src\Xt1.App.Wpf\bin\Debug\net8.0-windows\XT1 Studio.exe" --panelshot D:\Bayonetta3\romfs\core\core.dat $env:TEMP\panel.png --tree
```

`--export-decode` writes a decode as PNG so a layout can be inspected by eye (`--mip`, `--slice`,
`--bank`, `--index`, `--hash`, `--all`), and `--scan-layouts` sweeps `pitch` × `blockHeight`
candidates scoring each one. The score is the surface's **padding fill**, which some atlases write
as magenta (255,0,255): a wrong stride drags padding into the image. That objective is how the
`ui_core.dat` 1026x1026 atlas was pinned down (3072-byte pitch → 6 magenta pixels; 2752 → 78,594).

`--thumbnail <file.xt1> <out.png> [--size N] [--mip M] [--face F] [--slice S]` renders one loose
`.xt1` (a single texture: header + payload) to a thumbnail. It decodes the **cheapest mip that still
covers the requested size** instead of the base level followed by a downscale — a 512x512 ASTC
texture at `--size 64` decodes mip 3 directly (107 ms measured, against 172 ms for the full 512x512) —
and never upscales: a texture smaller than the request comes back at its own size. HDR (BC6H)
textures are Reinhard-tone-mapped and sRGB encoded, matching the GL preview. Exit codes: 0 written,
1 decode/write failed, 2 bad usage or not a loose XT1.

The same rendering backs the Explorer thumbnail handler, and the subcommand stays useful on its own
for scripting. Banks (`.dat`/`.wta`/`.wtb` and their `.dtt`/`.wtp` payloads) are rejected: a bank is
many textures, so "the" thumbnail is ambiguous — export the texture as `.xt1` first (the texture menu's
Export → `.xt1`).

### Headless file operations

`--file <op>` is the command-line form of the shell's file menu, so a whole edit-and-save pass can run
without a window. `--help` prints the command list.

```powershell
& ".\XT1 Studio.exe" --help
& ".\XT1 Studio.exe" --file new out\empty.wtb --kind wtb
& ".\XT1 Studio.exe" --file export bank.dat out\ --as png           # every texture, to PNG
& ".\XT1 Studio.exe" --file export bank.dat tex.dds --as dds --index 3
& ".\XT1 Studio.exe" --file import bank.dat replacement.png --index 3 --format BC7_UNORM
& ".\XT1 Studio.exe" --file add bank.dat icon.png --bank 0 --name ui_icon
& ".\XT1 Studio.exe" --file delete bank.dat --hash 0x482960F9
& ".\XT1 Studio.exe" --file replace-raw bank.dat payload.bin --index 3 --force
& ".\XT1 Studio.exe" --file recompress bank.dat --rule bc1-bc3 --quality highest
```

Every op drives the same `BankDocument` / `TextureOperations` / `BankWriter` services the UI uses and
so behaves the same as the corresponding menu item. Operations that mutate a bank write it straight
away; `--dry-run` prints exactly what would be written (for `export` too) without touching disk, and
`--out DIR` / `--base NAME` redirect the output. `new` refuses to overwrite an existing bank unless
`--force` is given. Selectors (`--bank N`, `--index N`, `--hash 0xH`) pick the target
and must resolve to exactly one texture for `import`/`delete`/`replace-raw`; `export` with no selector
exports everything (a loose `.xt1` behaves like a one-texture bank). Image imports re-encode through
the shared encoder and default to the texture's current format, or `--format 0xNN|name` (an unknown
value is a usage error), with
`--backend managed|nvtt|cli`, `--quality`, `--mips`/`--no-mips`, `--solidify` and `--opaque`; a `.dds`
input is imported losslessly. `replace-raw` detects an `XT1\0` record and replaces header+payload
verbatim, otherwise it replaces the bare payload (a size mismatch needs `--force`). `recompress`
re-encodes ASTC textures in place, the same as the batch tool, to a target chosen with
`--rule bc1|bc1-bc3|bc1-bc7|bc7` or overridden per side with `--opaque-format`/`--alpha-format`
(any encodable target such as `bc7`, `astc 8x8` or `rgba8`); the sRGB/UNORM variant follows each
source, and single-mip inputs are kept unless `--downscale-without-mips`. Exit codes: 0 success,
1 an operation failed, 2 bad usage or no unique match.

### Explorer thumbnails

```powershell
& ".\XT1 Studio.exe" --register-shell                    # current user, no administrator needed
& ".\XT1 Studio.exe" --register-shell --machine          # all users (elevated shell)
& ".\XT1 Studio.exe" --unregister-shell [--machine]
```

The same thing lives in the GUI: **Settings → Explorer → “Thumbnails for .xt1”**, with a *for all
users* checkbox, a status line showing the current state, and a button that reads Register or Remove.
That is the path to give somebody else: hand them the published folder (keeping `shell\` beside
`XT1 Studio.exe`), have them open Settings and click Register once — no command line involved.

Windows only thumbnails a file type through an in-process COM server registered per extension
(`IThumbnailProvider`); it never launches an external program, so `--thumbnail` alone would not show
anything. `src\Xt1.Shell` is that COM server: `Xt1ThumbnailProvider` decodes through the same
`Thumbnailer` (cheapest sufficient mip, HDR tone mapping) and returns a premultiplied 32bpp DIB, and
`ShellRegistration` writes the registry entries:

```
HKCU\Software\Classes\.xt1\ShellEx\{e357fccd-a995-4576-b01f-234630154e96} = {CLSID}
HKCU\Software\Classes\.xt1\PerceivedType = image
HKCU\Software\Classes\CLSID\{CLSID}\InprocServer32 = <app>\shell\Xt1.Shell.comhost.dll
    ThreadingModel = Apartment   (required for shell extension handlers)
    Class = Xt1.Shell.Xt1ThumbnailProvider   Assembly = Xt1.Shell
```

A .NET 8 assembly cannot be loaded by the shell directly, so the handler is built with
`EnableComHosting`: `Xt1.Shell.comhost.dll` is a native DLL exporting `DllGetClassObject` that
bootstraps the runtime in whatever process asks. Windows 11 extracts thumbnails in a `dllhost.exe`
surrogate, which loads the com host, runs the handler and caches the result. The com host cannot live
inside the app's single-file exe, so the publish copies it (with `Xt1.Core` and its dependencies) into
a `shell` folder beside `XT1 Studio.exe`; `--register-shell` points at that copy.

Caveats worth knowing:

- The .NET 8 runtime is required, as it is for the app; a missing runtime means no thumbnails and no
  error. The handler is loaded into the surrogate as ordinary user code, so it is worth signing if it
  is distributed widely.
- Registering per-user works and needs no administrator. `--machine` writes the same entries to HKLM
  for every user on the machine.
- Explorer caches thumbnails per file, so restart Explorer (or clear the thumbnail cache) to refresh
  files it has already listed. A dllhost surrogate keeps the DLL loaded for a while after a request,
  which is why the app's copy of `shell\` is best-effort (a build while a thumbnail is cached logs a
  warning instead of failing).
- Thumbnails are produced for loose `.xt1` files only; a `.dat`/`.wta`/`.wtb` bank keeps its icon.
  The icon shown for `.xt1` itself is still the generic one, since no file-type icon is registered.

`--gltest` assembles every 3D texture (or, with `--bank`, every cubemap) in the real viewport,
renders it offscreen and checks the result numerically, writing the frames as PNGs: that the volume
draws and sits centered, that orbiting changes the image, that channel isolation and the opacity
source reach the shader, that the slice planes stay inside the volume's projected box, and that the
skybox draws and responds to turning and zoom, and that auto exposure is derived from the cubemap
rather than the user's exposure. It reports `n/a` rather than a failure where the data
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
`crc32(name) & 0x7FFFFFFF`, the tree menu exposes (and correctly enables) import/export/delete, and the
2D layer-animation bar appears for a volume (default 15 fps, play/pause, scrub to a layer) but stays
hidden for a flat texture. It
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
| Wide pitch (`tile_width_spacing = 4`) | A record with layout bit `0x400`, `blockHeightLog2 == 3`, and a base-mip row that is not 1024-aligned widens every non-"small" mip to a 1024-byte pitch. See `XT1_FORMAT.md` §6 for the canonical predicate. This explains the former multi-mip ASTC 5×5/6×6 class exactly (915456 / 1385472 / 4596736), so **no record is left flagged**; the layout exposes it as `WidePitchApplied` and the tree dump counts `widePitch` |
| DAT container | 4-byte descriptor = sub-file size; tables 4-byte aligned; sub-files packed contiguously; `.dtt` payload base is 0x1000. Offsets are **not always monotonic**: stub entries are `0` and some sub-files alias a region nested inside another (`es*.dat` keeps its `"wta"` inside its embedded `"eff"` DAT), so a sub-file range comes from the declared size when it fits |
| GOB swizzle | Tegra block-linear. A GOB is 64x8 bytes; `blockHeight` stacks GOBs vertically and **`blockDepth` stacks slices**, so 3D Z is interleaved. Addressing follows the `tegra_swizzle` reference (Ryujinx / Tegra TRM) and `Swizzle`/`Deswizzle` are exact inverses |
| `textureLayout` | Low 3 bits = `blockHeightLog2`; **bits 4..6 = `blockDepthLog2`** (0 for 2D, 4 = block depth 16 for every 3D record); bit `0x400` selects the wide-pitch layout class |
| Wide-pitch chain | `em305c` #2 (ASTC 5×5 2050x2050, 11 mips) decomposes exactly: pitches `[7168,4096,2048,832,448,256,128,64,64,64,64]`, total 4596736. The pitch must be fed to the swizzle or the magenta padding leaks in (3072 → 6 pixels, 2752 → 78,594). The 47 single-mip cases are the same mechanism; the stored-size back-solve is now only a fallback and no longer fires |
| Cube faces | A face is `align4096`-ed **only when its base mip surface reaches 4096 bytes**, never when the whole chain merely exceeds a page: 8x8 BC3s → 512/face (dense), BC1 64x64/7 → 5120/face (dense, total 30720), BC3 64x64/7 → 8192/face (total 49152) |
| Formats | `0x01` (`"af"`) is a single uncompressed 8-bit channel at 1 byte/px (not compressed — corrected, see below); `0x6D` is uncompressed 32 bpp (decode-only, channel order unconfirmed) |
| BC7 ids | BC7 is stored under `0x4D`/`0x4E`, the ids Bayonetta 3 mods use; round-trip testing confirms these are the real BC7_UNORM/BC7_SRGB blocks. The `0x51`/`0x52` ids other tools list for BC7 are a different format and are intentionally unmapped, so `0x4D`/`0x4E` are the only BC7 entries and the only BC7 targets in the import dialog |
| BC4/BC5 aliases | `0x49`/`0x4B` are labelled `BC4_SNORM`/`BC5_SNORM` by other tools, but testing shows the payloads are unsigned, so they are treated as BC4/BC5_UNORM aliases of `0x45`/`0x4A` (decode, encode and DDS export as UNORM). No SNORM id is known in the game data |
| Alias source | An alias is declared once, as `FormatInfo.AliasOf` in `FormatTable`. The managed codec map, the NVTT capability table and the DDS DXGI table all derive their alias entries from it (resolve-to-canonical or copy), so adding an alias is a one-place edit and the tables cannot drift |
| ASTC in DDS | ASTC DDS uses NVIDIA's DXGI ids, four slots per footprint: UNORM `134 + 4i`, sRGB `135 + 4i` (8x6 = 158, 8x8 = 162, 10x5 = 166). A stride-3 table maps a footprint to its neighbour, so 8x6 imports as 10x5. The DX10 header also follows NVTT's convention (`DDSD_LINEARSIZE` for block formats, `DDSCAPS_TEXTURE\|COMPLEX\|MIPMAP`, cube/volume bits in `dwCaps2`), which its reader requires. Verified both ways against `nvtt_export`/`nvddsinfo` |
| Hash | `crc32(name) & 0x7FFFFFFF`, masked, never auto-overwritten |

## Layout

```
src/Xt1.Core              net8.0, no UI: containers, XT1 model, swizzle, layout, codecs, NVTT interop, writers, editing
src/Xt1.App.Wpf           net8.0-windows: WPF shell (XAML, themed), hosted OpenGL cubemap/volume viewport, all dialogs
src/Xt1.Rendering.WinForms net8.0-windows: the OpenGL renderer + theme palette the WPF viewport hosts (retired WinForms shell)
src/Xt1.Shell             net8.0-windows: the .xt1 Explorer thumbnail handler (IThumbnailProvider + COM host)
tests/Xt1.Core.Tests      468 tests, real-file validation against D:\Bayonetta3\romfs when present
```

`Xt1.Core` is headless and fully testable; the app only consumes it. The window and executable icon is
`src/Xt1.App.Wpf/Xt1Studio.ico`, embedded both as the Win32 `ApplicationIcon` and as a WPF `Resource`
(the window icon) so the published single-file exe shows it without a loose file.

## Features

- Open `.wta`, `.dat`, `.dtt`, `.wtb`, `.xt1`; a `.dat` pairs automatically with its `.dtt`
  (and vice versa), and a split `.wta` automatically pairs with its sibling `.wtp`.
- Explorer thumbnails for loose `.xt1` textures (`--register-shell`): a real shell handler decodes
  each file's cheapest sufficient mip, so browsing a folder of textures shows their contents instead
  of a generic icon. See [Explorer thumbnails](#explorer-thumbnails).
- File browser over the container tree: a column list (Name / Modified / Size, click a header to sort,
  columns are drag-resizeable) of every openable file (`.dat`, `.wta`, `.wtb`, `.xt1`) in the active
  document's folder, with the selected file's bank/texture tree below it. Picking a row loads that
  file; parsing is deferred, so a folder of hundreds of large banks does not all load at once and
  switching files does not rebuild the file list. Banks reach the tree under two tags: `"wta"` (whose payload is paired from the
  `"wtp"` sub-files) and `"wtb"` (a standalone bank carrying its payload inline). A container
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
  & ".\XT1 Studio.exe" --dump-tree <file>     # tree + per-texture decode check
  & ".\XT1 Studio.exe" --dump-tree <folder>   # scan every .wta/.wtb/.dat/.xt1 recursively
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
  submenu (`.png` / `.dds` / `.xt1`), add texture from image or DDS, delete texture, copy hash, and a
  bank-level **Export every texture in this bank** submenu that writes the whole bank as a PNG, DDS or
  raw XT1 folder. Items are enabled per selection: DDS export only for formats that can be
  written (including ASTC), bank actions only when a bank is loaded, and so on.
- 2D viewer: pan/zoom, mip selection, R/G/B/A isolation, alpha checkerboard. For a 3D
  texture it shows the currently selected slice (with pan/zoom preserved while scrubbing), so one
  slice can be inspected while the GL tab shows the whole volume. Some 3D/multilayer textures are
  really 2D animation sheets with one frame per layer, so a 3D texture also gets a layer-animation
  bar on the 2D tab: Play/Pause, an FPS field (default 15), and a time bar to scrub to a specific
  layer. Animation wraps over the layers the 3D viewport actually composed, so a volume truncated by
  the preview cap plays to its last held layer. It is hidden for regular 2D and cubemap textures,
  which have no frames to play.
- GL viewport:
  - **Cubemap**: an inside-out skybox. Entering the view starts level and forward (it does not
    inherit the volume's orbit angles). Drag or the arrow keys look around (drag right turns right,
    drag up looks up). The look uses `Ry(yaw)*Rx(pitch)` - pitch in the camera's own frame, then yaw
    about world up - so the camera's right vector is always horizontal and the horizon cannot roll or
    flip; pitch is capped ~10 degrees short of vertical. (The volume's orbit camera keeps the
    `Rx(pitch)*Ry(yaw)` form it needs to swing an object upright.) A drag that reaches the viewport
    edge is wrapped back to the middle so a long turn does not stall; wheel changes the field of
    view; double-click or Home resets. The shared channel selector isolates R/G/B/A here too, and explicit
    **Gamma** / **Exposure** controls appear in the preview bar; because they drive the sky render they
    are shown for cubemaps only (a volume shows slice/render/slice-plane controls instead). **Auto
    exposure** (on by default) normalises the sky to a middle-grey key from the cubemap's own
    luminance before those controls, so gamma and exposure are applied after it and never feed back
    into it.
  - **3D texture**: the whole volume assembled into one ray-marched object. The camera orbits it —
    the target stays at the origin for every angle, drag swings the object round, wheel dollies in
    and out, double-click resets. A faint bounding box is drawn so the object reads even where it is
    empty.
  - **Render modes** (`RGB`, `Alpha`, `MIP`): `RGB`/`Alpha` are alpha-blended volume renders whose
    opacity comes from the RGB luminance or from the stored alpha; `MIP` is a maximum-intensity
    projection. Blended opacity is normalized by the ray's path through the box, so a flat volume
    (256x256x16 crosses only a sliver of its box) is as visible as a cubic one. The toolbar
    `Opaque Preview` toggle and the render-mode combo are two views of the same "ignore alpha" choice, so
    the button applies in 3D too; a volume whose alpha is uniform defaults to `RGB`, because alpha
    carries no density there.
  - **Channel isolation** (`R`/`G`/`B`/`A`) applies to the cubemap, the volume and its slices, not
    just the 2D viewer.
  - **Slice planes** (off by default) overlay the three axis cross-sections; the slice control moves
    them and switches the 2D tab to that slice.
  - Volumes are assembled whole; only a volume larger than 512 slices or 64M texels is truncated, and
    the status line says so when that happens.

- Import/replace from an image or a DDS (a DDS is copied in losslessly with its own mips; adding a
  DDS works too). The image import dialog offers **Solidify** (edge-pad the RGB of fully transparent
  texels from the nearest opaque colour, so filtering/BC compression does not leave a dark halo) and
  **Force opaque** (set alpha to 255); both work with either encoder since they act on the decoded
  image, and when both are ticked solidify runs first (while the cutout still exists) and the opaque
  conversion second. The bundled NVTT CLI has no solidify switch, so this is the equivalent of its GUI's
  "Edge Pad (Solidify)" pass. Also: add/delete textures, per-texture export to PNG / DDS / raw `.xt1` (a DDS export of
a 3D volume carries every depth slice of each level and writes `resourceDimension = TEXTURE3D`), and a bank- or
  container-wide PNG/DDS/XT1 export. All of it is in the tree's right-click menu.
- **File → New empty bank...** authors a fresh `.wta`+`.wtp` (split bank, the layout most of the game
  uses) or standalone `.wtb` with no textures. An empty bank still enables *Add texture from image...*
  in the tree menu so it can be filled.
- **Tools → Export textures from folder...**: writes every texture in a folder of
  `.dat`/`.wta`/`.wtb`/`.xt1` banks to PNG, DDS or raw XT1, with an optional recursive scan. The output
  is either one folder for everything (same-named banks get a numeric suffix) or one subfolder per bank
  mirroring the source tree, and both the loaded document's container and a single bank branch offer the
  same three formats from the tree menu.
- **Tools → Batch recompress (ASTC to BC)**: re-encodes ASTC textures to a chosen format, keeping the
  existing mip levels (none are regenerated) and only touching textures that decode cleanly. Opaque and
  alpha/transparent textures can target different formats; the dropdowns list every encodable format
  (BC1–BC7, BC6H, RGBA8/BGRA8/L8 and every ASTC footprint), and each family's sRGB or UNORM variant
  follows the source so a mixed bank is handled in one pass. Optional half-size downscale (drops mip 0
  when a chain exists, otherwise box-filters a single-mip image, with a checkbox to skip images that
  have no mips). Runs on the loaded document or over a folder of `.dat`/`.wta`/`.wtb`/`.xt1` files with
  an optional recursive scan.
- WTB sRGB/cubemap flags can be kept in step with a texture's format on add/replace (on by default),
  and new textures default to having a CRC32 hash (the "Has CRC32 hash" flag).
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
- **nvtt_export CLI** — fallback; `--help` is probed once and the required flags are verified, and
  the capability table reports which formats each backend covers instead of letting them disagree.
- **Managed** (`BCnEncoder.Net` for BC1–BC5/BC7, `AstcSharp` for ASTC, raw copy) — zero external
  dependencies; this is what decodes previews and what encodes when NVTT is missing.

Both backends share one XT1→format mapping (`ManagedFormatMap`), one transparency test
(`RgbaImage.HasTransparency`) and one sRGB curve (`Srgb`), so they cannot drift apart.

`Prototype.py`'s stale format table (e.g. `0x38` labeled ASTC) is **not** used; `0x38` is RGBA8
sRGB, and the real table comes from `XT1_FORMAT.md`.

## Known limitations

- **3D editing** is view-only. Volumes decode correctly (depth-interleaved) and preview assembled in
  the 3D viewport, but re-encoding a volume is rejected rather than approximated.
- BC6H encoding is NVTT-only: the managed path reports it as unsupported instead of writing the
  wrong blocks, so without NVTT it is refused rather than silently mis-encoded. Only the tool paths
  NVTT actually consumes is configurable (`nvtt30205.dll`, in **Tools → Settings**; leave it blank to
  auto-detect).
- Alpha / binary-alpha and the `0x2C` textureFlag semantics are still unknown; they are exposed as
  free-form editable fields and preserved verbatim rather than interpreted.
- No known unexplained layout mismatches remain: the former wide-pitch (multi-mip ASTC) and
  tiny-cube classes are computed exactly, and the stored-size pitch back-solve no longer fires on
  the known corpus. A record whose stored size still disagrees is surfaced as a warning with an
  override, and the base mip still previews from the stored size.
- Payloads shorter than the computed surface decode with zero-fill and a warning rather than
  throwing (malformed/truncated banks stay loadable).
- `XT1 Studio.exe --selfcheck` runs the headless integration check (native tools, codecs, golden
  bank load, byte-identical no-edit save, a save→reload validation that re-encodes a texture at a
  new size and verifies every `table0` offset, a `--thumbnail` pass that exports a real texture as a
  loose `.xt1` and runs the command line over it, and a shell-handler pass that initializes
  `Xt1ThumbnailProvider` from that file and checks the 32bpp DIB it returns — plus that the COM host
  is deployed beside the app, which is what `--register-shell` needs).
- A thumbnail never costs a full decode when a cheap mip will do: `--thumbnail` (and the shell host
  that calls it) decodes the smallest level that still covers the request, so a 4096-wide texture is
  a 256-wide decode rather than a full one plus a downscale.
- Unloading NVTT is idempotent (`Interlocked.Exchange` on the handle). The encoder owns and frees the
  library it was built on, and saving settings used to free the same handle again through the raw
  `NvttNative`, which threw `InvalidOperationException` out of `NativeLibrary.Free` — the OK button
  in Settings crashed with NVTT loaded. `--uitest` now exercises the settings-apply path too.
