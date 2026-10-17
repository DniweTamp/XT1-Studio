using Xt1.Core.Codecs;
using Xt1.Core.Containers;
using Xt1.Core.Editing;
using Xt1.Core.Encoding;
using Xt1.Core.Format;
using Xt1.Core.Imaging;
using Xt1.Core.Nvtt;
using Xt1.Core.Settings;
using Xt1.Core.Xt1;

namespace Xt1.App.Wpf.Cli;

/// <summary>
/// <c>XT1 Studio.exe --file &lt;op&gt; ...</c>: the headless form of every file operation the shell
/// offers - new/open/save, export PNG/DDS/XT1, import/replace, add, delete, raw-payload replace and
/// batch recompress.
/// <para>
/// One command keeps the shared parts (loading a document, selecting a texture with
/// <c>--index</c>/<c>--hash</c>/<c>--bank</c>, and writing with <c>--out</c>/<c>--base</c>) in one
/// place, so the CLI drives the same <see cref="BankDocument"/>/<see cref="TextureOperations"/>
/// services the UI uses instead of a parallel implementation. Mutating ops write by default; pass
/// <c>--dry-run</c> to see what would be written.
/// </para>
/// </summary>
internal static class FileCli
{
    internal static int Run(string[] args, Action<string> log)
    {
        int at = Array.FindIndex(args, a => string.Equals(a, "--file", StringComparison.OrdinalIgnoreCase));
        if (at < 0)
        {
            Usage(log);
            return 2;
        }

        var rest = args[(at + 1)..];
        if (rest.Length == 0 || rest[0].StartsWith('-'))
        {
            Usage(log);
            return 2;
        }

        string op = rest[0].ToLowerInvariant();
        var (positional, options) = Split(rest[1..]);
        try
        {
            return op switch
            {
                "new" => New(positional, options, log),
                "save" => Save(positional, options, log),
                "export" => Export(positional, options, log),
                "import" => Import(positional, options, log),
                "add" => Add(positional, options, log),
                "delete" => Delete(positional, options, log),
                "replace-raw" or "replace-xt1" => ReplaceRaw(positional, options, log),
                "dummy" or "dummy-out" => Dummy(positional, options, log),
                "recompress" => Recompress(positional, options, log),
                _ => Unknown(op, log),
            };
        }
        catch (Exception ex)
        {
            // A corrupt image/DDS, an unexpected write failure or a malformed bank must fail the
            // command, not escape as an unhandled exception out of App.OnStartup.
            log($"[error] {ex.Message}");
            return 1;
        }
    }

    // --- commands -------------------------------------------------------------------------

    private static int New(List<string> positional, string[] options, Action<string> log)
    {
        if (positional.Count < 1) { Usage(log); return 2; }

        string kindText = (CliOptions.Get(options, "--kind") ?? "wta").ToLowerInvariant();
        BankKind? kind = kindText switch
        {
            "wta" or "split" or "effect" => BankKind.EffectWta,
            "wtb" or "standalone" => BankKind.StandaloneWtb,
            _ => null,
        };
        if (kind is null)
        {
            log($"[error] unknown --kind '{kindText}' (use wta or wtb)");
            return 2;
        }

        try
        {
            var path = Path.GetFullPath(positional[0]);

            // A new bank is a create, so an existing header or payload there is almost certainly a
            // mistake; refuse it rather than replace the file irrecoverably (the shell confirms too).
            var targets = kind == BankKind.EffectWta
                ? new[] { path, Path.ChangeExtension(path, ".wtp") }
                : new[] { path };
            if (!Has(options, "--force") && !Has(options, "--overwrite") &&
                Array.Find(targets, File.Exists) is { } existing)
            {
                log($"[error] {existing} already exists; pass --force to overwrite");
                return 2;
            }

            var document = BankDocument.CreateEmpty(kind.Value, path);
            log($"new {kindText} bank {path}");
            return Write(document, options, log);
        }
        catch (Exception ex)
        {
            log($"[error] {ex.Message}");
            return 1;
        }
    }

    private static int Save(List<string> positional, string[] options, Action<string> log)
    {
        if (positional.Count < 1) { Usage(log); return 2; }
        var document = Load(positional[0], log);
        return document is null ? 2 : Write(document, options, log);
    }

