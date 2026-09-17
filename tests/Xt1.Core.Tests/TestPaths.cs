namespace Xt1.Core.Tests;

/// <summary>Paths to the real Bayonetta 3 golden samples. Tests skip silently when absent.</summary>
public static class TestPaths
{
    public const string Romfs = @"D:\Bayonetta3\romfs";

    public static string Pl0080Dat => Path.Combine(Romfs, "pl", "pl0080.dat");
    public static string Em0000Dat => Path.Combine(Romfs, "em", "em0000.dat");
    public static string UiCoreDat => Path.Combine(Romfs, "ui", "ui_core.dat");
    public static string CoreDat => Path.Combine(Romfs, "core", "core.dat");
    public static string Pl0000Dtt => Path.Combine(Romfs, "pl", "pl0000.dtt");
    public static string UiCoreDtt => Path.Combine(Romfs, "ui", "ui_core.dtt");
    public static string CubeMapWtb => Path.Combine(Romfs, "core", "core_dat", "CubeMap.wtb");
    public static string GradingWtb => Path.Combine(Romfs, "core", "core_dat", "Grading.wtb");
    public static string PreTextureWta => Path.Combine(Romfs, "core", "core_dat", "pre_texture.wta");
    public static string PreTextureWtp => Path.Combine(Romfs, "core", "core_dat", "pre_texture.wtp");

    /// <summary>Split effect banks: `NNN.wta` + `NNN.wtp`, when the effect pkz has been extracted.</summary>
    public static string EffectTextureDirectory => Path.Combine(Romfs, "effect", "texture");

    /// <summary>
    /// An "aliased offset" DAT: the `"wta"` entry points at a WTB nested inside the `"eff"`
    /// sub-file, and the `hkx`/`eff` offsets are stub zeros, so the offset table is not monotonic.
    /// </summary>
    public static string Es0001Dat => Path.Combine(Romfs, "es", "es0001.dat");

    /// <summary>
    /// Container mixing both bank kinds: `core.dat` holds the `"wta"` bank (`pre_texture.wta`,
    /// payload in `core.dtt`'s `"wtp"`) while `core.dtt` additionally holds `"wtb"` sub-files —
    /// standalone banks (`CubeMap.wtb`, `Grading.wtb`) carrying their payload inline.
    /// </summary>
    public static string CoreDtt => Path.Combine(Romfs, "core", "core.dtt");

    /// <summary>A `.dtt` whose only banks are `"wtb"` sub-files plus one `"wta"`.</summary>
    public static string Z5000Dtt => Path.Combine(Romfs, "st5", "z5000.dtt");

    public static bool Exists(string path) => File.Exists(path);
}
