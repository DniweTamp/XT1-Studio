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
    /// succeeded. That way a disk-full/locked/permission failure cannot leave a half-updated pair.
    /// </summary>
    public void SaveAll()
    {
        var staged = new List<(string Temp, string Final)>(Files.Count);
        try
        {
            foreach (var file in Files)
            {
                var directory = Path.GetDirectoryName(file.Path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                var temp = file.Path + ".xt1-staging-" + Guid.NewGuid().ToString("N");
                File.WriteAllBytes(temp, file.Bytes);
                staged.Add((temp, file.Path));
            }

            foreach (var (temp, final) in staged)
                File.Move(temp, final, overwrite: true);
        }
        catch
        {
            foreach (var (temp, _) in staged)
            {
                try
                {
                    if (File.Exists(temp)) File.Delete(temp);
                }
                catch
                {
                    // Best effort: a leftover staging file is harmless.
                }
            }
            throw;
        }
    }
}
