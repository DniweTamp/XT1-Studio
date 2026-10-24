# XT1 Studio

Windows viewer and editor for PlatinumGames Bayonetta 3 `WTB`/`XT1` texture banks.

Supported containers: `.wta` + `.wtp`, `.dat` + `.dtt`, standalone `.wtb`, raw `.xt1`.

![XT1 Studio](assets/xt1-studio-main-screenshot.jxl)

## Features

**Viewing**

- Opens `.wta`, `.dat`, `.dtt`, `.wtb`, `.xt1`. A `.dat` pairs automatically with its `.dtt` (and vice versa); a split `.wta` pairs with its sibling `.wtp`.
- 2D viewer: pan/zoom, mip selection, R/G/B/A channel isolation, alpha checkerboard.

**Editing**

- Add, delete and replace textures from an image or a DDS. A DDS is copied in losslessly with its own mips.
- Image import offers **Solidify**: edge-pads the RGB of fully transparent texels from the nearest opaque color.
- sRGB/cubemap flags follow the texture format on add/replace (default on). New textures default to the "Has CRC32 hash" flag.
- Undo/redo across field edits, raw byte edits, add/delete and re-encode.
- Unchanged sub-files, tables and reserved bytes are written back byte-for-byte.
- **File → New empty bank...** creates a `.wta` + `.wtp` pair or a standalone `.wtb` with no textures.

**Export**

- Per texture: PNG, DDS or raw `.xt1`. Bank-wide and container-wide export use the same formats. Import, add, delete and export are in the container tree's right-click menu.
- **Tools → Export textures from folder...** exports every texture from a folder of `.dat`/`.wta`/`.wtb`/`.xt1` files, with optional recursive scan.

**Batch recompress (ASTC to BC)**

- **Tools → Batch recompress** converts ASTC textures to a BC format.
- Existing mips are reused, not regenerated. Textures that fail to decode are skipped.
- Opaque and alpha textures can target different formats.
- Optional half-size: drops mip 0 when the texture has a mip chain, otherwise box-filters the single level. A checkbox skips textures that have no mips.
- Runs on the open document or on a folder, with optional recursive scan.

**Explorer integration**

- Thumbnails for loose `.xt1` files. See [Explorer thumbnails](#explorer-thumbnails).

## Requirements

- Windows x64
- .NET 8 SDK to build, .NET 8 Desktop Runtime to run a framework-dependent build
- Optional: NVIDIA Texture Tools 3.2.5 (`nvtt30205.dll`), see [Encoders](#encoders)

## Build and run

```powershell
dotnet build Xt1Tool.sln
dotnet test  tests\Xt1.Core.Tests\Xt1.Core.Tests.csproj
dotnet run   --project src\Xt1.App.Wpf\Xt1.App.Wpf.csproj
```

The tool is portable. `settings.json` (backend, defaults, tool paths, last opened folder) is stored next to the executable.

## Encoders

| Backend | Formats | Notes |
|---|---|---|
| NVTT (`nvtt30205.dll`) | BC1-BC7, BC6H, all 14 ASTC footprints | Preferred when available |
| Managed (`BCnEncoder.Net`, `AstcSharp`) | BC1-BC5, BC7, ASTC, raw copy | No external dependencies. Also used for previews |

NVTT lookup order: path set in **Tools → Settings**, beside the exe, NVIDIA install directories, `PATH`. Default install path: `C:\Program Files\NVIDIA Corporation\NVIDIA Texture Tools`. If it is not found, the managed encoder is used.

## Command line

`--file <op>` runs the file menu's operations without a window. `--help` lists all commands.

```powershell
& ".\XT1 Studio.exe" --file export bank.dat out\ --as png
& ".\XT1 Studio.exe" --file import bank.dat replacement.png --index 3 --format BC7_UNORM
& ".\XT1 Studio.exe" --file add bank.dat icon.png --bank 0 --name ui_icon
& ".\XT1 Studio.exe" --file delete bank.dat --hash 0x482960F9
& ".\XT1 Studio.exe" --file recompress bank.dat --rule bc1-bc3 --quality highest   # see --help for rules and quality levels
```

## Release publish

```powershell
.\publish-release.bat                  # default; runs a self-check on the result
.\publish-release.bat /clean           # empty the output folder first
.\publish-release.bat /bundle-natives  # natives folded into the exe; only the exe and shell\ remain
.\publish-release.bat /bundle-nvtt     # also copy nvtt30205.dll (~100 MB)
.\publish-release.bat /nopause
```

Managed assemblies are bundled into `XT1 Studio.exe`. In the default mode `glfw3.dll` sits beside it and the thumbnail host lives in `shell\`.

## Explorer thumbnails

```powershell
& ".\XT1 Studio.exe" --register-shell             # current user
& ".\XT1 Studio.exe" --register-shell --machine   # all users (elevated)
& ".\XT1 Studio.exe" --unregister-shell [--machine]
```

Also available in **Settings → Explorer**.

- Loose `.xt1` files only. Banks keep their icon.
- Requires the .NET 8 runtime; if it is missing there are no thumbnails and no error.
- Explorer caches thumbnails per file. Restart Explorer or clear the cache to refresh.

## Known limitations

- BC6H encoding requires NVTT.
