# XT1 texture format (PlatinumGames engine, Bayonetta 3)

Reverse-engineered from shipped data and the decompressed `main` NSO. Everything was checked against real samples unless marked **[inferred]**, which means the data fits but is not proven byte for byte. All integers are little-endian.

---

## Orientation

There is no standalone `.xt1` file. A texture is described by a 56-byte (`0x38`) `XT1` header stored in a `WTB` texture bank. Its pixels live in a separate payload stream. A bank holds many textures, each with its own `XT1` header.

The `WTB` carries five parallel tables, one entry per texture: payload offset (`table0`), payload size (`table1`), flags (`table2`), CRC32 name hash (`table3`), and the `XT1` header.

The engine has two parallel file systems, both stored inside `.pkz` archives:

| System | Header bank | Payload | `table3` hash | Used by |
|---|---|---|---|---|
| Effect | `<n>.wta` (a raw `WTB`) | `<n>.wtp` | absent | `effect/**` |
| Model / UI / stage | `<n>.dat` (a `DAT`; the `WTB` is its sub-file tagged `"wta"`) | `<n>.dtt` (a `DAT`; its sub-file tagged `"wtp"` is the payload stream) | present | `ba/`, `bg/`, `bh/`, `pl/`, `ui/`, `miscTex/`, ... |

The two files of a pair share a basename. Standalone `.wtb` files also exist, with the payload inline (§7.4).

---

## 1. Format enum (`XT1` offset `0x1C`)

### Uncompressed and BC

| Value | Format | Block | Bytes/block |
|---|---|---|---|
| `0x01` | 8-bit single channel, **uncompressed** (`0x14` high half is `0x6661`, `"af"`) | 1x1 | 1 |
| `0x25` | R8G8B8A8_UNORM | 1x1 | 4 |
| `0x38` | R8G8B8A8, uncompressed, sRGB-tagged. **Not ASTC** | 1x1 | 4 |
| `0x42` | BC1_UNORM | 4x4 | 8 |
| `0x43` | BC2_UNORM | 4x4 | 16 |
| `0x44` | BC3_UNORM | 4x4 | 16 |
| `0x45` | BC4_UNORM **[inferred]** | 4x4 | 8 |
| `0x46` | BC1_SRGB | 4x4 | 8 |
| `0x47` | BC2_SRGB | 4x4 | 16 |
| `0x48` | BC3_SRGB | 4x4 | 16 |
| `0x49` | BC4_SNORM **[inferred]** | 4x4 | 8 |
| `0x50` | BC6H_UF16, used for HDR cubemaps **[inferred]**. A 16 bpp uncompressed HDR format also fits the byte count | 4x4 | 16 |
| `0x6D` | 32 bpp uncompressed, 3D textures only; channel order unconfirmed **[inferred]** | 1x1 | 4 |

Format `0x01` payloads are not compressed despite the `"af"` tag. Lay them out like any other format with 1 byte per pixel. Whether the channel is intensity or alpha is unconfirmed; render it as grayscale.

### ASTC

UNORM is `0x79`-`0x86` and sRGB is `0x87`-`0x94`, so sRGB = UNORM + `0x0E`. Every ASTC format uses 16 bytes per block. Block sizes follow the enum in order:

| UNORM | sRGB | Block |
|---|---|---|
| `0x79` | `0x87` | 4x4 |
| `0x7A` | `0x88` | 5x4 **[inferred]** |
| `0x7B` | `0x89` | 5x5 |
| `0x7C` | `0x8A` | 6x5 **[inferred]** |
| `0x7D` | `0x8B` | 6x6 |
| `0x7E` | `0x8C` | 8x5 **[inferred]** |
| `0x7F` | `0x8D` | 8x6 **[inferred]** |
| `0x80` | `0x8E` | 8x8 |
| `0x81` | `0x8F` | 10x5 **[inferred]** |
| `0x82` | `0x90` | 10x6 **[inferred]** |
| `0x83` | `0x91` | 10x8 **[inferred]** |
| `0x84` | `0x92` | 10x10 |
| `0x85` | `0x93` | 12x10 **[inferred]** |
| `0x86` | `0x94` | 12x12 (`0x94` never observed) |

