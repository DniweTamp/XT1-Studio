# Change log

## Unreleased — add textures to a bank from a loose `.xt1`

"Add texture" only accepted common images (re-encoded) and DDS (lossless), while the loose-`.xt1`
form the tool itself writes — "Export .xt1...", the Explorer thumbnails — could only be used to
*replace* an existing texture. A texture you can export you can also add back.

* **Core** (`Editing/TextureOperations.cs`): new `BuildFromXt1` parses a raw `.xt1` blob and wraps
  it as a new editable texture — header and payload copied verbatim, no decode or re-encode, any
  texture type the blob describes (2D, cube, 3D). A blob without the `XT1\0` magic, a header-only
  file, or a payload shorter than the header's `imageSize` is rejected with a reason.
* **Shell**: the bank context menu's add item is now "Add texture from image, DDS or .xt1..." and
  its picker takes `.xt1` files; the loose form carries no table2/table3, so the same add-time
  conventions as image/DDS adds apply (name from the file name, CRC32 hash, model-bank class bit,
  sRGB/cube bits synced from the format when the setting is on).
* **CLI**: `--file add` takes `file.xt1` as the input the same way (`--name` still applies);
  format/encoder options are ignored for it, as they already are for DDS.

### Tests

* `tests/Xt1.Core.Tests/AddFromXt1Tests.cs`: header/payload verbatim (2D and cube, cube flag sync),
  the three rejection cases, the add/undo command, and a full save/reload of a split bank with the
  add-time conventions applied.

## Unreleased — trim trailing padding from saved `.wtp` streams

A saved split bank (`.wta`/`.wtp`) wrote its payload stream back exactly as loaded, padding and all.
A stream copied out of a container can carry the container's whole trailing pad — one reported case
was an `es0610.wtp` of 11,583,488 bytes where only 5,423,104 are referenced by `table0`/`table1`, so
the file came out ~2x too big even though its offset table was correct (every gap already matched
`align4096(size)`, byte-for-byte identical to a repack by other tools).

* **Payload streams** (`Editing/BankWriter.cs`): when an untouched bank's stored stream is written
  back to a loose `.wtp`, it is now cut to `max(offset + length)` — the end of the last payload,
  past which no table entry points. Referenced payload bytes are never touched, an already-exact
  stream is unchanged, and a dirty bank still rebuilds exactly as before.
* **Opt-out**: `WriteOptions.TrimTrailingPadding = false` (CLI `--no-trim-padding`) keeps the loaded
  stream byte-for-byte.
* **DAT documents are unaffected**: the container keeps its stored layout (file length, descriptors
  and inter-member padding) by design, and trimming inside it would not shrink the `.dat`/`.dtt` —
  only alter their bytes.

### Tests

* `tests/Xt1.Core.Tests/PayloadPaddingTests.cs`: padded stream is trimmed to the referenced end and
  stays byte-identical there; the opt-out keeps it; an unpadded stream round-trips byte-for-byte; an
  empty bank's stream is never trimmed. Full suite: 549 passing.

## Unreleased — Bayonetta 1 / Bayonetta 2 support

Additions made to `Xt1.Core` for the Bayonetta mod manager. Bayonetta 3 behaviour is
unchanged and its tests are untouched.

### DAT containers (`Containers/DatFile.cs`, `Containers/DatHashMap.cs`, `Util/Mem.cs`)

* **Byte order.** New `ByteOrder` enum and order-parameterised `Mem` helpers. `DatFile.Parse`
  auto-detects little- vs big-endian from header sanity checks (little-endian preferred; the
  Wii U ports are the only big-endian data) and `DatFile.ByteOrder` exposes the result. Empty
  containers (`count == 0`) are accepted.
* **Member hash map.** The `off_hash_map` table at header word 0x18 is now parsed into
  `DatFile.HashMap` (`crc32(name) & 0x7FFFFFFF`, int16 bucket ranks / u32 hashes / u16 indices,
  offsets relative to the table). It is preserved byte-for-byte and rebuilt only when the member
  set changes.
* **Alignment.** `DatFile.Alignment` (per extension/tag) plus `DefaultAlignment` control the
  repack layout. Default 0x10 (Bayonetta 3 `.dat`); `DatFile.PlatinumAlignments()` returns the
  Bayonetta 1/2 table (`wmb/wtb/wtp/eff/sdx/exp 0x1000`, `wta/bxm/sop 0x40`, `dat 0x2000`,
  default 0x10), from Kerilk's BSD-2 `bayonetta_tools`.
* **Members.** `DatFile.Add` / `DatFile.Remove` append and drop members (`Names` lists them).

Format facts verified against the real games: Bayonetta 1 PC is little-endian with header word
0x18 = 0 (no hash map); Bayonetta 2 Wii U is big-endian with a hash map; Bayonetta 2 Switch is
little-endian with a hash map; Bayonetta 3 is little-endian with a hash map.

### Texture banks (`Banks/TextureBank.cs`, new)

`TextureBank` is a replace-only, format-agnostic bank model (parse, list, export one, import one,
rebuild) for the mod manager. It covers:

* **Bayonetta 1 (PC)** — `WTB\0` version 0, DDS textures stored inline, no hashes (matched by
  index), no infos or mipmap tables; payload alignment 0x1000.
* **Bayonetta 2 (Wii U)** — `\0BTW` big-endian version 1, a 0xC0 GX2 record per texture in the
  infos table, pixel/mipmap data in the paired stream, a mipmap-offset table at header word 0x20,
  hash-matched; alignment 0x2000. Export synthesizes the portable `Gfx2`/`BLK{` GTX container and
  import splits it back, copying GX2 surface bytes verbatim (no swizzle).
* **Bayonetta 2 (Switch)** — `WTB\0` version 1, complete BNTX blobs in the paired stream,
  hash-matched; alignment 0x2000.

Import rewrites only the target texture's descriptor/data/offset/size; the flags table, the hash
table and every other texture are carried over byte-for-byte. The Bayonetta 3 XT1 bank keeps its
richer interactive editor (`Containers/WtbBank.cs`, `EditableTexture`); the two share
`Util/Mem`, the masked-CRC32 hash and the table walk rather than duplicating loop logic.

### API additions used by the mod manager

* `WtbBank.BuildSplit(streamBase)` rebuilds a split bank after a texture's header and payload are
  replaced: it recomputes `table0` (4096-aligned) and returns the paired payload stream.
* `TextureBank.HasMagic` and `TextureBank.Detect` expose bank detection to callers.
* `EditableTexture.Header.Raw` remains the mutable 0x38-byte buffer an importer writes into.

### Fixes

* DAT: when the hash-map offset (header word 0x18) is 0 (Bayonetta 1 PC has no member hash map),
  the "extra blob" no longer swallows the whole header, which had corrupted any structural rebuild.

### Other projects

None needed. `Xt1Tool.Wpf` (and the rest of `Xt1Tool.sln`) build and pass unchanged: the only API
change was adding an optional byte-order parameter to `DatFile.Parse`.

### Tests

`tests/Xt1.Core.Tests/DatMultiGameTests.cs` and `TextureBankTests.cs`: real Bayonetta 1
`core.dat` and `pre_texture.wtb`, real Bayonetta 2 Wii U `wp0020.dat` / `wp0020.wta` / `wp0020.wtp`,
plus synthetic hash-map and BNTX fixtures. Real-file tests skip silently when the samples are
absent. Full suite: 520 passing.
