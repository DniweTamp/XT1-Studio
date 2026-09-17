using Xt1.Core.Interop;
using Xt1.Core.Util;

namespace Xt1.Core.Containers;

public enum BankKind
{
    /// <summary>`&lt;name&gt;.wta` (raw WTB) + `&lt;name&gt;.wtp` payload stream.</summary>
    EffectWta,

    /// <summary>`&lt;name&gt;.dat` whose `"wta"` sub-file is the WTB, plus a `.dtt` payload container.</summary>
    ModelDat,

    /// <summary>Standalone `.wtb` with the payload appended inline (table0 rebased absolute).</summary>
    StandaloneWtb,

    /// <summary>Raw `.xt1` (header + payload).</summary>
    RawXt1,
}

/// <summary>
/// One WTB bank inside a document. A `.dat` may contain several `"wta"` sub-files (e.g.
/// `ui_core.dat` has two), each paired with the matching `"wtp"` payload sub-file.
/// </summary>
public sealed class BankSection
{
    public required string Label { get; init; }
    public required WtbBank Bank { get; init; }

    public DatSubFile? WtaSubFile { get; init; }
    public DatSubFile? WtpSubFile { get; init; }

    /// <summary>The `"wtb"` sub-file this bank came from (a standalone WTB with inline payload).</summary>
    public DatSubFile? WtbSubFile { get; init; }

    public int WtaIndex { get; init; } = -1;
    public int WtpIndex { get; init; } = -1;

    /// <summary>Payload stream (table0 is relative to this for split banks).</summary>
    public byte[] PayloadStream { get; set; } = Array.Empty<byte>();

    public override string ToString() => $"{Label} ({Bank.Count} textures)";
}

/// <summary>
/// A loaded texture bank document: `.wta`+`.wtp`, `.dat`+`.dtt`, standalone `.wtb`, or raw `.xt1`.
/// </summary>
public sealed class BankDocument
{
    private readonly List<string> _warnings = new();

    public BankKind Kind { get; private set; }
    public string? FilePath { get; private set; }

    /// <summary>Path of the file that holds the WTB bank (`.wta`, `.dat`, `.wtb`, `.xt1`).</summary>
    public string? HeaderSidePath { get; private set; }

    /// <summary>Path of the payload file (`.wtp`, `.dtt`), or null for standalone/raw.</summary>
    public string? PayloadSidePath { get; private set; }

    public List<BankSection> Sections { get; } = new();

    /// <summary>The `.dat` that holds the `"wta"` WTB sub-file(s), for model banks.</summary>
    public DatFile? HeaderDat { get; private set; }

    /// <summary>The `.dtt` payload container, for model banks.</summary>
    public DatFile? PayloadDat { get; private set; }

    public RawXt1? RawXt1 { get; private set; }

    public IReadOnlyList<string> Warnings => _warnings;

    public string DisplayName =>
        FilePath is null ? "(untitled)" : Path.GetFileName(FilePath);

    public bool IsDirty =>
        Sections.Any(s => s.Bank.IsDirty) ||
        (HeaderDat?.IsDirty ?? false) ||
        (PayloadDat?.IsDirty ?? false) ||
        (RawXt1?.IsDirty ?? false);

    public IEnumerable<EditableTexture> AllTextures => Sections.SelectMany(s => s.Bank.Textures);

