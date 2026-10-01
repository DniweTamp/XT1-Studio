# XT1 texture format (PlatinumGames engine, Bayonetta 3)

Reverse-engineered from shipped data (`D:\Bayonetta3\romfs\*.pkz`) and the decompressed
`main` NSO. Everything marked **[verified]** was checked against real samples (2 171 XT1
records in 324 effect banks, plus 3 810 model/UI banks). **[inferred]** items fit the data
but are not byte-for-byte proven. Scratch parsers live in `%TEMP%\kilo\`.

> This spec deliberately does **not** use Timo654's `XT1_tool`. Its format table and header
> model are wrong (see §13), so it is only useful as a first glance.

---

## 1. Overview

There is no standalone `.xt1` file. A texture is described by a 56-byte **`XT1` header**
that lives in a **`WTB` "texture bank"**, and its pixels live in a separate compressed
payload stream. The engine has **two parallel file systems** that both use `WTB`/`XT1`:

| System | Header bank | Payload | Hash table | Used by |
|---|---|---|---|---|
| Effect | `<name>.wta` (raw `WTB`) | `<name>.wtp` (Oodle) | absent (`0x18 == 0`) | `effect/**` |
| Model / UI / stage | `<name>.dat` (a `DAT`; the `WTB` is the sub-file tagged `"wta"`) | `<name>.dtt` (a `DAT`; its sub-file tagged `"wtp"` is the payload stream) | present | `ba/`, `bg/`, `bh/`, `pl/`, `ui/`, `miscTex/`, ... |

Both live inside `.pkz` archives (§2). The `.wta`/`.wtp` and `.dat`/`.dtt` pairs share a
basename. A bank holds many textures (e.g. `effect/texture/004.wta` = 15, `ba/ba1f41.dat`
= 40), each with its own `XT1` header.

`.dat` and `.dtt` are **the same container format** (both start with `DAT\0`); only the
extension differs. Sub-files inside a `DAT` are identified **by 4-char tag, not by index**:
a `WTB` is `"wta"`, the pixel payload stream is `"wtp"`, and other tags (`"wmb"`, `"bxm"`,
`"uvd"`, `"epb"`) are unrelated to textures. The `"wta"` sub-file is usually first but this
must not be assumed.

The `WTB` carries five parallel index tables, one entry per texture:
**payload offset, payload size, flags, CRC32 name-hash, XT1 header**.

---

## 2. `.pkz` archive

```
struct PkzHeader {
    char   magic[4];       // "pkzl"
    uint32 version;        // 0x00020000      (offset 0x04)
    uint64 file_size;      // == real size     (offset 0x08)
    uint32 flags;          //                  (offset 0x10)
    uint32 entry_count;    //                  (offset 0x14)
    uint32 table_offset;   // base for name/comp offsets (offset 0x18)
    uint32 unk;            //                  (offset 0x1C)
};
// then entry_count * 32-byte entries:
struct PkzEntry {
    uint32 name_offset;       // + table_offset + entry_count*0x20  (offset 0x00)
    uint32 comp_offset;       // + table_offset + entry_count*0x20  (offset 0x04)
    uint64 decompressed_size;                                  // (offset 0x08)
    uint64 data_offset;                                        // (offset 0x10)
    uint64 compressed_size;                                    // (offset 0x18)
};
```

Strings are NUL-terminated, padded to 8 bytes. `comp` is `"OodleKraken"` or `"None"`.
`.wta`/`.dat` (when `None`) are readable directly; `.wtp`/`.dtt` are Oodle-compressed and
need `oo2core_*.dll` (Kraken) to inflate.

---

## 3. `DAT` container (`.dat` and `.dtt`)

`.dat` and `.dtt` are the same container. Sub-files are found by **tag**, so scan the tag
table and match `"wta"` / `"wtp"` rather than reading entry 0. Observed header **[verified]**:

```
0x00 char[4] "DAT\0"
0x04 u32     subfile_count    (2 or 3 observed; number of tags/offsets)
0x08 u32     off_subfile_offsets   -> u32[subfile_count] file offsets of each sub-file
0x0C u32     off_tags              -> char[4] * subfile_count  ("wta","wtp","wmb","bxm","uvd","epb")
0x10 u32     off_names             -> u32 slot_size, then subfile_count names, slot_size bytes each
0x14 u32     off_descriptors       -> per-sub-file descriptor array (first words = sizes)
0x18 u32     off_extra             (another table, nonzero)
0x1C u32     0
0x20 ...     sub-file offsets (this is where off_subfile_offsets points)
```

Example `ba/ba1f41.dat` (3 680 bytes): sub-file offsets `[0x80, 0xBE0]`, tags `"wta","bxm"`,
names `"ba1f41.wta"`, `"ObjectParam.bxm"`, WTB at `0x80`, `ObjectParam.bxm` at `0xBE0`.
The `"wta"` sub-file is the `WTB`. Other sub-files are unrelated to textures:
`"wmb"` (`WMB3` model), `"bxm"` (`ObjectParam` binary-XML), `"uvd"` (unknown).

For a model the payload side is a `.dtt` whose `"wtp"` sub-file holds the pixel stream;
`WTB` `table0` offsets are relative to that `"wtp"` sub-file, not to the `.dtt` file.
**[verified on decompressed files]**:

```
pl/pl0000.dtt   : DAT ver 1, tags ['wtp'],          sub=['0x1000']
em/em0000.dtt   : DAT ver 1, tags ['wtp'],          sub=['0x1000']
wp/wp0020.dtt   : DAT ver 1, tags ['wtp'],          sub=['0x1000']
ui/ui_core.dtt  : DAT ver 2, tags ['wtp','wtp'],    sub=['0x1000','0x7d6000']
```

So the payload starts at `.dtt` offset **0x1000**, which is why
`decompressed .dtt == 4096 + payload_total` (§11). `"wtp"` may not be the first sub-file.

**The `"wta"` sub-file is often not first** — find it by tag. Verified examples:
`em/em0000.dat` tags `['bxm','bnk','bxm','wta', ...]` (`"wta"` at index 3);
`ui/ui_core.dat` tags `[..., 'wta','wta', ...]` (`"wta"` at indices 7 and 8);
`pl/pl0000.dat` `['wta','bnk','bxm','rbf','wmb','sop']` (index 0);
`wp/wp0020.dat` `['wta','bxm','bxm','wmb']` (index 0).

When repacking a `DAT`, update: the sub-file offset table, the descriptor size of the `"wta"`
(and/or `"wtp"`) sub-file, the following sub-file offsets, and the pkz entry sizes.

---

## 4. `WTB` texture bank

Offsets are relative to the start of the `WTB` (i.e. relative to the `.wta`, or relative to
the `WTB` inside a `.dat`). **[verified]**

```
off   type      field
0x00  char[4]   "WTB\0"                    (0x00425457 LE)
0x04  u32       version == 3
0x08  u32       texture_count
0x0C  u32       table0_offset  -> u32[count]  payload offsets
0x10  u32       table1_offset  -> u32[count]  payload sizes (== XT1 imageSize)
0x14  u32       table2_offset  -> u32[count]  flags (see §9)
0x18  u32       table3_offset  -> u32[count]  CRC32 name-hash (0 = absent, effect banks)
0x1C  u32       xt1_offset     -> count * 0x38-byte XT1 headers, back-to-back
```

`T = round_up(count*4, 32)`. Each table is 4-byte entries padded to a 32-byte multiple, so:

```
0x20            table0
0x20 + T        table1
0x20 + 2T       table2
0x20 + 3T       table3        (only if table3_offset != 0)
0x20 + 4T       XT1[0]        (or 0x20+3T when table3 absent)
round_up(end, 8)
```

For effect banks `table3_offset == 0`, so the table is omitted and `XT1` follows table2.
For model banks the table is present. (Both layout forms were observed; use the stored
offsets, do not assume a fixed stride.)

### table0 — payload offsets **[verified]**
LE `u32`. Offset of texture *i*'s payload **within the payload stream**: the `.wtp` file for
effects, or the `"wtp"` sub-file inside the `.dtt` for models. `table0[0] == 0`; each
following payload is aligned: `table0[i+1] = align4096(table0[i] + table1[i])`.

### Standalone `.wtb` files **[verified]**
Some banks ship as a single `.wtb` (e.g. `core/core_dat/CubeMap.wtb`, `Grading.wtb`;
`wet/WetNoiseTex.wtb`). It is the same `WTB` structure with the payload stream **appended
inline**, but it is *not* a plain `wta || wtp` byte concat:

* the header/tables/XT1 region is **zero-padded to 0x1000**, and the payload begins there;
* `table0` offsets are then **rebased to absolute file offsets** (first entry is `0x1000`,
  not `0`), whereas in the split `.wta`+`.wtp` / `.dat`+`.dtt` forms `table0[0] == 0`.

`CubeMap.wtb` (544 768 B): `count=11`, XT1 ends at `0x388`, bytes `0x388..0x1000` are zero,
payload at `0x1000`, `table0 = [0x1000, 0xD000, 0x19000, …]`, sizes 49 152, no inter-payload
gaps. `Grading.wtb` (53 248 B): same, payload at `0x1000`.

So to turn a `wta`+`wtp` (or `dat`+`dtt`) pair into a standalone `.wtb`: take the `WTB`,
pad it to `0x1000`, append the payload stream, and add `0x1000` to every `table0` entry.
Conversely, a `.wtb` can be split by subtracting the payload base from `table0` and writing
the payload out from `0x1000` onward.

### table1 — payload sizes **[verified]**
LE `u32`, equals the `XT1.imageSize` of the same index. `align4096(max(table0+table1))`
equals the decompressed payload stream size.

### table2 — flags **[verified bytes]**
4 bytes; read as raw bytes `[b0][b1][b2][b3]`. `b1` and `b2` are **always 0**; `b0` records
whether the record carries a CRC32 hash and `b3` is a small flag set:

| byte | bit | meaning |
|---|---|---|
| `b0` | `0x20` | has CRC32 hash (model/UI/character banks, `.dat`); `0x00` = no hash (effect banks, `.wta`) |
| `b3` | `0x60` | base bits, always set |
| `b3` | `0x04` | sRGB (duplicates the format's sRGB bit) |
| `b3` | `0x08` | format is `0x01` (`"af"`, single-channel 8 bpp). Originally read as "second-layer compressed"; `0x01` payloads are in fact uncompressed (see §8 correction) |
| `b3` | `0x80` | **cubemap/env-map usage** (see below) |

So the distinct `b3` values are `0x60` (linear 2D), `0x64` (sRGB 2D), `0x68` (compressed),
`0xE0` (linear cube), `0xE4` (sRGB cube). Read as a big-endian `u32` these are
`0x20000060`, `0x20000064`, `0x20000068`, `0x200000E0`, `0x200000E4`.

**Cube bit (`b3 & 0x80`).** This *is* a type-related flag, matching your observation:
every `type == 8` (cube array) model texture with a BC format sets it, e.g. all the 64x64
BC1/BC2/BC3 character/enemy cubemaps are `0xE4`, while the same formats as 2D are `0x64`.
(A handful of `type == 8` records in `core.dat` do not set it, so treat it as a usage flag,
not a strict copy of `textureType`.) There is **no observed alpha/binary-alpha bit** in
`table2` — see §9.

> With only `b0` and `b3` populated, `table2` is a compact legacy flag word (Wii U origin).

### table3 — CRC32 name hash **[verified]**
LE `u32`, one per texture: **`crc32(texture_name) & 0x7FFFFFFF`**.

Confirmed exactly on name-bearing banks (the name string is also stored in the `DAT`):

| name | zlib `crc32` | stored hash |
|---|---|---|
| `gallery_concept_205` | `0x421E5ED4` | `0x421E5ED4` |
| `archive_angel_mainpic_01` | `0xB128CBED` | `0x3128CBED` |
| `archive_angel_mainpic_02` | `0x28219A57` | `0x28219A57` |
| `archive_angel_footprintspic_01` | `0x67E26B85` | `0x67E26B85` |
| `archive_beast_mainpic_01` | `0xA0207052` | `0x20207052` |
| `alfheim_img_001` | `0x1FE67C86` | `0x1FE67C86` |

Use the **texture name without extension**, mask off bit 31, store little-endian. This is the
key that models/materials use to bind a texture, so a custom converter must recompute it for
any renamed/added texture (or copy the material's hash).

```python
def name_hash(name: str) -> int:
    return zlib.crc32(name.encode("ascii")) & 0x7FFFFFFF
```

Note: packed banks do not store the individual texture names, so the hash is the only name
available for those textures. The `DAT` names (`"ba1f41.wta"`) name the bank, not the
textures inside it.

---

## 5. `XT1` header

56 bytes (`0x38`), little-endian, fixed. **[verified]** on all 5 981 records parsed.

```
off   type   field
0x00  char[4] magic          "XT1\0" == 0x00315458 LE (3232856)
0x04  u32    flags            always 0x01000101
0x08  u64    imageSize        total payload bytes (see §6)
0x10  u32    headerSize       == 0x38
0x14  u32    mipCount | codec low 16 bits = mipCount; high 16 bits = 0, or "af" (0x6661) for compressed (fmt 0x01)
0x18  u32    textureType      §7
0x1C  u32    format           §8
0x20  u32    width            base width, pixels
0x24  u32    height           base height, pixels
0x28  u32    depth            base depth (1 for 2D/cube)
0x2C  u32    textureFlag      per-texture flag, 0x00 or 0x20 (see §9)
0x30  u32    textureLayout    §10 (low 3 bits = blockHeightLog2)
0x34  u32    textureLayout2   always 0x00010007
```

Example (128x128 ASTC 8x8 sRGB, 7 mips):

```
0x80: 58 54 31 00 01 01 00 01 00 1E 00 00 00 00 00 00   XT1\0  flags  imageSize=0x1E00
0x90: 38 00 00 00 07 00 00 00 01 00 00 00 8E 00 00 00   hdr=0x38 mips=7 type=1 fmt=0x8E
0xA0: 80 00 00 00 80 00 00 00 01 00 00 00 20 00 00 00   w=128 h=128 depth=1 unk4=0x20
0xB0: 01 00 00 00 07 00 01 00                            layout=1 layout2=0x00010007
```

Compressed record (fmt `0x01`, codec `"af"`): `0x14` reads `0x6661000A` -> mips 10, codec "af".

---

## 6. Payload layout, surface size, alignment

Each texture's payload is its mip chain, stored already swizzled in **Tegra X1 block-linear
(GOB)** order (decode with §11). The stored `imageSize` is the sum of per-mip block-linear
surfaces, each starting 512-byte aligned:

```python
def pow2_round_up(x):
    x -= 1
    x |= x >> 1; x |= x >> 2; x |= x >> 4; x |= x >> 8; x |= x >> 16
    return x + 1

def round_up(x, y): return ((x - 1) | (y - 1)) + 1
def div_up(n, d):   return (n + d - 1) // d

def surface_size(width, height, mip_count, layout, blk_w, blk_h, bytes_per_block):
    bhl   = layout & 7                 # blockHeightLog2 (see §10)
    bh0   = 1 << bhl                   # mip 0 block height, in 8-row GOBs
    size  = 0
    for m in range(mip_count):
        w = max(1, width  >> m)
        h = max(1, height >> m)
        wb = div_up(w, blk_w)
        hb = div_up(h, blk_h)
        # Driver block-height reduction (Ryujinx AdjustTileSize / tegra_swizzle
        # mip_block_height): halve the block height while the mip still fits the lower half.
        bh = bh0
        while hb <= (bh // 2) * 8 and bh > 1:
            bh //= 2
        lines  = 8 * bh
        pitch  = round_up(wb * bytes_per_block, 64)
        surface = pitch * round_up(hb, lines)
        size = round_up(size, 512)     # each mip starts 512-aligned
        size += surface
    return size
```

> The per-mip `bh` must be the driver rule above, not a constant-threshold "shift": the two
> coincide only for the sizes Bayonetta 3 ships, and diverge for arbitrary custom geometries
> (e.g. a 130-block-tall mip). A texture swizzled with the wrong per-mip height round-trips in
> the tool but deswizzles its lower mips wrong in the game, which is why the tool now ports
> `block_height_mip0`/`mip_block_height` from `tegra_swizzle` directly.

**[verified]** against data, e.g. (all reproduced exactly):

| format | w | h | mips | layout | formula | `imageSize` |
|---|---|---|---|---|---|---|
| `0x8E` | 256 | 256 | 8 | 2 | 24064 | 24064 |
| `0x8E` | 512 | 512 | 9 | 3 | 89600 | 89600 |
| `0x87` | 256 | 256 | 8 | 0x403 | 89088 | 89088 |
| `0x44` | 2048 | 1024 | 1 | 4 | 2097152 | 2097152 |
| `0x44` | 1024 | 128 | 1 | 2 | 131072 | 131072 |
| `0x38` | 512 | 512 | 1 | 0x304 | 1048576 | 1048576 |
| `0x84` | 1024 | 1024 | 10 | 4 | 288256 | 288256 |
| `0x89` | 2048 | 2048 | 6 | 4 | 4511744 | 4511744 |

**Resolved — the "ASTC anomaly" is Tegra `tile_width_spacing = 4`.** ASTC `0x7d`/`0x8b` (6x6) at
1024x1024/10 mips stores `imageSize = 915456` where the plain formula yields 772096, and
`em305c.dat` #2 (2050x2050 ASTC 5x5, 11 mips) stores 4596736 where the plain formula yields
4092928. Both are the square-tile driver mode. The trigger is a record-level property (a texture
cannot vary it per mip): **layout bit `0x400` is set, `blockHeightLog2 == 3`, and the base mip's row
(`widthBlocks * bytesPerBlock`) not divisible by 1024**. When it fires, every mip that is *not*
"small" rounds its row pitch up to a full 16 GOBs (1024 bytes) instead of 64:

```python
wide_pitch = ((texture_layout & 0x400) != 0
              and blkLog2 == 3
              and ((wb0 * bytes_per_block) % 1024 != 0))

# per mip, replacing the pitch line in surface_size() above:
is_small = (wb * bytes_per_block <= (1 << (6 - bpp_log2 + 4))   # width term, in bytes
            or hb <= (1 << (3 + blkLog2))                       # height term, in rows
            or (is_3d and depth < (1 << block_depth_log2)))     # depth term, 3D only
pitch = round_up(wb * bytes_per_block, 1024 if wide_pitch and not is_small else 64)
```

The later mips of a chain fall into `is_small` (a 64-block height already fits a single 16-GOB-tall
block), so only the first few mips are widened. This reproduces the whole `em305c` chain exactly:

| mip | 0 | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 | 9 | 10 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| pitch | 7168 | 4096 | 2048 | 832 | 448 | 256 | 128 | 64 | 64 | 64 | 64 |
| surface | 3 211 264 | 1 048 576 | 262 144 | 53 248 | 14 336 | 4 096 | 1 024 | 512 | 512 | 512 | 512 |
| offset | 0 | 3 211 264 | 4 259 840 | 4 521 984 | 4 575 232 | 4 589 568 | 4 593 664 | 4 594 688 | 4 595 200 | 4 595 712 | 4 596 224 |

total **4 596 736**. Mip 0's pitch is `round_up(410 * 16, 1024) = 7168`; mip 3 has `hb = 52`, so it
is small and keeps `round_up(52 * 16, 64) = 832`.

The **47 single-mip pitch recoveries** are the same mechanism (`pitch = round_up(row, 1024)`): the
formula now computes them directly, so back-solving the pitch from the stored `imageSize` is only
a fallback and no longer fires on the known corpus. The magenta-padding evidence still holds and
is the definitive check for any candidate geometry: `ui_core.dat`'s `core.wta` #1 (1026x1026 ASTC
6x6, hash `0x482960F9`) is 589824 with a 3072-byte pitch — decoding it with 3072 leaks **6**
magenta pixels, with the formula's 2752 it leaks **78,594**. The wide pitch must be fed to the
swizzle, not just to the size maths.

**Note on the pitch rule:** a global "align the row to 512 bytes" was tested against every record
and is wrong (669/10317 matches versus 10161/10317 for the 64-byte rule). Likewise a global
"pad the width in blocks to a multiple of 64" is wrong (447/10317); it is the
`tile_width_spacing = 4` mode above, gated by the row trigger and the per-mip `is_small` test, that
fits the whole class. At the end of the census 10 304/10 304 non-cube records and all 13
previously-flagged BC1_SRGB 64x64 cubes match, leaving **0 unexplained mismatches**.

Alignment summary:

* `XT1` header 56 bytes; `mipCount`-many surfaces, each started on **512**.
* Payload within `.wtp`/`.dtt` starts on **4096**.
* `pitch` rounded to **64** bytes (GOB width), or to **1024** when the record uses
  `tile_width_spacing = 4` (see above).
* `blockHeightLog2` from `textureLayout & 7`.

### Cubes / arrays
`textureType == 8` (cube array). The payload holds **6 faces**, each a full mip chain, concatenated
with a stride that is **4096-aligned only when the face's base mip (mip 0) surface reaches 4096
bytes**:

```python
face_stride = align4096(face_size) if base_mip_surface >= 4096 else face_size
image_size  = 6 * face_stride
```

The alignment is a property of the **base mip**, not of the total face size. BC1 (`0x46`) 64x64/7
mips has a 5120-byte chain but only a 2048-byte base mip, so the six 5120-byte faces are packed
densely (`6 * 5120 = 30720`); BC3 (`0x48`) 64x64/7 mips has a 4096-byte base mip, so its 7680-byte
chain is padded to 8192 (`6 * 8192 = 49152`). An earlier rule keyed off `face_size <= 4096`, which
wrongly page-aligned the BC1 chains. All observed groups:

| format / size | base mip surface | face size | stored per face | rule |
|---|---|---|---|---|
| 8x8 BC3s | 512 | 512 | **512** | dense (`6*512 = 3072`) |
| 64x64 BC1s / 7 mips | 2048 | 5120 | **5120** | dense (`6*5120 = 30720`) |
| 64x64 BC3s / 7 mips | 4096 | 7680 | 8192 | `align4096` |
| 64x64 BC6H / 7 mips | 4096 | 7680 | 8192 | `align4096` |
| 64x64 RGBA8s | 16384 | 23552 | 24576 | `align4096` |
| 256x256 BC6H / 9 mips | 65536 | 89600 | 90112 | `align4096` |

A face whose base mip surface is exactly 4096 (e.g. BC3 64x64 with one mip, or RGBA8 64x64) *is*
aligned. Getting the rule wrong reads every face except the first from padding, which is what made
the tiny 8x8 cube records and the BC1 64x64 chains look like unusable anomalies. Reading a face at
`face * 4096` also drags the magenta padding fill into the image, the same signature as the §6
pitch error.

---

## 7. Texture type (`0x18`) **[verified]**

```
0 T_1D   1 T_2D   2 T_3D   3 T_Cube   4 T_1D_Array
5 T_2D_Array   6 T_2D_Multisample   7 T_2D_Multisample_Array   8 T_Cube_Array
```

Observed: `1` (2D), `2` (**3D** — e.g. `core.dat` has 128x128x128/8mip, 256x256x16, 16x16x32), and `8` (cube array). Cubes are stored as **cube array (8)**, not `3`. `depth` is used for 3D; the `table2` `b3 & 0x80` bit tracks cube usage (§4).

---

## 8. Format enum (block geometry) **[verified]**

The engine uses an NVN-aligned enum. Block geometry was solved by matching `imageSize`
against §6 across the whole-game census; sRGB vs UNORM is confirmed by the +14 offset
between paired values and by table2 `b3`.

### Uncompressed / BC

| value | format | block | bytes/block | seen |
|---|---|---|---|---|
| `0x01` | single 8-bit channel, uncompressed (`"af"` codec tag; see correction below) | 1x1 | 1 | yes |
| `0x25` | `R8G8B8A8_UNORM` uncompressed | 1x1 | 4 | yes (all types) |
| `0x38` | `R8G8B8A8` uncompressed (sRGB-tagged) | 1x1 | 4 | yes |
| `0x42` | `BC1_UNORM` | 4x4 | 8 | yes (pl/es) |
| `0x43` | `BC2_UNORM` | 4x4 | 16 | yes |
| `0x44` | `BC3_UNORM` | 4x4 | 16 | yes (most common) |
| `0x45` | `BC4_UNORM` | 4x4 | 8 | not seen |
| `0x46` | `BC1` sRGB | 4x4 | 8 | yes |
| `0x47` | `BC2` sRGB | 4x4 | 16 | yes |
| `0x48` | `BC3` sRGB | 4x4 | 16 | yes |
| `0x49` | `BC4_SNORM` | 4x4 | 8 | not seen |
| `0x50` | `BC6H_UF16` (HDR; cubemaps here) | 4x4 | 16 | yes |
| `0x6d` | uncompressed 32 bpp, **3D only** (16x16x32); channel order unconfirmed | 1x1 | 4 | yes |

### ASTC — UNORM `0x79..0x86`, sRGB `0x87..0x94` **[verified for the listed sizes]**

The two blocks are identical in ordering, exactly `+0x0E` apart:

| UNORM | sRGB | block | BT3 |
|---|---|---|---|
| `0x79` | `0x87` | 4x4 | 38 / 200 |
| `0x7a` | `0x88` | 5x4 | (not seen) |
| `0x7b` | `0x89` | 5x5 | 10 / 8 |
| `0x7c` | `0x8a` | 6x5 | (not seen) |
| `0x7d` | `0x8b` | 6x6 | 10 / 13 |
| `0x7e` | `0x8c` | 8x5 | (not seen) |
| `0x7f` | `0x8d` | 8x6 | (not seen) |
| `0x80` | `0x8e` | 8x8 | 97 / 1869 |
| `0x81` | `0x8f` | 10x5 | (not seen) |
| `0x82` | `0x90` | 10x6 | (not seen) |
| `0x83` | `0x91` | 10x8 | (not seen) |
| `0x84` | `0x92` | 10x10 | 44 / 34 |
| `0x85` | `0x93` | 12x10 | (not seen) |
| `0x86` | `0x94` | 12x12 | 12 / 0 |

Directly verified sizes: 4x4 (`0x79`/`0x87`), 5x5 (`0x7b`/`0x89`), 6x6 (`0x7d`/`0x8b`),
8x8 (`0x80`/`0x8e`), 10x10 (`0x84`/`0x92`), 12x12 (`0x86`). The
`5x4/6x5/8x5/8x6/10x5/10x6/10x8/12x10` rows are the regular gaps in the enum and are
**[inferred]** (never observed in Bayonetta 3; `0x2d`/`0x3a` sizes reported by other tools
were not seen here). This confirms the enum is UNORM `0x79..0x86` then sRGB `0x87..0x94`,
14 block sizes each, in the order `4x4,5x4,5x5,6x5,6x6,8x5,8x6,8x8,10x5,10x6,10x8,10x10,12x10,12x12`.

All ASTC formats use 16 bytes per block.

### `0x01` single-channel 8-bit (`"af"`)

> **CORRECTION (2026-09-17, verified against all 16 `0x01` records in the game):** `0x01` is
> **not compressed**. It is an uncompressed single 8-bit channel at **1 byte per pixel**, and §6
> applies normally with `bytesPerBlock = 1`. The three observed `imageSize` values below, which
> were read as "compressed sizes", are exactly what §6 produces:
>
> | record | dims/mips | layout | §6 with bpb=1 | stored |
> |---|---|---|---|---|
> | `ui_core*.dat` #2 | 1024x1024 / 1 | `0x205` | 1 048 576 | 1 048 576 |
> | `055.wta` #21,#22 | 128x128 / 8 | `0x4` | 25 600 | 25 600 |
> | `055.wta` #14 | 8x512 / 10 | `0x4` | 66 560 | 66 560 |
>
> bpb = 2 and 4 do not fit the first row (2 097 152 / 4 194 304). Deswizzling with bpb = 1 and
> the layout's `blockHeightLog2` lowers the plane's 2D total variation by 3.5x versus the raw
> byte order, and sweeping `blockHeightLog2` 0..7 makes the layout's low 3 bits the unique
> variation minimum — so the low-3-bits rule of §9 holds for `0x01` too. Whether the single
> channel is intensity or alpha is still unconfirmed; it is rendered as grayscale.

`0x14` reads `mipCount | (codec << 16)` with `codec == 0x6661` = `"af"`. Payload is located by
the same table0/table1. **[resolved — not compressed]**

---

## 9. `textureLayout` (`0x30`) **[mostly verified]**

**Low 3 bits = `blockHeightLog2`** (Tegra block height); substituting them into §6 reproduces
`imageSize` exactly. Observed values `0x0,0x1,0x2,0x3,0x4,0x304,0x403`. Bit `0x400` selects the
`tile_width_spacing = 4` wide-pitch class described in §6. Bits `0x100` and `0x200` remain
unconfirmed — **preserve the whole word verbatim**.

> **ADDITION (2026-09-17): bits 4..6 are `blockDepthLog2`.** They are `0` for every 2D record and
> `4` (= block depth 16) for every 3D record observed (`layout == 0x40`). 2D surfaces are
> unaffected, but 3D textures need it — see the §10 correction. The claim above about the high
> bits still holds for `0x100`/`0x200`; it was simply never checked for `0x40`.

`textureLayout2` (`0x34`) is the constant `0x00010007`; `unknown@0x04` is `0x01000101`;
`headerSize` is `0x38`. Preserve all three.

### `textureFlag` (`0x2C`) — the real per-texture flag
This is the **only header field that varies per texture beyond type/format.** It is `0x20`
or `0x00` and is independent of format, type, mips, sRGB and bank class. Proof it is a real
per-texture flag and not a bank/format constant:

* `effect/texture/402.wta` holds two records that are **byte-identical except for this
  field** (`fmt 0x8E`, 256x256, 8 mips, same `imageSize`, same `table2`): index 0 = `0x20`,
  index 1 = `0x00`.
* `pl/pl0080.dat` / `pl/pl0081.dat` set it (`0x20`) **exactly** on their anomalous
  ASTC 6x6 1024x1024 records, while neighbouring same-format 512x512 records are `0x00`.

Distribution over readable records (including the decompressed character/enemy/weapon banks):
`0x00` ~4 900, `0x20` ~2 900. It correlates with neither `table2`, the DAT tag set,
dimension, nor the format's alpha channel.

**Alpha test:** decoding the base mip of every BC3/RGBA8/BC1 texture in the decompressed
banks and classifying alpha as binary (`{0,255}`) vs. smooth showed **no correlation** with
this flag: e.g. `0x42` (BC1_UNORM) is binary both with `flag=0x00` (17) and `flag=0x20`
(108); `0x25` (RGBA8_UNORM) has smooth alpha with `flag=0x00` (14). So `0x2C` is **not** the
alpha-clip flag. Binary-alpha/alpha-test is a **material** property in this engine, not a
texture-header flag.

`table2` (`0x14`) *does* carry a cube usage bit (`b3 & 0x80`), but no alpha bit either. So:
type info = `textureType` (`0x18`) plus the `table2` cube hint; alpha-clip = material-side.

---

## 10. Tegra X1 block-linear swizzle

Decode/encode surfaces of `wb x hb` blocks with `bytes_per_block` bytes per block:

```python
def get_addr_block_linear(x, y, width_blocks, bpp, base, block_height):
    # x, y in blocks; width_blocks = image width in blocks; bpp = bytes per block
    image_width_in_gobs = (width_blocks * bpp + 63) // 64
    gob = (base
           + (y // (8 * block_height)) * 512 * block_height * image_width_in_gobs
           + (x * bpp // 64) * 512 * block_height
           + (y % (8 * block_height) // 8) * 512)
    px = x * bpp
    return (gob + ((px % 64) // 32) * 256 + ((y % 8) // 2) * 64
            + ((px % 32) // 16) * 32 + (y % 2) * 16 + (px % 16))
```

* `block_height = 1 << blockHeightLog2` for mip 0, reduced per mip by the driver rule in §6.
* Iterate `y in [0,hb)`, `x in [0,wb)`, and move `bpp` bytes between linear
  `(y*wb + x)*bpp` and `get_addr_block_linear(x, y, wb, bpp, 0, block_height)`.
* Only **block-linear (tileMode 0)** is used; the pitch-linear path is not needed.

> **CORRECTION / ADDITION (2026-09-17): 3D surfaces interleave Z, and the formula above is 2D only.**
> A GOB is 64x8 bytes and `block_height` stacks that many GOBs vertically; a matching
> **`block_depth`** stacks that many *slices* into the same block. For 3D textures Z is therefore
> interleaved, not concatenated, and treating each slice as its own 2D surface produces a visible
> block-tile pattern on every slice.
>
> Use the reference implementation (`tegra_swizzle`, itself ported from Ryujinx's
> `BlockLinearLayout` and Tegra TRM v1.3 page 1217-1218):
>
> ```
> slice_size     = ceil(height_blocks / (8*block_height)) * 512 * block_height * block_depth * width_in_gobs
> gob_address_z  = (z / block_depth) * slice_size + (z % block_depth) * 512 * block_height
> gob_address_y  = (y / (8*block_height)) * 512 * block_height * block_depth * width_in_gobs
>                + ((y % (8*block_height)) / 8) * 512
> gob_address_x  = (x_bytes / 64) * 512 * block_height * block_depth
> in_gob_offset  = ((x % 64)/32)*256 + ((y % 8)/2)*64 + ((x % 32)/16)*32 + (y % 2)*16 + (x % 16)
> ```
> with `block_depth = clamp(block_depth(mip_depth), ...)`, `block_depth(d) = 16/8/4/2/1` chosen by
> `d + d/2` reaching 16/8/4/2, and halved while `mip_depth <= block_depth/2`.
>
> For XT1 the block depth is stored explicitly: **bits 4..6 of `textureLayout` are
> `blockDepthLog2`** — `0` for every 2D record, `4` (= 16) for every 3D record observed. Note this
> refines the §9 statement that the high bits do not affect the math: bits 4..6 do, for 3D.
>
> Verified on `core.dat`'s 128x128x128 RGBA8 volume: mean absolute neighbour difference drops from
> 13.1 (in-plane) / 36.8 (slice-to-slice) with a depth-agnostic deswizzle to 10.5 / 11.8 with
> `block_depth = 16`.

* Decode per mip: use that mip's `wb, hb, block_height` and its 512-aligned offset within
  the payload.

---

## 11. Compression

* **Archive layer:** payload entries (`.wtp`, `.dtt`) and other sub-files are Oodle **Kraken**.
  `.wta`, and uncompressed `.dat`, are stored raw. `pkz` `comp` names the codec per entry.
* **Effect system:** the `.wtp` file *is* the payload stream; `decompressed .wtp ==
  align4096(Σ payloads)` (e.g. `004.wtp` = 826 368).
* **Model system:** the `.dtt` is a `DAT` whose `"wtp"` sub-file is the payload stream. The
  stream begins at `.dtt` offset **0x1000**, so `decompressed .dtt == 4096 + payload_total`
  (e.g. `ba1f41.dtt` = 2 741 760 = 4096 + 2 737 664; `gallery .dtt` = 2 101 248 = 4096 +
  2 097 152). The `WTB` `table0` offsets are relative to the `"wtp"` sub-file, i.e. subtract
  the `"wtp"` sub-file offset (0x1000 here) before indexing. **[inferred from sizes]**
* **`XT1` codec `"af"` (format `0x01`)**: **corrected** — a single uncompressed 8-bit channel at
  1 byte per pixel, laid out by §6 with `bytesPerBlock = 1`. Not a second compression layer.
  (The `table2` `b3` `0x08` bit is still set on these records; it evidently flags the format
  rather than literal compression.)
* **Format `0x6D`**: observed only on 3D textures (16x16x32). Uncompressed 32 bpp: §6 matches the
  stored `imageSize` with 4 bytes per texel across 32 slices (1024 x 32 = 32 768), and 4 is the
  only bytes-per-texel value that fits. Channel order (RGBA vs BGRA) is unconfirmed.

---

## 12. Converter recipes

### 12.1 Extract

1. `pkz` -> find the header side (`<name>.wta` raw, or `<name>.dat` raw/Oodle) and the
   payload side (`<name>.wtp`, or `<name>.dtt`).
2. Parse `DAT` (§3) and select the sub-files **by tag**: `"wta"` is the `WTB`; `"wtp"` is the
   payload stream. Do not assume either is entry 0.
3. Oodle-decompress the payload side, then take the `"wtp"` sub-file (its byte range from the
   `DAT` offset table). For effects the whole `.wtp` is the stream.
4. Parse `WTB` (§4): `count`, `table0/1/2/3`, `xt1_offset`.
5. For each `i`: read `XT1[i]` (§5); payload = `stream[table0[i] : table0[i]+table1[i]]`;
   deswizzle each mip (§6/§10); decode blocks (BC1/BC3/BC6H/ASTC/RGBA8), or `"af"`-decompress
   for `0x01`.
6. If needed, resolve `table3[i]` back to a name using materials, or keep it as the key.

### 12.2 Inject a replacement texture (keep format)

1. Encode the image to the target block format (BC1/BC3/BC6H/ASTC/RGBA8). ASTC can be
   produced with `astcenc`.
2. Build the full mip chain (base >> m down to 1x1), or a single level; set `mipCount`
   accordingly.
3. Swizzle each mip to block-linear with the mip's `blockHeightLog2` (after the §6 shift) and
   the 512-aligned per-mip layout; concatenate. `imageSize` = sum (use §6, or copy for the
   ASTC 6x6 1024 anomaly).
4. Repack the texture's slot inside the payload stream at `align4096`; update `table0` for all
   following textures, `table1[i] = imageSize`.
5. Update `XT1[i]` (width/height/mips/format/imageSize/layout) but keep `0x04`, `0x34`,
   `unknown4` and `table2` semantics consistent.
6. Rebuild the `WTB` inside its `"wta"` sub-file. Update the `DAT` offset/size tables for the
   `"wta"` sub-file (`.dat`) and the `"wtp"` sub-file (`.dtt`), keeping the 0x1000 payload
   start. Recompress each payload side with Oodle Kraken and update the pkz entry sizes.

### 12.3 Add a brand-new texture

1. Pick a name (the material must reference it by hash), format and dimensions.
2. Compute `table3[i] = crc32(name) & 0x7FFFFFFF` (§4).
3. Insert the XT1 header, the three/four table entries, the payload (4096-aligned), and
   update `count` and all offsets.
4. If it is a new file, add `.wta`/`.wtp` (or `.dat`/`.dtt`) entries to the pkz and, for a
   `DAT`, add the sub-file to the `DAT` table too.

---

## 13. Quirks and known-wrong assumptions

1. **No standalone `.xt1`.** The format is `WTB`+`XT1` inside `.wta` (effects) or inside the
   `"wta"` sub-file of a `.dat` (models), with pixels in `.wtp` (effects) or the `"wtp"`
   sub-file of a `.dtt` (models). One bank holds many textures.
2. **`.dat` and `.dtt` are the same `DAT` container**; only the extension differs. Identify
   sub-files by 4-char tag (`"wta"`, `"wtp"`, `"wmb"`, `"bxm"`, `"uvd"`, `"epb"`), **never by
   index** — the `"wta"`/`"wtp"` sub-file is not guaranteed to be first.
3. **`0x38` is NOT ASTC** (public tables say `ASTC_8x8_UNORM`). It is uncompressed 32 bpp:
   256x256/1 = 262 144 = `256*256*4`.
4. **`0x01` is uncompressed single-channel 8 bpp** (see the correction in §8): `0x14` =
   `mips | ("af" << 16)`, `imageSize` follows §6 with `bytesPerBlock = 1`, and dimensions follow
   §6 like any other format.
5. **mipCount is `0x14 & 0xFFFF`.** Never use the full word.
6. **`imageSize` is the block-linear total**, not `w*h*bpp`; it includes GOB padding, the
   512-per-mip alignment, the 1024 row pitch when `tile_width_spacing = 4` applies (§6), and 4096
   per cube face when the face's base mip reaches a page.
7. **Per-mip block-height reduction** (the driver rule in §6; a constant-threshold "shift" is
   wrong off the shipped sizes). Without it the sizes and low-mip swizzle are wrong.
8. **The hash table (`0x18`) is real** and is `crc32(name) & 0x7FFFFFFF` (LE). It is absent in
   effect banks (`0x18 == 0`) and present in model/UI banks; materials bind textures by it.
9. **table2 is a 4-byte flag field**: `b0` = has CRC32 hash, and `b3` = `0x60` base plus
   `0x04` sRGB, `0x08` compressed, `0x80` cube-usage. `b1`/`b2` are always 0. It carries a
   cube hint but **no alpha-clip bit**; the exact 2D/cube value is `textureType` (`0x18`),
   and the per-texture flag is `0x2C` (§9). Preserve all of them.
10. **Alignments:** payload 4096, mip 512, pitch 64 (1024 in the `tile_width_spacing = 4`
    mode), cube face stride 0 or 4096. Mixing them shifts everything. For a model the payload
    stream is the `"wtp"` sub-file, which starts at `.dtt` offset 0x1000.
11. **Cubes are `type 8`**, 6 faces. The face stride is 4096-aligned only when the base mip
    surface is at least 4096 bytes; smaller chains are stored densely (§6).
12. **ASTC 6x6 at 1024x1024** is not anomalous: it is the `tile_width_spacing = 4` wide pitch
    (§6), computed exactly — do not copy the stored size.
13. **Oodle is mandatory** for payloads; only metadata is readable without it.
14. **Table padding**: tables are padded to 32-byte multiples; do not assume `count*4`.
15. **Constants** `0x01000101` (`0x04`), `0x00010007` (`0x34`), `0x38` (`0x10`) must be kept.
16. **`DAT` banks carry names** for the *bank* (not the individual textures), plus sub-files
    (`wmb`, `bxm`, `uvd`) unrelated to textures.

---

## 14. Evidence

* Effect banks: all 324 uncompressed `.wta` -> 2 171 XT1 records; stride `0x38`; invariants
  `magic x2171`, `0x04 == 0x01000101 x2171`, `headerSize == 0x38 x2171`,
  `0x34 == 0x00010007 x2171`.
* Whole-game census (3 810 model/UI/stage/effect uncompressed banks): formats
  `0x8E x1869`, `0x44 x3689`, `0x87 x200`, `0x48 x136`, `0x80 x97`, `0x38 x61`, `0x84 x44`,
  `0x79 x38`, `0x92 x34`, `0x46 x24`, `0x8B x13`, `0x50 x11`, `0x7D x10`, `0x89 x2`,
  `0x01 x3`.
* Hash: matched stored value with `crc32(name) & 0x7FFFFFFF` for six named banks (§4).
* Sizes: §6 formula reproduced `imageSize` for 4x4, 5x5, 8x8, 10x10, BC1/BC3/BC6H and RGBA8
  across hundreds of records.
* `WTB` size identity: `effect/texture/004.wta` -> 826 368 = decompressed `004.wtp`;
  `ba/ba1f41.dat` -> 2 737 664 + 4096 = decompressed `ba1f41.dtt`.
* `DAT` tag scan: in the 3 810 uncompressed `.dat` banks from pkz, tag orders were
  `("wta","uvd") x3725`, `("wta","wmb","bxm") x81`, `("wta","bxm") x4` (`"wta"` happened to be
  entry 0). On the **decompressed** loose banks `"wta"` is often elsewhere, e.g. `em0000.dat`
  index 3, `ui_core.dat` indices 7/8.
* Decompressed `.dtt` are `DAT`s whose only sub-file is `"wtp"` at `0x1000`
  (`pl0000.dtt`, `em0000.dtt`, `wp0020.dtt`, `ui_core.dtt`), confirming the payload model.
* Decompressed character/enemy/weapon banks added the missing formats
  (`0x25`, `0x42`, `0x43`, `0x47`, `0x6d`, `0x7b`, `0x86`), texture **type 2 (3D)**, and the
  `table2` `b3` values `0x68`/`0xE0`/`0xE4` (cube bit).
* Alpha test: decoded base mips of BC1/BC3/RGBA8 textures in the decompressed banks; binary
  vs. smooth alpha correlated with **neither** `table2` nor the `0x2C` flag.

---

## 15. Open questions

* Meaning of the `textureLayout` high bits (`0x100`/`0x200`/`0x400`).
* Exact meaning of the `0x2C` `textureFlag` (not alpha; candidate: material usage slot).
* `table2` — the remaining theoretical bits (only `b0`/`b3` with bits `0x04/0x08/0x80` are
  used in BT3). Whether the `b3 & 0x80` bit means "cube" or "referenced as env map".
* Where alpha-test/blend is stored (material `bxm`/`wmb`, not the texture header).
* Whether character/enemy/weapon banks (Oodle-only, not scanned) use additional `table2`
  bits; all 6 231 readable records use only `b0`/`b3`.
* The `"af"` codec for format `0x01`.
* Exact `"wtp"` sub-file offset inside model `.dtt` (0x1000 inferred from all paired sizes)
  and whether `"wtp"` is ever not the first sub-file.
* ~~The ASTC 6x6 1024x1024 `imageSize` anomaly.~~ **Resolved**: the multi-mip ASTC class is
  Tegra `tile_width_spacing = 4`; see §6.
* Whether `0x50` is `BC6H_UF16` or a 16-bpp uncompressed HDR format (both fit the byte count).
* The unobserved ASTC sizes (`0x7a/0x7b` UNORM, `0x7c`, `0x7e..0x86` etc.) — names inferred
  from enum spacing.
* `DAT` `"uvd"` sub-file and the exact `DAT` descriptor array layout.
