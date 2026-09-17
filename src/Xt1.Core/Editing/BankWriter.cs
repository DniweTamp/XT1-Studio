using Xt1.Core.Containers;
using Xt1.Core.Interop;

namespace Xt1.Core.Editing;

/// <summary>
/// Serializes a <see cref="BankDocument"/> back to loose files. Split banks write the WTB and the
/// payload stream separately; model banks rewrite the `DAT` containers (which keeps the payload
/// base at 0x1000); standalone `.wtb` and raw `.xt1` are written whole.
/// </summary>
public static class BankWriter
{
    /// <summary>Build the payload stream by placing each texture's bytes at its table0 offset.</summary>
    public static byte[] BuildPayloadStream(BankSection section, bool recomputeOffsets)
    {
        var bank = section.Bank;

        // An untouched bank keeps its exact stored stream. Trailing padding is not uniform:
        // core.dtt's "wtp" ends precisely at the last payload (34528256, not 4096-aligned) while
        // other containers pad, so repacking a clean stream would change bytes for no reason.
        if (!bank.IsDirty && !bank.StructureDirty && section.PayloadStream.Length > 0)
            return section.PayloadStream;

        if (recomputeOffsets)
        {
            // Honour the per-bank sync preference; RecalculatePayloadOffsets reads it. This runs
            // before the WTB is serialized (see the callers) so table0/table1 and the payload
            // stream always describe the same layout.
            bank.RecalculatePayloadOffsets(0);
        }

        // Emit exactly to the end of the last payload; do not invent trailing padding.
        long end = 0;
        foreach (var tex in bank.Textures)
            end = Math.Max(end, (long)tex.PayloadOffset + tex.Payload.Length);
        if (end > int.MaxValue) throw new InvalidOperationException("payload stream is too large");

        var stream = new byte[end];
        foreach (var tex in bank.Textures)
        {
            int at = (int)tex.PayloadOffset;
            if (at < 0 || at + tex.Payload.Length > stream.Length) continue;
            tex.Payload.CopyTo(stream, at);
        }
        return stream;
    }

    public static WriteResult Write(BankDocument document, WriteOptions options)
    {
        var files = new List<OutputFile>();
        var warnings = new List<string>(document.Warnings);

        switch (document.Kind)
        {
            case BankKind.EffectWta:
                WriteSplitEffect(document, options, files, warnings);
                break;

            case BankKind.ModelDat:
                WriteModelDat(document, options, files, warnings);
                break;

            case BankKind.StandaloneWtb:
                files.Add(new OutputFile(ResolveHeaderPath(document, options),
                    StandaloneWtbWriter.Build(document.Sections[0].Bank, options)));
                break;

            case BankKind.RawXt1:
                if (document.RawXt1 is null) throw new InvalidOperationException("raw XT1 document has no texture");
                files.Add(new OutputFile(ResolveHeaderPath(document, options), document.RawXt1.ToBytes()));
                break;

            default:
                throw new NotSupportedException($"unsupported bank kind {document.Kind}");
        }

        return new WriteResult(files, warnings);
    }

    private static void WriteSplitEffect(BankDocument document, WriteOptions options,
        List<OutputFile> files, List<string> warnings)
    {
        var section = document.Sections[0];
        // Recompute payload offsets FIRST: it mutates table0/table1, and the WTB must be
        // serialized from the same state the payload stream is laid out with.
        var payload = BuildPayloadStream(section, options.RecomputePayloadOffsets);
        var bankBytes = section.Bank.ToBytes();

        var headerPath = ResolveHeaderPath(document, options);
        var payloadPath = Path.ChangeExtension(headerPath, ".wtp");

        files.Add(new OutputFile(headerPath, bankBytes));

        if (options.RecompressOodle)
        {
            var compressed = TryCompress(payload, options, warnings);
            if (compressed is not null)
                files.Add(new OutputFile(payloadPath, compressed));
            else
                files.Add(new OutputFile(payloadPath, payload));
        }
        else if (options.WritePayloadSide)
        {
            files.Add(new OutputFile(payloadPath, payload));
        }
    }

    private static void WriteModelDat(BankDocument document, WriteOptions options,
        List<OutputFile> files, List<string> warnings)
    {
        foreach (var section in document.Sections)
        {
            // A "wtb" sub-file is a standalone bank with its payload inline: rebuild it in place,
            // and only when it actually changed so an untouched container keeps its exact bytes.
            if (section.WtbSubFile is not null)
            {
                if (section.Bank.IsDirty)
                    section.WtbSubFile.Bytes = StandaloneWtbWriter.Build(section.Bank, options);
                continue;
            }

            // Build the payload stream (which recomputes table0/table1) before serializing the
            // WTB, otherwise the "wta" sub-file would carry stale offsets.
            if (section.WtpSubFile is not null)
            {
                var payload = BuildPayloadStream(section, options.RecomputePayloadOffsets);
                section.WtpSubFile.Bytes = payload;
            }

            if (section.WtaSubFile is not null)
                section.WtaSubFile.Bytes = section.Bank.ToBytes();
        }

        if (document.HeaderDat is not null)
        {
            var headerPath = ResolveHeaderPath(document, options);
            files.Add(new OutputFile(headerPath, document.HeaderDat.ToBytes()));
        }

        if (document.PayloadDat is not null && options.WritePayloadSide)
        {
            var payloadPath = ResolvePayloadPath(document, options);
            var bytes = document.PayloadDat.ToBytes();
            if (options.RecompressOodle)
            {
                var compressed = TryCompress(bytes, options, warnings);
                files.Add(new OutputFile(payloadPath, compressed ?? bytes));
            }
            else
            {
                files.Add(new OutputFile(payloadPath, bytes));
            }
        }
    }

    private static byte[]? TryCompress(byte[] data, WriteOptions options, List<string> warnings)
    {
        using var oodle = OodleCodec.TryCreate(out var error, options.OodlePath);
        if (oodle is null)
        {
            warnings.Add($"Oodle unavailable ({error}); payload written uncompressed");
            return null;
        }
        if (!oodle.SupportsCompression)
        {
            warnings.Add("this oo2core build is decompress-only; payload written uncompressed");
            return null;
        }
        return oodle.Compress(data);
    }

    private static string ResolveHeaderPath(BankDocument document, WriteOptions options)
    {
        var original = document.HeaderSidePath ?? document.FilePath ?? "output.wtb";
        return Rebase(original, options);
    }

    private static string ResolvePayloadPath(BankDocument document, WriteOptions options)
    {
        var original = document.PayloadSidePath ?? Path.ChangeExtension(document.FilePath ?? "output.dat", ".dtt");
        return Rebase(original, options);
    }

    private static string Rebase(string original, WriteOptions options)
    {
        var directory = options.OutputDirectory ?? Path.GetDirectoryName(original) ?? ".";
        var baseName = options.OutputBaseName ?? Path.GetFileNameWithoutExtension(original);
        var extension = Path.GetExtension(original);
        return Path.Combine(directory, baseName + extension);
    }
}