    private static int Export(List<string> positional, string[] options, Action<string> log)
    {
        if (positional.Count < 2) { Usage(log); return 2; }

        string asKind = (CliOptions.Get(options, "--as") ?? "png").ToLowerInvariant();
        if (!TextureExport.TryParse(asKind, out var format))
        {
            log($"[error] unknown --as '{asKind}' (use png, dds or xt1)");
            return 2;
        }

        var document = Load(positional[0], log);
        if (document is null) return 2;

        string output = positional[1];
        var targets = Select(document, options);
        if (targets.Count == 0)
        {
            log("[error] no texture matched");
            return 2;
        }

        int mip = CliOptions.GetInt(options, "--mip") ?? 0;
        int face = CliOptions.GetInt(options, "--face") ?? 0;
        int slice = CliOptions.GetInt(options, "--slice") ?? 0;

        // A single target with a file extension is a file path; anything else is a directory.
        bool toDirectory = targets.Count > 1 || Directory.Exists(output) || !Path.HasExtension(output);
        bool dryRun = Has(options, "--dry-run");
        if (toDirectory && !dryRun) Directory.CreateDirectory(output);

        string stem = Path.GetFileNameWithoutExtension(document.FilePath ?? "texture");
        string extension = TextureExport.Extension(format);
        int written = 0, skipped = 0;
        foreach (var target in targets)
        {
            var texture = target.Texture;
            string defaultName = $"{stem}_tex{texture.Index}_0x{texture.EffectiveHash():X8}";
            string file = toDirectory ? Path.Combine(output, defaultName + extension) : output;

            if (!TextureExport.CanExport(texture, format))
            {
                log($"  - #{texture.Index} {texture.FormatInfo?.Name ?? "unknown"}: cannot export as " +
                    extension.TrimStart('.'));
                skipped++;
                continue;
            }

            if (dryRun)
            {
                // Dry-run must predict the real run, so validate without writing: PNG is decoded and
                // DDS needs a usable layout, exactly the failures that would skip it for real.
                if (!TextureExport.TryValidate(texture, format, out var dryError, mip, face, slice))
                {
                    log($"  x #{texture.Index}: {dryError}");
                    skipped++;
                }
                else
                {
                    log($"  (dry run) #{texture.Index} {texture.FormatInfo?.Name} -> {file}");
                    written++;
                }
                continue;
            }

            if (TextureExport.TryWrite(texture, format, file, out var error, mip, face, slice))
            {
                log($"  + #{texture.Index} {texture.FormatInfo?.Name} -> {file}");
                written++;
            }
            else
            {
                log($"  x #{texture.Index}: {error}");
                skipped++;
            }
        }

        log((dryRun ? "would export " : "exported ") + $"{written} file(s)" +
            (skipped > 0 ? $", skipped {skipped}" : string.Empty));
        return written > 0 ? 0 : 1;
    }

    private static int Import(List<string> positional, string[] options, Action<string> log)
    {
        if (positional.Count < 2) { Usage(log); return 2; }

        var document = Load(positional[0], log);
        if (document is null) return 2;

        var one = RequireOne(document, options, log);
        if (one is null) return 2;
        var (texture, section) = one.Value;

        if (!File.Exists(positional[1]))
        {
            log($"[error] file not found: {positional[1]}");
            return 2;
        }

        if (!TryResolveFormatOption(options, log, out var requestedFormat)) return 2;

        var result = ImportInto(texture, positional[1], options, requestedFormat, BuildEncoders(), log,
            out var description);
        if (!result.Success)
        {
            log($"[error] {result.Error}");
            return 1;
        }

        SyncRawPayload(document, texture, section);
        log($"imported {description}");
        return Write(document, options, log);
    }

