using Xt1.Core.Containers;
using Xt1.Core.Encoding;
using Xt1.Core.Format;
using Xt1.Core.Imaging;
using Xt1.Core.Util;
using Xt1.Core.Xt1;

namespace Xt1.Core.Editing;

/// <summary>Result of a high-level texture operation.</summary>
public sealed record OperationResult(bool Success, string? Error, string? Warning = null)
{
    public static OperationResult Ok(string? warning = null) => new(true, null, warning);
    public static OperationResult Fail(string error) => new(false, error);
}

/// <summary>
/// High-level editing operations (replace/add/delete/convert, recompute helpers, batch force).
/// Each mutation is wrapped in an <see cref="IEditCommand"/> so the caller can push it onto an
/// <see cref="EditHistory"/>.
/// </summary>
public static class TextureOperations
{
    /// <summary>
    /// Replace a 2D texture's pixels, re-encoding to its current format.
    /// <paramref name="originalHeader"/> lets the caller supply the header as it was *before* it
    /// changed the format for this encode; without it the undo snapshot would record the new format
    /// alongside the old payload, and undoing would leave a header that lies about its payload.
    /// </summary>
    public static OperationResult Replace2D(EditableTexture texture, RgbaImage image,
        ITextureEncoder encoder, bool generateMips, EncodeQuality quality, byte[]? originalHeader = null,
        bool syncFlags = false)
        => Replace2D(texture, image, encoder,
            generateMips ? MipChain.FullMipCount(image.Width, image.Height) : 1,
            quality, originalHeader, syncFlags);

    /// <summary>
    /// Replace a 2D texture with an explicit mip count. Batch replace uses this to preserve each
    /// texture's own mip count rather than regenerating a full chain; see the boolean overload for
    /// <paramref name="originalHeader"/>.
    /// </summary>
    public static OperationResult Replace2D(EditableTexture texture, RgbaImage image,
        ITextureEncoder encoder, int mipCount, EncodeQuality quality, byte[]? originalHeader = null,
        bool syncFlags = false)
    {
        var beforePayload = texture.Payload;
        var beforeHeader = originalHeader ?? texture.Header.Raw.ToArray();
        var beforeSize = texture.PayloadSize;
        var beforeOffset = texture.PayloadOffset;
        var beforeImageSize = texture.Header.ImageSize;
        var beforeMips = texture.Header.MipCount;
        var beforeW = texture.Header.Width;
        var beforeH = texture.Header.Height;
        var beforeTable2 = (byte[])texture.Table2Bytes.Clone();

        var format = texture.FormatInfo;
        if (format is null) return OperationResult.Fail("unknown format");

        var levels = MipChain.Build(image, Math.Max(1, mipCount), format.IsSrgb);
        var result = TextureEncoder.EncodeLevels(texture, levels, encoder, quality);
        if (!result.Success)
            return OperationResult.Fail(result.Error ?? "encode failed");

        // Sync before snapshotting the new state so the flag bytes are part of the undoable state;
        // otherwise Undo would restore the old payload and format but keep the new table2 flags.
        if (syncFlags) SyncFlagsFromFormat(texture);

        // Not a no-op: capture the new state so Undo can restore it.
        var afterPayload = texture.Payload;
        var afterHeader = texture.Header.Raw.ToArray();
        var afterSize = texture.PayloadSize;
        var afterImageSize = texture.Header.ImageSize;
        var afterMips = texture.Header.MipCount;
        var afterW = texture.Header.Width;
        var afterH = texture.Header.Height;
        var afterTable2 = (byte[])texture.Table2Bytes.Clone();

        _pendingCommand = new DelegateCommand("Replace texture",
            () =>
            {
                texture.Payload = afterPayload;
                afterHeader.CopyTo(texture.Header.Raw, 0);
                afterTable2.CopyTo(texture.Table2Bytes, 0);
                texture.PayloadSize = afterSize;
                texture.Header.ImageSize = afterImageSize;
                texture.Header.MipCount = afterMips;
                texture.Header.Width = afterW;
                texture.Header.Height = afterH;
            },
            () =>
            {
                texture.Payload = beforePayload;
                beforeHeader.CopyTo(texture.Header.Raw, 0);
                beforeTable2.CopyTo(texture.Table2Bytes, 0);
                texture.PayloadSize = beforeSize;
                texture.PayloadOffset = beforeOffset;
                texture.Header.ImageSize = beforeImageSize;
                texture.Header.MipCount = beforeMips;
                texture.Header.Width = beforeW;
                texture.Header.Height = beforeH;
            });

        return OperationResult.Ok();
    }

