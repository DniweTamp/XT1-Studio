namespace Xt1.Core.Editing;

/// <summary>One file to write to disk.</summary>
public sealed record OutputFile(string Path, byte[] Bytes);

public sealed class WriteOptions
{
    /// <summary>Recompute <c>table0</c> payload offsets as align4096 cumulative sizes before writing.</summary>
    public bool RecomputePayloadOffsets { get; set; } = true;

    /// <summary>Compress the payload side (`.wtp` / `.dtt`) with Oodle when the DLL supports it.</summary>
    public bool RecompressOodle { get; set; }

    public string? OodlePath { get; set; }

    /// <summary>Write the payload side even when it is unchanged.</summary>
    public bool WritePayloadSide { get; set; } = true;

    /// <summary>Directory override; defaults next to the source file.</summary>
    public string? OutputDirectory { get; set; }

    /// <summary>Base file name (without extension) override.</summary>
    public string? OutputBaseName { get; set; }
}

public sealed record WriteResult(IReadOnlyList<OutputFile> Files, IReadOnlyList<string> Warnings)
{
    /// <summary>
    /// Writes every output. A bank emits a coupled pair (`.dat`+`.dtt` or `.wta`+`.wtp`), so all
    /// files are staged next to their destinations first and only swapped in once every write has
    /// succeeded. The previous content of each destination is moved aside before it is replaced and
    /// is put back if any later swap fails, so a disk-full/locked/permission failure cannot leave a
    /// half-updated pair, and no destination is destroyed without a recoverable copy.
    /// </summary>
    public void SaveAll()
    {
        var staged = new List<(string Temp, string Final, string? Backup)>(Files.Count);
        try
        {
            foreach (var file in Files)
            {
                var directory = Path.GetDirectoryName(file.Path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                var temp = file.Path + ".xt1-staging-" + Guid.NewGuid().ToString("N");
                File.WriteAllBytes(temp, file.Bytes);
                staged.Add((temp, file.Path, null));
            }

            for (int i = 0; i < staged.Count; i++)
            {
                var (temp, final, _) = staged[i];
                string? backup = null;
                if (File.Exists(final))
                {
                    backup = final + ".xt1-backup-" + Guid.NewGuid().ToString("N");
                    File.Move(final, backup);
                }

                staged[i] = (temp, final, backup);
                File.Move(temp, final);
            }
        }
        catch
        {
            // Roll back newest-first: restore each destination that was already replaced, then drop
            // the staged files. A leftover backup or staging file is preferable to losing content,
            // so this stays best-effort and never hides the original failure.
            for (int i = staged.Count - 1; i >= 0; i--)
            {
                var (temp, final, backup) = staged[i];
                try
                {
                    if (backup is not null)
                    {
                        if (File.Exists(backup)) File.Move(backup, final, overwrite: true);
                    }
                    else if (!File.Exists(temp) && File.Exists(final))
                    {
                        // The swap completed for a destination that did not exist before, so the
                        // file there is one we created: remove it.
                        File.Delete(final);
                    }

                    if (File.Exists(temp)) File.Delete(temp);
                }
                catch
                {
                    // Best effort.
                }
            }

            throw;
        }

        foreach (var (_, _, backup) in staged)
        {
            if (backup is null) continue;
            try
            {
                if (File.Exists(backup)) File.Delete(backup);
            }
            catch
            {
                // Best effort: a leftover backup is harmless.
            }
        }
    }
}