    private static int Add(List<string> positional, string[] options, Action<string> log)
    {
        if (positional.Count < 2) { Usage(log); return 2; }

        var document = Load(positional[0], log);
        if (document is null) return 2;
        if (document.RawXt1 is not null)
        {
            log("[error] a loose .xt1 is a single texture; use --file replace-raw to replace it");
            return 2;
        }

        int? bank = CliOptions.GetInt(options, "--bank");
        BankSection? section = bank is { } b
            ? b >= 0 && b < document.Sections.Count ? document.Sections[b] : null
            : document.Sections.FirstOrDefault();
        if (section is null)
        {
            log("[error] no such bank (--bank)");
            return 2;
        }

        string input = positional[1];
        if (!File.Exists(input))
        {
            log($"[error] file not found: {input}");
            return 2;
        }

        if (!TryResolveFormatOption(options, log, out var requestedFormat)) return 2;

        var settings = App.Settings;
        EditableTexture? texture;
        try
        {
            if (input.EndsWith(".xt1", StringComparison.OrdinalIgnoreCase))
            {
                // A loose .xt1 is header + payload: add it verbatim, no decode or re-encode.
                texture = TextureOperations.BuildFromXt1(File.ReadAllBytes(input), null, out var xt1Error);
                if (texture is null)
                {
                    log($"[error] {xt1Error ?? "could not import the .xt1"}");
                    return 1;
                }
            }
            else if (input.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
            {
                var dds = DdsIo.Read(File.ReadAllBytes(input));
                texture = TextureOperations.BuildFromDds(dds, null, out var ddsError);
                if (texture is null)
                {
                    log($"[error] {ddsError ?? "could not import the DDS"}");
                    return 1;
                }
            }
            else
            {
                var format = requestedFormat
                             ?? FormatTable.Get(settings.DefaultImportFormat)
                             ?? FormatTable.Get(Xt1Format.Bc7Unorm)!;
                var prepared = Solidify.Process(ImageIo.Load(input), Has(options, "--solidify"), Has(options, "--opaque"));
                var encoder = BuildEncoders().Pick(ParseBackend(CliOptions.Get(options, "--backend"), settings), format,
                    warning => log("  ! " + warning));
                texture = EditableTexture.CreateNew(format.Format, (uint)prepared.Width, (uint)prepared.Height);
                var result = TextureOperations.Replace2D(texture, prepared, encoder, !Has(options, "--no-mips"),
                    ParseQuality(CliOptions.Get(options, "--quality")), null, settings.SyncFlagsFromFormat);
                if (!result.Success)
                {
                    log($"[error] {result.Error}");
                    return 1;
                }
            }
        }
        catch (Exception ex)
        {
            log($"[error] {ex.Message}");
            return 1;
        }

        // Mirror the shell: new textures carry a CRC32 hash and derive it from the name.
        string name = CliOptions.Get(options, "--name") ?? Path.GetFileNameWithoutExtension(input);
        texture.Name = name;
        texture.HashMode = HashMode.AutoFromName;
        texture.Hash = Crc32NameHash.ComputeMasked(name);
        texture.SetModelBankClass(true);
        if (settings.SyncFlagsFromFormat) TextureOperations.SyncFlagsFromFormat(texture);

        section.Bank.Add(texture);
        section.Bank.Reindex();
        log($"added \"{name}\" (hash 0x{texture.Hash:X8}) to {section.Label}");
        return Write(document, options, log);
    }

    private static int Delete(List<string> positional, string[] options, Action<string> log)
    {
        if (positional.Count < 1) { Usage(log); return 2; }

        var document = Load(positional[0], log);
        if (document is null) return 2;

        var one = RequireOne(document, options, log);
        if (one is null) return 2;
        var (texture, section) = one.Value;
        if (section is null)
        {
            log("[error] a loose .xt1 holds a single texture and has no bank to delete from");
            return 2;
        }

        section.Bank.Remove(texture);
        section.Bank.Reindex();
        log($"deleted texture #{texture.Index} from {section.Label}");
        return Write(document, options, log);
    }

    private static int ReplaceRaw(List<string> positional, string[] options, Action<string> log)
    {
        if (positional.Count < 2) { Usage(log); return 2; }

        var document = Load(positional[0], log);
        if (document is null) return 2;

        var one = RequireOne(document, options, log);
        if (one is null) return 2;
        var (texture, section) = one.Value;

        if (!File.Exists(positional[1]))
        {
            log($"[error] file not found: {positional[1]}");
            return 2;
        }

        var bytes = File.ReadAllBytes(positional[1]);
        bool isXt1 = RawXt1.HasMagic(bytes);
        bool force = Has(options, "--force");

        // An .xt1 record replaces header+payload verbatim; anything else is a bare payload, which must
        // match imageSize unless --force overrides it (the shell asks before forcing).
        var result = isXt1
            ? TextureOperations.ReplaceWithXt1(texture, bytes)
            : TextureOperations.ReplaceRawPayload(texture, bytes, requireExactSize: !force);
        if (!result.Success)
        {
            log($"[error] {result.Error}" + (!isXt1 && !force ? " (pass --force to override the size)" : string.Empty));
            return 1;
        }

        SyncRawPayload(document, texture, section);
        log(result.Warning is null ? "replaced payload" : "replaced payload: " + result.Warning);
        return Write(document, options, log);
    }

    /// <summary>
    /// Replace a 2D texture with a solid 8x8, single-level ASTC 8x8 fill. Mirrors the shell's
    /// "Dummy out" menu: <c>--fill white|black|normal</c>, sRGB/UNORM follows the source texture.
    /// </summary>
    private static int Dummy(List<string> positional, string[] options, Action<string> log)
    {
        if (positional.Count < 1) { Usage(log); return 2; }

        string fillText = CliOptions.Get(options, "--fill") ?? "white";
        if (!TextureOperations.TryParseDummyFill(fillText, out var fill))
        {
            log($"[error] unknown --fill '{fillText}' (white, black, normal)");
            return 2;
        }

        var document = Load(positional[0], log);
        if (document is null) return 2;

        var one = RequireOne(document, options, log);
        if (one is null) return 2;
        var (texture, section) = one.Value;

        var type = texture.Header.Type;
        if (type.Is3D() || type.IsCube())
        {
            log("[error] dummy out supports 2D textures only");
            return 1;
        }

        var format = TextureOperations.DummyFormat(texture);
        if (format is null)
        {
            log("[error] ASTC 8x8 is not available");
            return 1;
        }

        var encoder = BuildEncoders().Pick(EncoderBackend.Nvtt, format);
        var result = TextureOperations.DummyOut(texture, fill, encoder,
            ParseQuality(CliOptions.Get(options, "--quality")), !Has(options, "--no-sync-flags"));
        if (!result.Success)
        {
            log($"[error] {result.Error}");
            return 1;
        }

        SyncRawPayload(document, texture, section);
        log($"dummied out #{texture.Index} ({fillText}) as {format.Name} 8x8");
        return Write(document, options, log);
    }

    private static int Recompress(List<string> positional, string[] options, Action<string> log)
    {
        if (positional.Count < 1) { Usage(log); return 2; }

        var formats = ParseFormats(CliOptions.Get(options, "--rule"));
        if (formats is null)
        {
            log($"[error] unknown --rule '{CliOptions.Get(options, "--rule")}' (bc1, bc1-bc3, bc1-bc7, bc7)");
            return 2;
        }
        var (opaqueFormat, alphaFormat) = formats.Value;

        // --opaque-format / --alpha-format override either half of the rule with any encodable target.
        if (CliOptions.Get(options, "--opaque-format") is { } opaqueName)
        {
            var parsed = ParseTarget(opaqueName, "--opaque-format", log);
            if (parsed is null) return 2;
            opaqueFormat = parsed;
        }
        if (CliOptions.Get(options, "--alpha-format") is { } alphaName)
        {
            var parsed = ParseTarget(alphaName, "--alpha-format", log);
            if (parsed is null) return 2;
            alphaFormat = parsed;
        }

        // One --base for several inputs would resolve every bank to the same output pair, so all but
        // the last would be silently overwritten; refuse it rather than guess a name.
        if (positional.Count > 1 && CliOptions.Get(options, "--base") is not null)
        {
            log("[error] --base cannot be used with more than one input; their outputs would collide");
            return 2;
        }

        // With --out, inputs that share a file name also resolve to the same output pair.
        if (positional.Count > 1 && CliOptions.Get(options, "--out") is not null)
        {
            var names = positional.Select(Path.GetFileNameWithoutExtension).ToList();
            if (names.Count != names.Distinct(StringComparer.OrdinalIgnoreCase).Count())
            {
                log("[error] inputs share a file name; with --out their outputs would collide");
                return 2;
            }
        }

        var settings = App.Settings;
        var request = new RecompressOptions(
            opaqueFormat,
            alphaFormat,
            Has(options, "--downscale"),
            // The shell's batch dialog leaves "keep single-mip textures" checked, so the CLI default
            // is to hold them back too; --downscale-without-mips is the explicit opt-out.
            !Has(options, "--downscale-without-mips"),
            ParseBackend(CliOptions.Get(options, "--backend"), settings),
            ParseQuality(CliOptions.Get(options, "--quality")),
            ParseQuality(CliOptions.Get(options, "--alpha-quality") ?? CliOptions.Get(options, "--quality")),
            !Has(options, "--no-sync-flags"));

        var recompressor = new BatchRecompressor(BuildEncoders());
        var progress = new InlineProgress(log);
        int converted = 0, skipped = 0;

        foreach (var path in positional)
        {
            var document = Load(path, log);
            if (document is null) return 2;

            var stats = recompressor.RunOnDocument(document, request, progress, CancellationToken.None);
            converted += stats.Converted;
            skipped += stats.Skipped;
            if (stats.Converted == 0)
            {
                log($"  - {Path.GetFileName(path)}: nothing to convert");
                continue;
            }

            int code = Write(document, options, log);
            if (code != 0) return code;
        }

        log($"recompressed {converted} texture(s)" + (skipped > 0 ? $", {skipped} skipped" : string.Empty));
        return 0;
    }

    // --- shared helpers -------------------------------------------------------------------

    /// <summary>Leading tokens are positionals; the first option-looking token starts the option list.</summary>
    private static (List<string> Positional, string[] Options) Split(string[] args)
    {
        var positional = new List<string>();
        int i = 0;
        for (; i < args.Length && !args[i].StartsWith('-'); i++)
            positional.Add(args[i]);
        return (positional, args[i..]);
    }

    private static bool Has(string[] options, string name) =>
        Array.Exists(options, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

    private static BankDocument? Load(string path, Action<string> log)
    {
        if (!File.Exists(path))
        {
            log($"[error] file not found: {path}");
            return null;
        }

        try
        {
            return BankDocument.Load(path);
        }
        catch (Exception ex)
        {
            log($"[error] {ex.Message}");
            return null;
        }
    }

    /// <summary>Write a document with the shared <c>--out</c>/<c>--base</c>/<c>--dry-run</c> options.</summary>
    private static int Write(BankDocument document, string[] options, Action<string> log)
    {
        var write = new WriteOptions
        {
            OutputDirectory = CliOptions.Get(options, "--out"),
            OutputBaseName = CliOptions.Get(options, "--base"),
            RecomputePayloadOffsets = !Has(options, "--no-recompute"),
            TrimTrailingPadding = !Has(options, "--no-trim-padding"),
            WritePayloadSide = !Has(options, "--no-payload-side"),
        };

        var result = BankWriter.Write(document, write);
        if (Has(options, "--dry-run"))
        {
            log($"  (dry run) {result.Files.Count} file(s) would be written:");
            foreach (var file in result.Files) log($"    {file.Path} ({file.Bytes.Length} bytes)");
            return 0;
        }

        result.SaveAll();
        foreach (var file in result.Files) log($"  + {file.Path} ({file.Bytes.Length} bytes)");
        foreach (var warning in result.Warnings) log($"  ! {warning}");
        return 0;
    }

    private readonly record struct SelectedTexture(BankSection? Section, EditableTexture Texture);

    /// <summary>Textures matching <c>--bank</c>/<c>--index</c>/<c>--hash</c>, including a loose .xt1.</summary>
    private static List<SelectedTexture> Select(BankDocument document, string[] options)
    {
        int? bank = CliOptions.GetInt(options, "--bank");
        int? index = CliOptions.GetInt(options, "--index");
        uint? hash = CliOptions.GetUInt(options, "--hash");

        var matches = new List<SelectedTexture>();
        for (int s = 0; s < document.Sections.Count; s++)
        {
            if (bank is not null && s != bank) continue;
            foreach (var texture in document.Sections[s].Bank.Textures)
            {
                if (index is not null && texture.Index != index) continue;
                if (hash is not null && texture.EffectiveHash() != hash) continue;
                matches.Add(new SelectedTexture(document.Sections[s], texture));
            }
        }

        if (document.RawXt1 is { } raw && bank is null)
        {
            var wrapper = EditableTexture.FromRawReadOnly(raw);
            if ((index is null || wrapper.Index == index) && (hash is null || wrapper.EffectiveHash() == hash))
                matches.Add(new SelectedTexture(null, wrapper));
        }

        return matches;
    }

    private static (EditableTexture Texture, BankSection? Section)? RequireOne(
        BankDocument document, string[] options, Action<string> log)
    {
        var matches = Select(document, options);
        if (matches.Count == 1) return (matches[0].Texture, matches[0].Section);

        log(matches.Count == 0
            ? "[error] no texture matched; pass --index N, --hash 0xH or --bank N"
            : $"[error] {matches.Count} textures matched; narrow it with --bank/--index/--hash");
        return null;
    }

    /// <summary>
    /// Replace a texture's pixels from an image (decode + re-encode through the shared encoder) or
    /// losslessly from a DDS. Mirrors the shell's import dialog with the same defaults.
    /// </summary>
    private static OperationResult ImportInto(EditableTexture texture, string input, string[] options,
        FormatInfo? requestedFormat, EncoderPicker encoders, Action<string> log, out string description)
    {
        if (input.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
        {
            var dds = DdsIo.Read(File.ReadAllBytes(input));
            description = $"{Path.GetFileName(input)} ({dds.MipCount} mip(s), {dds.Format?.Name})";
            return TextureOperations.ReplaceWithDds(texture, dds, App.Settings.SyncFlagsFromFormat);
        }

        var settings = App.Settings;
        var format = requestedFormat
                     ?? texture.FormatInfo
                     ?? FormatTable.Get(settings.DefaultImportFormat)
                     ?? FormatTable.Get(Xt1Format.Bc7Unorm)!;
        var prepared = Solidify.Process(ImageIo.Load(input), Has(options, "--solidify"), Has(options, "--opaque"));
        var encoder = encoders.Pick(ParseBackend(CliOptions.Get(options, "--backend"), settings), format,
            warning => log("  ! " + warning));

        // Capture the header before switching the format so a failed encode restores it (see Replace2D).
        var originalHeader = texture.Header.Raw.ToArray();
        texture.Header.Format = format.Format;
        var result = TextureOperations.Replace2D(texture, prepared, encoder, !Has(options, "--no-mips"),
            ParseQuality(CliOptions.Get(options, "--quality")), originalHeader, settings.SyncFlagsFromFormat);
        if (!result.Success) originalHeader.CopyTo(texture.Header.Raw, 0);

        description = $"{Path.GetFileName(input)} -> {format.Name}";
        return result;
    }

    /// <summary>
    /// A loose .xt1 is edited through a wrapper that shares the header; a replace assigns a new payload
    /// array, so the wrapper's payload must be copied back into the container before writing.
    /// </summary>
    private static void SyncRawPayload(BankDocument document, EditableTexture texture, BankSection? section)
    {
        if (section is null && document.RawXt1 is { } raw) raw.Payload = texture.Payload;
    }

    private static EncoderPicker BuildEncoders()
    {
        var tools = App.Settings.Resolve();
        var nvtt = NvttNative.TryCreate(out _, tools.NvttDll);
        return new EncoderPicker(nvtt is null ? null : new NvttEncoder(nvtt),
            CliEncoder.TryCreate(out _, tools.NvttCli));
    }

    /// <summary>
    /// Resolve the optional <c>--format</c>. Returns true when the option is absent (leaving
    /// <paramref name="format"/> null) or resolves; false when it was given but is unknown, so the
    /// caller can fail as a usage error instead of silently encoding in a different format.
    /// </summary>
    private static bool TryResolveFormatOption(string[] options, Action<string> log, out FormatInfo? format)
    {
        format = null;
        var text = CliOptions.Get(options, "--format");
        if (text is null) return true;

        format = ResolveFormat(text, log);
        return format is not null;
    }

    private static FormatInfo? ResolveFormat(string text, Action<string> log)
    {
        if (uint.TryParse(text, out uint numeric) ||
            (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
             && uint.TryParse(text[2..], System.Globalization.NumberStyles.HexNumber, null, out numeric)))
        {
            var byValue = FormatTable.Get(numeric);
            if (byValue is not null) return byValue;
        }

        string wanted = text.Replace('-', '_').Replace(' ', '_');
        var match = FormatTable.All.FirstOrDefault(f =>
            string.Equals(f.Name, wanted, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(f.Format.ToString(), wanted, StringComparison.OrdinalIgnoreCase));
        if (match is null)
            log($"[error] unknown format '{text}'; use an id (0x4D) or a name such as BC7_UNORM");
        return match;
    }

    private static EncodeQuality ParseQuality(string? text) => (text ?? string.Empty).ToLowerInvariant() switch
    {
        "fastest" or "fast" => EncodeQuality.Fastest,
        "production" or "prod" => EncodeQuality.Production,
        "highest" or "high" => EncodeQuality.Highest,
        _ => EncodeQuality.Normal,
    };

    private static EncoderBackend ParseBackend(string? text, AppSettings settings) =>
        (text ?? string.Empty).ToLowerInvariant() switch
        {
            "managed" or "builtin" => EncoderBackend.Managed,
            "nvtt" or "nvidia" => EncoderBackend.Nvtt,
            "cli" or "external" => EncoderBackend.Cli,
            _ => settings.PreferredBackend,
        };

    /// <summary>The classic rule shorthands, as an (opaque, alpha) target pair.</summary>
    private static (RecompressTarget Opaque, RecompressTarget Alpha)? ParseFormats(string? text)
    {
        var (opaque, alpha) = (text ?? "bc1-bc3").ToLowerInvariant() switch
        {
            "bc1" or "always-bc1" => ("BC1", "BC1"),
            "bc1-bc3" or "bc1orbc3" => ("BC1", "BC3"),
            "bc1-bc7" or "bc1orbc7" => ("BC1", "BC7"),
            "bc7" or "always-bc7" => ("BC7", "BC7"),
            _ => (string.Empty, string.Empty),
        };
        return opaque.Length == 0 ? null : (Target(opaque), Target(alpha));
    }

    /// <summary>
    /// Resolve a recompress target from a picker label ("bc7", "astc 8x8") or a format family's base
    /// name/id (e.g. "BC1_UNORM" or 0x42), sharing <see cref="ResolveFormat"/>'s id and name parsing so
    /// both accept the same syntax. The sRGB/UNORM variant follows the source, so a colour-space-
    /// qualified name is rejected rather than silently accepted and ignored.
    /// </summary>
    private static RecompressTarget? ParseTarget(string text, string option, Action<string> log)
    {
        var wanted = Normalize(text);
        var byLabel = RecompressTargets.All.FirstOrDefault(t => Normalize(t.Label) == wanted);
        if (byLabel is not null) return byLabel;

        var format = ResolveFormat(text, _ => { });
        if (format is null)
        {
            log($"[error] unknown {option} '{text}'");
            return null;
        }

        var target = RecompressTargets.All.FirstOrDefault(t => t.Unorm.Format == format.Format);
        if (target is not null) return target;

        log($"[error] {option} '{text}' names a specific colour space; the sRGB/UNORM variant follows " +
            "each source texture, so use the format family instead (e.g. bc1, bc7, astc 8x8)");
        return null;
    }

    private static RecompressTarget Target(string label) =>
        RecompressTargets.All.First(t => t.Label == label);

    private static string Normalize(string text) =>
        string.Concat(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant));

    private static int Unknown(string op, Action<string> log)
    {
        log($"[error] unknown --file op '{op}'");
        Usage(log);
        return 2;
    }

    internal static void Usage(Action<string> log)
    {
        log("usage: --file <op> [args] [selectors] [options]");
        log("  new <path> [--kind wta|wtb] [--force]        create an empty bank");
        log("  save <path>                                  rewrite a bank (round-trip)");
        log("  export <path> <out> [--as png|dds|xt1]       export texture(s); no selector = all");
        log("  import <path> <image|file.dds>               replace one texture from an image or DDS");
        log("  add <path> <image|file.dds|file.xt1> [--bank N]  add a texture to a bank");
        log("  delete <path>                                delete one texture");
        log("  replace-raw <path> <file|payload> [--force]  replace from an .xt1 or raw payload");
        log("  dummy <path> [--fill white|black|normal]     replace with a solid 8x8 ASTC 8x8 (2D only)");
        log("  recompress <path>...                         recompress ASTC textures to a chosen format");
        log("");
        log("selectors: --bank N  --index N  --hash 0xH");
        log("export:    --mip M  --face F  --slice S");
        log("import/add: --format 0xNN|name  --backend managed|nvtt|cli");
        log("            --quality fastest|normal|production|highest  --mips|--no-mips");
        log("            --solidify  --opaque  --name NAME");
        log("recompress: --rule bc1|bc1-bc3|bc1-bc7|bc7  --quality Q  --alpha-quality Q");
        log("            --opaque-format NAME  --alpha-format NAME  (bc7, astc 8x8; colour space follows source)");
        log("            --downscale  --downscale-without-mips  --no-sync-flags");
        log("writing:   --out DIR  --base NAME  --no-recompute  --no-trim-padding  --no-payload-side  --dry-run");
    }

    /// <summary>An <see cref="IProgress{T}"/> that logs inline, so headless runs need no message pump.</summary>
    private sealed class InlineProgress : IProgress<string>
    {
        private readonly Action<string> _log;
        public InlineProgress(Action<string> log) => _log = log;
        public void Report(string value) => _log(value);
    }
}