    /// <summary>
    /// Replace a texture from an already-compressed DDS, losslessly (surfaces are copied, not
    /// decoded/re-encoded) and with the DDS's own mip chain. Wrapped in an undoable command.
    /// </summary>
    public static OperationResult ReplaceWithDds(EditableTexture texture, DdsImage dds, bool syncFlags = false)
    {
        var beforePayload = texture.Payload;
        var beforeHeader = texture.Header.Raw.ToArray();
        var beforeSize = texture.PayloadSize;
        var beforeImageSize = texture.Header.ImageSize;
        var beforeMips = texture.Header.MipCount;
        var beforeW = texture.Header.Width;
        var beforeH = texture.Header.Height;
        var beforeDepth = texture.Header.Depth;
        var beforeTable2 = (byte[])texture.Table2Bytes.Clone();

        var result = TextureEncoder.ImportDds(texture, dds);
        if (!result.Success)
        {
            beforeHeader.CopyTo(texture.Header.Raw, 0);
            return OperationResult.Fail(result.Error ?? "DDS import failed");
        }

        // Sync before snapshotting so the flag bytes take part in Undo/Redo (see Replace2D).
        if (syncFlags) SyncFlagsFromFormat(texture);

        var afterPayload = texture.Payload;
        var afterHeader = texture.Header.Raw.ToArray();
        var afterSize = texture.PayloadSize;
        var afterImageSize = texture.Header.ImageSize;
        var afterMips = texture.Header.MipCount;
        var afterW = texture.Header.Width;
        var afterH = texture.Header.Height;
        var afterDepth = texture.Header.Depth;
        var afterTable2 = (byte[])texture.Table2Bytes.Clone();

        _pendingCommand = new DelegateCommand("Replace from DDS",
            () =>
            {
                texture.Payload = afterPayload;
                afterHeader.CopyTo(texture.Header.Raw, 0);
                afterTable2.CopyTo(texture.Table2Bytes, 0);
                texture.PayloadSize = afterSize;
                texture.Header.ImageSize = afterImageSize;
                texture.Header.MipCount = afterMips;
                texture.Header.Width = afterW;
                texture.Header.Height = afterH;
                texture.Header.Depth = afterDepth;
            },
            () =>
            {
                texture.Payload = beforePayload;
                beforeHeader.CopyTo(texture.Header.Raw, 0);
                beforeTable2.CopyTo(texture.Table2Bytes, 0);
                texture.PayloadSize = beforeSize;
                texture.Header.ImageSize = beforeImageSize;
                texture.Header.MipCount = beforeMips;
                texture.Header.Width = beforeW;
                texture.Header.Height = beforeH;
                texture.Header.Depth = beforeDepth;
            });

        return OperationResult.Ok();
    }

    /// <summary>Build a brand-new editable texture from an already-compressed DDS (lossless).</summary>
    public static EditableTexture? BuildFromDds(DdsImage dds, string? name, out string? error)
    {
        error = null;
        if (dds.Format is null)
        {
            error = "the DDS uses a format with no XT1 equivalent";
            return null;
        }

        var texture = EditableTexture.CreateNew(dds.Format.Format, (uint)dds.Width, (uint)dds.Height);
        var result = TextureEncoder.ImportDds(texture, dds);
        if (!result.Success)
        {
            error = result.Error;
            return null;
        }

        texture.Name = name;
        return texture;
    }

