namespace Xt1.App.Wpf.Cli;

/// <summary>
/// Option parsing shared by the command-line diagnostics. One implementation means an option like
/// <c>--index</c> is read the same way by every subcommand; <c>0x</c>-prefixed indices are honoured
/// wherever an index is accepted.
/// </summary>
internal static class CliOptions
{
    /// <summary>The value after <paramref name="name"/>, or null when the option is absent.</summary>
    public static string? Get(string[] args, string name)
    {
        int at = Array.IndexOf(args, name);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }

    /// <summary>An integer option: decimal, or hexadecimal when prefixed with <c>0x</c>.</summary>
    public static int? GetInt(string[] args, string name)
    {
        var value = Get(args, name);
        if (value is null) return null;
        return int.TryParse(value, out int parsed) ? parsed
            : value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
              int.TryParse(value[2..], System.Globalization.NumberStyles.HexNumber, null, out parsed)
                ? parsed
                : null;
    }

    /// <summary>A hash option, read as hex with or without the <c>0x</c> prefix.</summary>
    public static uint? GetUInt(string[] args, string name)
    {
        var value = Get(args, name);
        if (value is null) return null;
        var text = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value;
        return uint.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out uint parsed)
            ? parsed
            : uint.TryParse(value, out parsed) ? parsed : null;
    }

    public static float? GetFloat(string[] args, string name)
    {
        var value = Get(args, name);
        if (value is null) return null;
        return float.TryParse(value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float parsed)
            ? parsed
            : null;
    }
}