    public static BankDocument Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("bank file not found", path);

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex)
        {
            throw new IOException($"failed to read {path}: {ex.Message}", ex);
        }

        var document = new BankDocument { FilePath = path };

        if (DatFile.HasMagic(bytes))
        {
            document.LoadDat(path, bytes);
            return document;
        }

        if (WtbBank.HasMagic(bytes))
        {
            document.HeaderSidePath = path;

            // A raw WTB file is either a standalone `.wtb` (payload inline) or the `.wta` half of
            // an effect bank whose payload lives in the sibling `.wtp`. The extension decides which:
            // an empty standalone bank has no table0 offset to infer it from.
            bool standaloneByExtension = path.EndsWith(".wtb", StringComparison.OrdinalIgnoreCase);
            var wtpPath = Path.ChangeExtension(path, ".wtp");
            byte[]? payloadStream = null;
            if (!standaloneByExtension && File.Exists(wtpPath))
            {
                try
                {
                    payloadStream = File.ReadAllBytes(wtpPath);
                    document.PayloadSidePath = wtpPath;
                }
                catch (Exception ex)
                {
                    document._warnings.Add($"failed to read {Path.GetFileName(wtpPath)}: {ex.Message}");
                }
            }

            var bank = WtbBank.Parse(bytes, payloadStream);
            if (standaloneByExtension) bank.IsStandalone = true;
            document.Kind = bank.IsStandalone ? BankKind.StandaloneWtb : BankKind.EffectWta;

            var section = new BankSection
            {
                Label = bank.IsStandalone ? "wtb" : "wta",
                Bank = bank,
                PayloadStream = bank.InlinePayload ?? payloadStream ?? Array.Empty<byte>(),
            };
            document.Sections.Add(section);

            if (bank.Count == 0)
                document._warnings.Add("bank contains no textures");

            if (!bank.IsStandalone && payloadStream is null)
                document._warnings.Add($"{Path.GetFileName(wtpPath)} not found; payloads will be unavailable");

            return document;
        }

        if (RawXt1.HasMagic(bytes))
        {
            document.Kind = BankKind.RawXt1;
            document.HeaderSidePath = path;
            document.RawXt1 = RawXt1.Parse(bytes);
            return document;
        }

        throw new InvalidDataException("unrecognized file: not DAT / WTB / XT1");
    }

    /// <summary>
    /// Author a brand-new empty bank. Effect banks are a `.wta` header with a matching `.wtp`
    /// payload stream; a standalone is a single `.wtb` with its payload inline. DAT containers are
    /// not authored here (the user can still open and edit existing ones).
    /// </summary>
    public static BankDocument CreateEmpty(BankKind kind, string headerPath)
    {
        var document = new BankDocument { Kind = kind, FilePath = headerPath, HeaderSidePath = headerPath };

        WtbBank bank;
        string label;
        switch (kind)
        {
            case BankKind.EffectWta:
                bank = WtbBank.CreateEmpty(includeTable3: false, standalone: false, payloadBase: 0);
                label = "wta";
                document.PayloadSidePath = Path.ChangeExtension(headerPath, ".wtp");
                break;
            case BankKind.StandaloneWtb:
                bank = WtbBank.CreateEmpty(includeTable3: true, standalone: true,
                    payloadBase: StandaloneWtbWriter.PayloadBase);
                label = "wtb";
                break;
            default:
                throw new NotSupportedException($"cannot author a new {kind} document");
        }

        document.Sections.Add(new BankSection
        {
            Label = label,
            Bank = bank,
            PayloadStream = Array.Empty<byte>(),
        });
        return document;
    }

    private void LoadDat(string path, byte[] bytes)
    {
        HeaderDat = DatFile.Parse(bytes);
        Kind = BankKind.ModelDat;
        HeaderSidePath = path;
        PayloadSidePath = Path.ChangeExtension(path, ".dtt");

        var wtaIndices = new List<int>();
        var wtpIndices = new List<int>();
        for (int i = 0; i < HeaderDat.SubFiles.Count; i++)
        {
            var tag = HeaderDat.SubFiles[i].Tag;
            if (tag == "wta") wtaIndices.Add(i);
            else if (tag == "wtp") wtpIndices.Add(i);
        }

        // Case A: this file is the payload container (only "wtp"), find the .dat sibling.
        if (wtaIndices.Count == 0 && wtpIndices.Count > 0)
        {
            PayloadDat = HeaderDat;
            HeaderDat = null;
            HeaderSidePath = Path.ChangeExtension(path, ".dat");
            PayloadSidePath = path;

            var datPath = HeaderSidePath!;
            if (File.Exists(datPath))
            {
                HeaderDat = DatFile.Parse(File.ReadAllBytes(datPath));
                wtaIndices.Clear();
                for (int i = 0; i < HeaderDat.SubFiles.Count; i++)
                    if (HeaderDat.SubFiles[i].Tag == "wta") wtaIndices.Add(i);
            }
            else
            {
                _warnings.Add($"payload container loaded without a matching .dat ({Path.GetFileName(datPath)} missing)");
            }
        }
        // Case B: this file is the header container (has "wta"), find the .dtt sibling.
        else if (wtaIndices.Count > 0 && PayloadDat is null)
        {
            var dttPath = Path.ChangeExtension(path, ".dtt");
            if (File.Exists(dttPath))
            {
                try
                {
                    PayloadDat = DatFile.Parse(File.ReadAllBytes(dttPath));
                    for (int i = 0; i < PayloadDat.SubFiles.Count; i++)
                        if (PayloadDat.SubFiles[i].Tag == "wtp") wtpIndices.Add(i);
                }
                catch (Exception ex)
                {
                    _warnings.Add($"failed to read payload container {Path.GetFileName(dttPath)}: {ex.Message}");
                }
            }
            else
            {
                _warnings.Add($"{Path.GetFileName(dttPath)} not found; payloads will be unavailable");
            }
        }

        if (wtaIndices.Count == 0 && !HasWtbBanks())
        {
            var source = HeaderDat ?? PayloadDat;
            var tags = source is null
                ? "none"
                : string.Join(", ", source.SubFiles.Select(s => s.Tag).Distinct());
            throw new InvalidDataException(
                $"DAT contains no texture bank (no \"wta\" or \"wtb\" sub-file; tags: {tags})");
        }

        // Banks come from two places: "wta" sub-files (payload paired from the "wtp" sub-files, in
        // order) and "wtb" sub-files, which are standalone banks carrying their payload inline.
        // core.dtt is the interesting case: it holds "wtb" banks (CubeMap, Grading) alongside the
        // "wtp" payload for the "wta" bank that lives in core.dat.
        var banks = new List<(DatSubFile Sub, DatFile Container, DatSubFile? Payload, int WtaIndex, int WtpIndex)>();

        for (int n = 0; n < wtaIndices.Count; n++)
        {
            int wtaIndex = wtaIndices[n];
            int wtpIndex = n < wtpIndices.Count ? wtpIndices[n] : -1;
            banks.Add((
                HeaderDat!.SubFiles[wtaIndex],
                HeaderDat,
                wtpIndex >= 0 ? PayloadDat!.SubFiles[wtpIndex] : null,
                wtaIndex,
                wtpIndex));
        }

        foreach (var container in new[] { PayloadDat, HeaderDat })
        {
            if (container is null) continue;
            for (int i = 0; i < container.SubFiles.Count; i++)
            {
                var sub = container.SubFiles[i];
                if (sub.Tag == "wtb") banks.Add((sub, container, null, -1, -1));
            }
        }

        bool manyBanks = banks.Count > 1;

        foreach (var (sub, container, payload, wtaIndex, wtpIndex) in banks)
        {
            bool standalone = ReferenceEquals(sub, payload) || sub.Tag == "wtb";

            if (!WtbBank.HasMagic(sub.Bytes))
            {
                _warnings.Add($"sub-file \"{sub.Tag}\"[{container.SubFiles.ToList().IndexOf(sub)}] " +
                              $"({sub.Name}) is not a WTB bank; skipped");
                continue;
            }

            byte[] stream = payload?.Bytes ?? Array.Empty<byte>();
            var bank = WtbBank.Parse(sub.Bytes, stream.Length > 0 ? stream : null);

            // DATs name their sub-files (e.g. ui_core.dat holds "core.wta" and "messcore.wta"), so
            // a container with several banks is labeled by name; a single-bank container keeps a
            // short label since the document name already identifies it.
            string label;
            if (manyBanks && !string.IsNullOrWhiteSpace(sub.Name))
                label = sub.Name;
            else if (sub.Tag == "wtb")
                label = "wtb";
            else if (!manyBanks)
                label = "wta";
            else
                label = $"wta[{wtaIndex}]";

            Sections.Add(new BankSection
            {
                Label = label,
                Bank = bank,
                WtaSubFile = sub.Tag == "wta" ? sub : null,
                WtpSubFile = payload,
                WtbSubFile = sub.Tag == "wtb" ? sub : null,
                WtaIndex = wtaIndex,
                WtpIndex = wtpIndex,
                // A standalone "wtb" bank carries its payload inside the sub-file itself.
                PayloadStream = bank.InlinePayload ?? stream,
            });
        }

        if (wtaIndices.Count > wtpIndices.Count)
            _warnings.Add($"{wtaIndices.Count} wta sub-file(s) but only {wtpIndices.Count} wtp payload(s)");

        bool HasWtbBanks()
        {
            foreach (var container in new[] { PayloadDat, HeaderDat })
            {
                if (container is null) continue;
                if (container.SubFiles.Any(s => s.Tag == "wtb")) return true;
            }
            return false;
        }
    }
}
