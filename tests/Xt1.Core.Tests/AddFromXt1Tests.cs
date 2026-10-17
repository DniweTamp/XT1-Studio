using Xt1.Core.Containers;
using Xt1.Core.Editing;
using Xt1.Core.Format;
using Xt1.Core.Xt1;
using Xunit;

namespace Xt1.Core.Tests;

/// <summary>
/// Adding a texture to a bank from a loose `.xt1` (<see cref="TextureOperations.BuildFromXt1"/>):
/// the blob is header + payload and must land in the bank verbatim — no decode, no re-encode, no
/// layout math — with only the add-time table2/hash conventions applied on top.
/// </summary>
public sealed class AddFromXt1Tests
{
    /// <summary>Serialize a texture the way "Export .xt1..." does (TextureExport.Write, Xt1 case).</summary>
    private static byte[] ExportAsXt1(EditableTexture texture) =>
        new RawXt1(texture.Header.Clone(), texture.Payload, trackOriginal: false).ToBytes();

    /// <summary>A source texture as an add produces it: canonical header, payload assigned to match.</summary>
    private static EditableTexture SourceTexture(uint width, uint height, Xt1Format format, TextureType type)
    {
        var texture = EditableTexture.CreateNew(format, width, height);
        texture.Header.Type = type;
        var payload = new byte[texture.Layout.ComputedImageSize];
        new Random(1234).NextBytes(payload);
        texture.Payload = payload;
        texture.PayloadSize = (uint)payload.Length;
        texture.Header.ImageSize = (ulong)payload.Length;
        return texture;
    }

    [Fact]
    public void BuildFromXt1_CopiesHeaderAndPayloadVerbatim()
    {
        var source = SourceTexture(4, 4, Xt1Format.Rgba8Unorm, TextureType.T2D);
        var bytes = ExportAsXt1(source);

        var built = TextureOperations.BuildFromXt1(bytes, "imported", out var error);

        Assert.Null(error);
        Assert.NotNull(built);
        Assert.Equal(source.Header.Format, built!.Header.Format);
        Assert.Equal(source.Header.Width, built.Header.Width);
        Assert.Equal(source.Header.Height, built.Header.Height);
        Assert.Equal(source.Header.MipCount, built.Header.MipCount);
        Assert.Equal(source.Header.Type, built.Header.Type);
        Assert.Equal(source.Header.ImageSize, built.Header.ImageSize);
        Assert.Equal(source.Payload, built.Payload);
        Assert.Equal("imported", built.Name);
    }

    [Fact]
    public void BuildFromXt1_PreservesCubeType_AndFlagSyncSetsTheCubeBit()
    {
        var source = SourceTexture(4, 4, Xt1Format.Rgba8Unorm, TextureType.TCube);
        var bytes = ExportAsXt1(source);

        var built = TextureOperations.BuildFromXt1(bytes, null, out _);

        Assert.NotNull(built);
        Assert.Equal(TextureType.TCube, built!.Header.Type);
        // A loose .xt1 carries no table2: the cube bit starts unset and flag sync derives it.
        Assert.False(built.IsCubeUsageFlag);
        TextureOperations.SyncFlagsFromFormat(built);
        Assert.True(built.IsCubeUsageFlag);
        Assert.True(built.IsSrgbFlag == (FormatTable.Get(Xt1Format.Rgba8Unorm)!.IsSrgb));
    }

    [Fact]
    public void BuildFromXt1_RejectsBytesWithoutMagic()
    {
        var built = TextureOperations.BuildFromXt1(new byte[] { 1, 2, 3, 4 }, null, out var error);

        Assert.Null(built);
        Assert.Contains("magic", error);
    }

    [Fact]
    public void BuildFromXt1_RejectsHeaderOnlyBlob()
    {
        var source = SourceTexture(4, 4, Xt1Format.Rgba8Unorm, TextureType.T2D);
        var headerOnly = new byte[Xt1Header.Size];
        source.Header.Raw.CopyTo(headerOnly, 0);

        var built = TextureOperations.BuildFromXt1(headerOnly, null, out var error);

        Assert.Null(built);
        Assert.Contains("no payload", error);
    }

    [Fact]
    public void BuildFromXt1_RejectsTruncatedPayload()
    {
        var source = SourceTexture(4, 4, Xt1Format.Rgba8Unorm, TextureType.T2D);
        var bytes = ExportAsXt1(source);
        var truncated = bytes.AsSpan(0, Xt1Header.Size + 8).ToArray();

        var built = TextureOperations.BuildFromXt1(truncated, null, out var error);

        Assert.Null(built);
        Assert.Contains("truncated", error);
    }

    [Fact]
    public void AddCommand_FromXt1Texture_InsertsAndUndoRemoves()
    {
        var document = BankDocument.CreateEmpty(BankKind.EffectWta, "virtual.wta");
        var section = document.Sections[0];
        var source = SourceTexture(4, 4, Xt1Format.Rgba8Unorm, TextureType.T2D);
        var built = TextureOperations.BuildFromXt1(ExportAsXt1(source), "imported", out _);
        Assert.NotNull(built);

        var command = TextureOperations.CreateAddTextureCommand(section, built!);
        command.Do();
        Assert.Equal(1, section.Bank.Count);
        Assert.Same(built, section.Bank.Textures[0]);

        command.Undo();
        Assert.Equal(0, section.Bank.Count);
    }

    /// <summary>
    /// The whole add path on a freshly authored split bank: with the shell's add-time conventions
    /// (name, CRC32 hash, model-bank class, flag sync) the texture must survive save/reload with
    /// its verbatim payload, format and hash intact.
    /// </summary>
    [Fact]
    public void AddedXt1Texture_SurvivesSaveReload()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Xt1Studio", "tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "new.wta");
            var document = BankDocument.CreateEmpty(BankKind.EffectWta, path);
            var source = SourceTexture(4, 4, Xt1Format.Rgba8Unorm, TextureType.T2D);
            var built = TextureOperations.BuildFromXt1(ExportAsXt1(source), "imported", out _);
            Assert.NotNull(built);

            // The add-time conventions shared by the shell (OnTreeAddTexture) and the CLI (FileCli.Add).
            built!.HashMode = HashMode.AutoFromName;
            built.Hash = Crc32NameHash.ComputeMasked("imported");
            built.SetModelBankClass(true);
            TextureOperations.SyncFlagsFromFormat(built);
            document.Sections[0].Bank.Add(built);

            BankWriter.Write(document, new WriteOptions()).SaveAll();

            var reloaded = BankDocument.Load(path);
            var bank = reloaded.Sections[0].Bank;
            Assert.Single(bank.Textures);
            var saved = bank.Textures[0];
            // WTB stores no names; the CRC32 hash is the persisted identity of the added texture.
            Assert.Equal(Crc32NameHash.ComputeMasked("imported"), saved.Hash);
            Assert.Equal(source.Header.Format, saved.Header.Format);
            Assert.Equal(source.Header.ImageSize, saved.Header.ImageSize);
            Assert.Equal(source.Payload, saved.Payload);
            Assert.True(saved.IsModelBankClass); // the synthesized table2 class bit survived the round-trip
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }
}