    /// <summary>Replace a cubemap from six faces. See <see cref="Replace2D"/> for <paramref name="originalHeader"/>.</summary>
    public static OperationResult ReplaceCube(EditableTexture texture, IReadOnlyList<RgbaImage> faces,
        ITextureEncoder encoder, bool generateMips, EncodeQuality quality, byte[]? originalHeader = null)
    {
        var beforePayload = texture.Payload;
        var beforeHeader = originalHeader ?? texture.Header.Raw.ToArray();
        var beforeSize = texture.PayloadSize;
        var beforeImageSize = texture.Header.ImageSize;

        var result = TextureEncoder.EncodeCube(texture, faces, encoder, generateMips, quality);
        if (!result.Success)
            return OperationResult.Fail(result.Error ?? "encode failed");

        var afterPayload = texture.Payload;
        var afterHeader = texture.Header.Raw.ToArray();
        var afterSize = texture.PayloadSize;
        var afterImageSize = texture.Header.ImageSize;

        _pendingCommand = new DelegateCommand("Replace cubemap",
            () =>
            {
                texture.Payload = afterPayload;
                afterHeader.CopyTo(texture.Header.Raw, 0);
                texture.PayloadSize = afterSize;
                texture.Header.ImageSize = afterImageSize;
            },
            () =>
            {
                texture.Payload = beforePayload;
                beforeHeader.CopyTo(texture.Header.Raw, 0);
                texture.PayloadSize = beforeSize;
                texture.Header.ImageSize = beforeImageSize;
            });

        return OperationResult.Ok();
    }

    [ThreadStatic] private static IEditCommand? _pendingCommand;

    /// <summary>The command produced by the last mutating operation (null when nothing changed).</summary>
    public static IEditCommand? TakePendingCommand()
    {
        var command = _pendingCommand;
        _pendingCommand = null;
        return command;
    }

    /// <summary>Produce an add-texture command (caller pushes it onto the history).</summary>
    public static IEditCommand CreateAddTextureCommand(BankSection section, EditableTexture texture)
    {
        int insertAt = section.Bank.Count;
        return new DelegateCommand("Add texture",
            () => section.Bank.Add(texture),
            () =>
            {
                section.Bank.Remove(texture);
                section.Bank.Reindex();
            });
    }

    public static IEditCommand CreateDeleteTextureCommand(BankSection section, EditableTexture texture)
    {
        int index = section.Bank.Textures.ToList().IndexOf(texture);
        return new DelegateCommand("Delete texture",
            () =>
            {
                section.Bank.Remove(texture);
                section.Bank.Reindex();
            },
            () =>
            {
                section.Bank.Insert(index, texture);
                section.Bank.Reindex();
            });
    }

    /// <summary>
    /// Bring the stored table2 sRGB and cubemap bits in line with the texture's format and type.
    /// Used after an add/replace so the record's flags describe the payload it actually carries.
    /// </summary>
    public static void SyncFlagsFromFormat(EditableTexture texture)
    {
        var info = texture.FormatInfo;
        if (info is null) return;
        texture.SetSrgbFlag(info.IsSrgb);
        texture.SetCubeUsageFlag(texture.Header.Type.IsCube());
    }

    /// <summary>Recompute <c>imageSize</c> from the current header fields (advisory helper).</summary>
    public static ulong RecomputeImageSize(EditableTexture texture)
    {
        var layout = texture.Layout;
        if (!layout.Applicable) return texture.Header.ImageSize;
        texture.Header.ImageSize = (ulong)layout.ComputedImageSize;
        texture.PayloadSize = (uint)Math.Min(layout.ComputedImageSize, uint.MaxValue);
        return texture.Header.ImageSize;
    }

    /// <summary>Set a header field and wrap the change in an undoable command.</summary>
    public static IEditCommand CreateHeaderWordCommand(EditableTexture texture, int offset, uint value, string name)
    {
        uint before = Mem.ReadU32LE(texture.Header.Raw, offset);
        return new DelegateCommand(name,
            () => Mem.WriteU32LE(texture.Header.Raw, offset, value),
            () => Mem.WriteU32LE(texture.Header.Raw, offset, before));
    }

    /// <summary>Batch force of a table2 byte across every texture in a bank.</summary>
    public static IEditCommand CreateBatchTable2Command(BankSection section, int table2Index, byte value)
    {
        var before = section.Bank.Textures.Select(t => t.Table2Bytes[table2Index]).ToArray();
        return new DelegateCommand($"Force table2[b{table2Index}] = 0x{value:X2}",
            () =>
            {
                foreach (var tex in section.Bank.Textures) tex.Table2Bytes[table2Index] = value;
            },
            () =>
            {
                for (int i = 0; i < section.Bank.Textures.Count; i++)
                    section.Bank.Textures[i].Table2Bytes[table2Index] = before[i];
            });
    }