Rows marked **[inferred]** were never observed. They follow the regular enum order.

---

## 2. `XT1` header (56 bytes, `0x38`)

| Offset | Type | Field | Notes |
|---|---|---|---|
| `0x00` | char[4] | magic | `"XT1\0"` |
| `0x04` | u32 | constant | Always `0x01000101`. Preserve |
| `0x08` | u64 | `imageSize` | Block-linear total for the whole mip chain (all 6 faces for cubes), including padding (§8). Not `w*h*bpp`. Equals `table1[i]` |
| `0x10` | u32 | `headerSize` | Always `0x38`. Preserve |
| `0x14` | u32 | mip count + codec | `mipCount = value & 0xFFFF`. Never use the full word. High half is `0`, except `0x6661` (`"af"`) on format `0x01` |
| `0x18` | u32 | `textureType` | §3 |
| `0x1C` | u32 | `format` | §1 |
| `0x20` | u32 | `width` | Base width in pixels |
| `0x24` | u32 | `height` | Base height in pixels |
| `0x28` | u32 | `depth` | 1 for 2D and cube; slice count for 3D |
| `0x2C` | u32 | `textureFlag` | `0x00` or `0x20`. Varies per texture, independent of format, type, mip count, sRGB and bank class. Meaning unknown. It does not track alpha (binary and smooth alpha textures occur with both values), so it is not an alpha-clip flag. Preserve |
| `0x30` | u32 | `textureLayout` | §4 |
| `0x34` | u32 | `textureLayout2` | Always `0x00010007`. Preserve |

---

## 3. `textureType` (`0x18`)

```
0 1D            1 2D             2 3D             3 Cube          4 1D array
5 2D array      6 2D multisample 7 2D multisample array           8 Cube array
```

Only `1`, `2` and `8` occur. Cubes are stored as type `8` with 6 faces, never as `3`. 3D textures use the `depth` field.

---

## 4. `textureLayout` (`0x30`)

| Bits | Meaning |
|---|---|
| `0x07` | `blockHeightLog2` of mip 0. Block height in 8-row GOBs is `1 << value` |
| `0x70` | `blockDepthLog2` **[inferred]**. `0` on every 2D record; `4` (block depth 16) on every 3D record |
| `0x400` | With `blockHeightLog2 == 3`, can select the wide row pitch (§8.1) |
| `0x100`, `0x200` | Unknown |

When editing a header, preserve the whole word unless you regenerate it.

---

## 5. `table2` flags

Four bytes per texture, read as raw bytes `[b0][b1][b2][b3]`. `b1` and `b2` are always `0`.

