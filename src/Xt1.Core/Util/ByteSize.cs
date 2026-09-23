namespace Xt1.Core.Util;

/// <summary>
/// The single byte-count formatter for the whole tool. Both shells used to keep their own copy, and
/// they disagreed on the unit labels (KB/MB/GB vs KiB/MiB/GiB) and precision, so the same file could
/// read differently in the browser, the inspector and the WPF pilot. Binary units throughout.
/// </summary>
public static class ByteSize
{
    public static string Format(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";

        double value = bytes / 1024.0;
        if (value < 1024) return $"{value:0.#} KiB";

        value /= 1024.0;
        if (value < 1024) return $"{value:0.#} MiB";

        value /= 1024.0;
        return $"{value:0.#} GiB";
    }
}