    /// <summary>Batch force of the XT1 0x2C textureFlag across every texture in a bank.</summary>
    public static IEditCommand CreateBatchTextureFlagCommand(BankSection section, uint value)
    {
        var before = section.Bank.Textures.Select(t => t.Header.TextureFlag).ToArray();
        return new DelegateCommand($"Force textureFlag = {Mem.Hex(value)}",
            () =>
            {
                foreach (var tex in section.Bank.Textures) tex.Header.TextureFlag = value;
            },
            () =>
            {
                for (int i = 0; i < section.Bank.Textures.Count; i++)
                    section.Bank.Textures[i].Header.TextureFlag = before[i];
            });
    }

    /// <summary>
    /// Replace a texture from a raw `.xt1` blob (header + payload): both are copied verbatim, so a
    /// texture exported with "Export .xt1..." can be pasted back into any slot. Falls back to a
    /// payload-only replace when the bytes are not an XT1 record.
    /// </summary>
    public static OperationResult ReplaceWithXt1(EditableTexture texture, byte[] bytes)
    {
        if (!RawXt1.HasMagic(bytes))
            return ReplaceRawPayload(texture, bytes, requireExactSize: false);

        var raw = RawXt1.Parse(bytes);
        var beforeHeader = texture.Header.Raw.ToArray();
        var beforePayload = texture.Payload;
        var beforeSize = texture.PayloadSize;
        var afterHeader = raw.Header.Raw.ToArray();
        var afterPayload = (byte[])raw.Payload.Clone();
        var afterSize = (uint)Math.Min(afterPayload.Length, uint.MaxValue);

        raw.Header.Raw.CopyTo(texture.Header.Raw, 0);
        texture.Payload = afterPayload;
        texture.PayloadSize = afterSize;

        _pendingCommand = new DelegateCommand("Replace from XT1",
            () =>
            {
                afterHeader.CopyTo(texture.Header.Raw, 0);
                texture.Payload = afterPayload;
                texture.PayloadSize = afterSize;
            },
            () =>
            {
                beforeHeader.CopyTo(texture.Header.Raw, 0);
                texture.Payload = beforePayload;
                texture.PayloadSize = beforeSize;
            });

        return OperationResult.Ok();
    }

    /// <summary>Replace the raw payload bytes directly (size mismatch is allowed but warned).</summary>
    public static OperationResult ReplaceRawPayload(EditableTexture texture, byte[] payload, bool requireExactSize)
    {
        if (requireExactSize && payload.Length != (int)texture.Header.ImageSize)
            return OperationResult.Fail(
                $"payload is {payload.Length} bytes but imageSize is {texture.Header.ImageSize}; " +
                "confirm the override or enable recompute");

        string? warning = null;
        if (payload.LongLength != (long)texture.Header.ImageSize)
            warning = $"payload size {payload.Length} != imageSize {texture.Header.ImageSize}";

        var before = texture.Payload;
        var beforeSize = texture.PayloadSize;
        var after = (byte[])payload.Clone();

        texture.Payload = after;
        texture.PayloadSize = (uint)Math.Min(after.Length, uint.MaxValue);

        _pendingCommand = new DelegateCommand("Replace raw payload",
            () =>
            {
                texture.Payload = after;
                texture.PayloadSize = (uint)Math.Min(after.Length, uint.MaxValue);
            },
            () =>
            {
                texture.Payload = before;
                texture.PayloadSize = beforeSize;
            });

        return OperationResult.Ok(warning);
    }

    /// <summary>Advisory validation across a whole bank.</summary>
    public static IReadOnlyList<string> ValidateBank(BankSection section)
    {
        var warnings = new List<string>();
        for (int i = 0; i < section.Bank.Textures.Count; i++)
        {
            var texture = section.Bank.Textures[i];
            foreach (var warning in texture.Validate())
                warnings.Add($"[{i}] {warning}");
        }
        if (section.Bank.IncludeTable3 && section.Bank.Table3Offset == 0)
            warnings.Add("table3 is marked present but its table offset is 0");
        return warnings;
    }
}