| Byte | Bit | Meaning |
|---|---|---|
| `b0` | `0x20` | Texture has a CRC32 hash in `table3` (model, UI and character banks). `0x00` in effect banks |
| `b3` | `0x60` | Base bits, always set |
| `b3` | `0x04` | sRGB (duplicates the format's sRGB bit) |
| `b3` | `0x08` | Set on format `0x01` records |
| `b3` | `0x80` | Cubemap / env-map usage |

The distinct `b3` values are `0x60` (linear 2D), `0x64` (sRGB 2D), `0x68` (format `0x01`), `0xE0` (linear cube) and `0xE4` (sRGB cube). Read as a big-endian u32 the whole field is `0x20000060`, `0x20000064`, and so on; read as a little-endian u32 it is `0x60000020`, and so on.

`b3 & 0x80` is a usage flag, not a copy of `textureType`: every type-`8` model texture in a BC format sets it, but a handful of type-`8` records in the core bank do not. Use `textureType` to identify cubes. `table2` has no alpha bit.

---

## 6. Alignment and constants

| What | Value |
|---|---|
| Payload slot in the stream (`table0`) | 4096 |
| Mip surface within a texture | 512 |
| Row pitch | 64 bytes (GOB width); 1024 in wide-pitch mode (§8.1) |
| Cube face stride | 4096 if the base mip surface is at least 4096 bytes, otherwise dense (§8.2) |
| `WTB` table size | Multiple of 32 bytes (§7.3) |
| First `"wtp"` sub-file inside a model `.dtt` | `0x1000` (§7.2) |

Mixing these shifts every following surface.

Constants to preserve on any rewritten header: `0x01000101` at `0x04`, `0x38` at `0x10`, `0x00010007` at `0x34`, plus `textureFlag`, `textureLayout` (unless regenerated) and `table2`.

---

## 7. Containers

### 7.1 `.pkz` archive

```
struct PkzHeader {              // 0x20 bytes
    char   magic[4];            // "pkzl"
    uint32 version;             // 0x00020000
    uint64 file_size;           // real file size
    uint32 flags;
    uint32 entry_count;
    uint32 table_offset;        // base for name_offset / comp_offset
    uint32 unk;
};
// followed by entry_count 32-byte entries:
struct PkzEntry {
    uint32 name_offset;         // + table_offset + entry_count * 0x20
    uint32 comp_offset;         // + table_offset + entry_count * 0x20
    uint64 decompressed_size;
    uint64 data_offset;
    uint64 compressed_size;
};
```

Strings are NUL-terminated and padded to 8 bytes. The `comp` string is `"OodleKraken"` or `"None"`; read it per entry. `.wtp` and `.dtt` payload files are Oodle Kraken compressed, so Oodle is required to read pixels. `.wta` and many `.dat` files are stored raw (`"None"`).

### 7.2 `DAT` container (`.dat` and `.dtt`)

`.dat` and `.dtt` are the same container; only the extension differs.

```
0x00 char[4] "DAT\0"
0x04 u32     subfile_count
0x08 u32     off_subfile_offsets   -> u32[subfile_count] file offsets of each sub-file
0x0C u32     off_tags              -> char[4][subfile_count]
0x10 u32     off_names             -> u32 slot_size, then subfile_count names of slot_size bytes each
0x14 u32     off_descriptors       -> per-sub-file descriptors (first words are sizes)
0x18 u32     off_extra             -> another table (unknown)
0x1C u32     0
```

Sub-files are identified by 4-char tag, never by index:

| Tag | Content |
|---|---|
| `"wta"` | The `WTB` |
| `"wtp"` | The pixel payload stream |
| `"wmb"`, `"bxm"`, `"uvd"`, `"epb"`, `"bnk"`, `"rbf"`, `"sop"` | Unrelated to textures |

* Scan the tag table. `"wta"` is often not the first sub-file, and `"wtp"` is not guaranteed to be first either.
* A `DAT` can hold several `"wta"` sub-files, with the matching `.dtt` holding several `"wtp"`. Pair them in order **[inferred]**.
* The names table names the sub-files (for example `xxx.wta`), not the textures inside a bank.
* In every observed model `.dtt`, the first `"wtp"` sub-file starts at `.dtt` offset `0x1000`; a later one (the UI core bank has a second) starts elsewhere. Always read the offset from the offset table. `WTB` `table0` offsets are relative to the start of the `"wtp"` sub-file that holds the bank's payload, not the `.dtt`.
* When repacking, update the sub-file offset table, the descriptor size of the changed `"wta"` or `"wtp"` sub-file, the offsets of every following sub-file, and the `.pkz` entry sizes.

### 7.3 `WTB` texture bank

Offsets are relative to the start of the `WTB` (the `.wta` file, or the `"wta"` sub-file inside a `.dat`).

```
0x00 char[4] "WTB\0"
0x04 u32     version = 3
0x08 u32     texture_count
0x0C u32     table0_offset   -> u32[count] payload offsets
0x10 u32     table1_offset   -> u32[count] payload sizes (== XT1 imageSize)
0x14 u32     table2_offset   -> u32[count] flags (§5)
0x18 u32     table3_offset   -> u32[count] CRC32 name hashes; 0 = table absent (effect banks)
0x1C u32     xt1_offset      -> count * 0x38-byte XT1 headers, back to back
```

Each table is 4-byte entries padded to a multiple of 32 bytes. With `T = round_up(count * 4, 32)` the usual layout is:

```
0x20          table0
0x20 + T      table1
0x20 + 2T     table2
0x20 + 3T     table3     (only if table3_offset != 0)
0x20 + 4T     XT1[0]     (0x20 + 3T when table3 is absent)
```

The end of the `XT1` array is rounded up to 8 **[inferred]**. Always use the stored offsets rather than assuming this stride.

* **`table0`**: offset of texture *i*'s payload inside the payload stream. `table0[0] == 0` and `table0[i+1] == align4096(table0[i] + table1[i])`.
* **`table1`**: payload size, equal to `XT1[i].imageSize`.
* **`table2`**: flags, §5.
* **`table3`**: name hash, §7.5.

Size checks: `align4096(max(table0[i] + table1[i]))` equals the decompressed payload stream size. For effects that stream is the whole `.wtp`. For a model `.dtt` with a single `"wtp"` sub-file, the decompressed `.dtt` is `4096 + stream size`.

### 7.4 Standalone `.wtb`

Some banks ship as a single `.wtb` (for example `core/core_dat/CubeMap.wtb`). It is the same `WTB` with the payload appended inline:

* The header, tables and `XT1` region are zero-padded to `0x1000`, and the payload starts there.
* `table0` entries are absolute file offsets, so the first is `0x1000`. In the split forms `table0[0]` is `0`.

To build a `.wtb` from a `.wta`+`.wtp` (or `.dat`+`.dtt`) pair: pad the `WTB` to `0x1000`, append the payload stream, and add `0x1000` to every `table0` entry. To split one, subtract `0x1000` from `table0` and write the payload out from `0x1000` onward.

### 7.5 Name hash

`table3[i] = crc32(texture_name) & 0x7FFFFFFF`, stored little-endian. Use the texture name without extension and clear bit 31.

```python
def name_hash(name: str) -> int:
    return zlib.crc32(name.encode("ascii")) & 0x7FFFFFFF
```

Materials bind textures by this hash. A renamed or added texture needs it recomputed, or copy the hash the material already uses. Packed banks do not store individual texture names, so the hash is the only identifier available.

---

## 8. Surface layout

Each texture's payload is its mip chain, swizzled in Tegra X1 block-linear (GOB) order. Every mip surface starts on a 512-byte boundary. For 2D textures `imageSize` is the end of the last surface; cubes add a face stride (§8.2).

### 8.1 Per-mip layout (2D)

```python
def round_up(x, y):               # y must be a power of two
    return ((x - 1) | (y - 1)) + 1

def div_up(n, d):
    return (n + d - 1) // d

def mip_layout(width, height, mip_count, layout, blk_w, blk_h, bpb):
    """Returns ([(offset, pitch, block_height, surface_size), ...], image_size).

    width, height : base size in pixels
    layout        : XT1 textureLayout word
    blk_w, blk_h  : block size in pixels (1x1 for uncompressed formats)
    bpb           : bytes per block
    """
    bhl = layout & 7                              # blockHeightLog2 of mip 0
    bh0 = 1 << bhl
    wb0 = div_up(width, blk_w)
    wide = ((layout & 0x400) != 0 and bhl == 3
            and (wb0 * bpb) % 1024 != 0)          # tile_width_spacing = 4 mode
    bpb_log2 = bpb.bit_length() - 1

    mips, size = [], 0
    for m in range(mip_count):
        wb = div_up(max(1, width  >> m), blk_w)
        hb = div_up(max(1, height >> m), blk_h)

        bh = bh0                                  # driver block-height reduction
        while bh > 1 and hb <= (bh // 2) * 8:
            bh //= 2

        # The width term is [inferred]: bpb_log2 = log2(bytes per block) is assumed,
        # and no known texture depends on it. The height term is verified.
        small = (wb * bpb <= (1 << (10 - bpb_log2))) or hb <= 8 * bh0
        pitch = round_up(wb * bpb, 1024 if (wide and not small) else 64)

        size = round_up(size, 512)                # each mip starts 512-aligned
        surface = pitch * round_up(hb, 8 * bh)
        mips.append((size, pitch, bh, surface))
        size += surface
    return mips, size                             # size == XT1.imageSize
```

* The per-mip block height must follow the driver rule above (`mip_block_height` in `tegra_swizzle`). A constant-threshold shift matches only the sizes the game ships.
* **Wide pitch** (`tile_width_spacing = 4`) is a per-record property. It applies only when layout bit `0x400` is set, `blockHeightLog2 == 3`, and the base row (`wb0 * bpb`) is not a multiple of 1024. Every mip that is not `small` then rounds its pitch to 1024 instead of 64. Global "align rows to 512 bytes" and "pad width to 64 blocks" rules are wrong.
* The pitch used for sizing must also be passed to the swizzler (§8.4). Sizing with the wide pitch but swizzling with the narrow one corrupts the image.
* For 3D records, `small` also holds when the mip's depth is below `1 << blockDepthLog2` **[inferred]**.

### 8.2 Cubemaps (`textureType == 8`)

A cube stores 6 faces, each a full mip chain, concatenated with a fixed stride. The stride is 4096-aligned only when the face's **base mip surface** (not the whole chain) is at least 4096 bytes. Smaller chains are stored densely.

```python
def cube_image_size(mips, face_size):
    base_surface = mips[0][3]
    stride = round_up(face_size, 4096) if base_surface >= 4096 else face_size
    return 6 * stride, stride                     # face k starts at k * stride
```

`mips` and `face_size` come from `mip_layout` for one face.

### 8.3 3D textures (`textureType == 2`) **[inferred]**

Z is interleaved: `block_depth` slices stack into one block, so each slice is not an independent 2D surface. `block_depth` for mip 0 is `1 << blockDepthLog2` (layout bits `0x70`), halved per mip like the block height. Variables below are per mip.

```python
bd = 1 << ((layout >> 4) & 7)
depth_m = max(1, depth >> m)
while bd > 1 and depth_m <= bd // 2:
    bd //= 2

gobs_per_row = pitch // 64
slice_size = div_up(hb, 8 * bh) * 512 * bh * bd * gobs_per_row
surface    = div_up(depth_m, bd) * slice_size

def offset_3d(x, y, z):                           # x in blocks, y in rows
    xb = x * bpb
    gob_z = (z // bd) * slice_size + (z % bd) * 512 * bh
    gob_y = ((y // (8 * bh)) * 512 * bh * bd * gobs_per_row
             + ((y % (8 * bh)) // 8) * 512)
    gob_x = (xb // 64) * 512 * bh * bd
    return (gob_z + gob_y + gob_x
            + ((xb % 64) // 32) * 256 + ((y % 8) // 2) * 64
            + ((xb % 32) // 16) * 32 + (y % 2) * 16 + (xb % 16))
```

### 8.4 Block-linear addressing (2D)

Only block-linear tile mode is used.

```python
def block_linear_offset(x, y, pitch, bpb, block_height):
    # x, y in blocks; pitch = row pitch in bytes (multiple of 64)
    gobs_per_row = pitch // 64
    xb = x * bpb
    gob = ((y // (8 * block_height)) * 512 * block_height * gobs_per_row
           + (xb // 64) * 512 * block_height
           + ((y % (8 * block_height)) // 8) * 512)
    return (gob + ((xb % 64) // 32) * 256 + ((y % 8) // 2) * 64
            + ((xb % 32) // 16) * 32 + (y % 2) * 16 + (xb % 16))
```

For each mip, use that mip's `pitch` and `block_height` from `mip_layout`, and its 512-aligned `offset`. Iterate `y in [0, hb)` and `x in [0, wb)`, moving `bpb` bytes between the linear offset `(y * wb + x) * bpb` and `offset + block_linear_offset(x, y, pitch, bpb, block_height)`.

`tegra_swizzle` (ported from Ryujinx `BlockLinearLayout` and the Tegra TRM) is the reference implementation.

---

## 9. Recipes

### 9.1 Extract

1. In the `.pkz`, find the header side (`<n>.wta` or `<n>.dat`) and the payload side (`<n>.wtp` or `<n>.dtt`). Inflate any entry whose `comp` is `"OodleKraken"`.
2. Effects: the `WTB` is the whole `.wta` and the stream is the whole `.wtp`. Models: parse each `DAT` (§7.2), take the `"wta"` sub-file as the `WTB` and the `"wtp"` sub-file as the stream.
3. Parse the `WTB` (§7.3): `count`, `table0`-`table3`, `xt1_offset`.
4. For each texture *i*: parse `XT1[i]` (§2). The payload is `stream[table0[i] : table0[i] + table1[i]]`.
5. Compute the mip layout (§8), deswizzle each mip (§8.4; read 6 faces for type `8`), and decode by format. Format `0x01` is raw bytes.
6. `table3[i]` is the texture's hash. Resolve it to a name through the materials, or keep it as the key.

### 9.2 Replace a texture (keep format)

1. Encode the image to the target format (BC1/BC3/BC6H/ASTC/RGBA8). `astcenc` produces ASTC.
2. Build the mip chain (base `>> m` down to 1x1), or a single level, and set `mipCount`.
3. Choose `blockHeightLog2` for the new size the way `tegra_swizzle`'s `block_height_mip0` does, write it into `textureLayout`, and swizzle each mip with the layout from §8.1. `imageSize` is the total.
4. Repack the texture's slot in the payload stream at `align4096`. Update `table0` for every following texture and set `table1[i] = imageSize`.
5. Update `XT1[i]` (width, height, mip count, format, `imageSize`, `textureLayout`). Keep the constants, `textureFlag` and `table2` consistent with the new format.
6. Rebuild the `WTB` inside its `"wta"` sub-file. Update the `DAT` offset and size tables for the `"wta"` sub-file (`.dat`) and the `"wtp"` sub-file (`.dtt`), keeping the `0x1000` payload start. Recompress each payload side with Oodle Kraken and update the `.pkz` entry sizes.

### 9.3 Add a texture

1. Pick a name (the material must reference it by hash), a format and dimensions.
2. Set `table3[i] = crc32(name) & 0x7FFFFFFF` and `table2`: `b0 = 0x20` in hash banks, `b3 = 0x60`, plus `0x04` for sRGB, `0x08` for format `0x01` and `0x80` for cubes.
3. Insert the `XT1` header, the table entries and the payload (4096-aligned). Update `count` and every offset.
4. For a new file, add the `.wta`/`.wtp` (or `.dat`/`.dtt`) entries to the `.pkz`. For a `DAT`, also add the sub-file to the `DAT` tables.

---

## 10. Unknowns

* `textureLayout` bits `0x100` and `0x200`.
* The meaning of `textureFlag` (`0x2C`). A candidate is the material usage slot.
* Whether `table2` `b3 & 0x80` means "cube" or "referenced as env map", and whether character, enemy and weapon banks use other `table2` bits.
* Whether format `0x50` is BC6H_UF16 or a 16 bpp uncompressed HDR format.
* Channel meaning of format `0x01` (intensity or alpha) and channel order of format `0x6D`.
* The ASTC sizes never seen in the game (§1).
* How several `"wta"`/`"wtp"` sub-files in one `DAT` pair up, and whether `"wtp"` is ever not the first sub-file.
* The `"uvd"` sub-file and the exact `DAT` descriptor array layout.
* Where alpha test and blend are stored. It is not the texture header; the material (`bxm`/`wmb`) is the likely place.
* Surface sizing for 3D textures with mips (§8.3).
