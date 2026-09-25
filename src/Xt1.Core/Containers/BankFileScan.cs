namespace Xt1.Core.Containers;

/// <summary>
/// The bank files the shell and its folder tools can open. One list so the file browser, the folder
/// export and the batch-recompress tools cannot disagree about what counts as a bank.
/// </summary>
public static class BankFileScan
{
    /// <summary>
    /// Openable bank header extensions. Payload-only siblings (`.dtt`, `.wtp`) are never scanned;
    /// loading the `.dat`/`.wta` header pairs them automatically.
    /// </summary>
    public static readonly string[] Extensions = { ".dat", ".wta", ".wtb", ".xt1" };

    /// <summary>Whether a path names an openable bank header (case-insensitive, extension only).</summary>
    public static bool IsBankFile(string path)
    {
        foreach (var extension in Extensions)
            if (path.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
