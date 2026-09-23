using Xt1.Shell;

namespace Xt1.App.Wpf;

/// <summary>
/// The WPF app's view of the Explorer thumbnail registration. Mirrors the WinForms
/// <c>Xt1.App.ShellIntegration</c> so the Settings dialog phrases the state identically; the
/// registration itself lives in <see cref="ShellRegistration"/>.
/// </summary>
internal static class ShellIntegration
{
    /// <summary>The COM host shipped beside the app, or null when it was not deployed.</summary>
    internal static string? ComHost(string? explicitPath = null) => ShellRegistration.FindComHost(explicitPath);

    internal static bool IsRegistered(bool machine) => ShellRegistration.IsRegistered(machine);

    /// <summary>Enable for the chosen scope; throws with something worth reading when it cannot.</summary>
    internal static void Enable(bool machine, string? explicitPath = null)
    {
        var comHost = ComHost(explicitPath);
        if (comHost is null)
            throw new InvalidOperationException(
                "The shell host was not found: expected shell\\Xt1.Shell.comhost.dll beside XT1 Studio.exe. " +
                "Use a published build (publish-release.bat) or rebuild the app.");

        try
        {
            ShellRegistration.Register(comHost, machine);
        }
        catch (UnauthorizedAccessException ex) when (machine)
        {
            throw new InvalidOperationException(
                "Registering for all users needs an elevated (administrator) shell. " + ex.Message, ex);
        }
    }

    internal static void Disable(bool machine) => ShellRegistration.Unregister(machine);

    /// <summary>A one-line state description for the status line.</summary>
    internal static string Describe(bool machine, bool registered, string? comHost)
    {
        if (registered)
            return machine
                ? "Registered for all users on this machine: Explorer thumbnails loose .xt1 files."
                : "Registered for this user: Explorer thumbnails loose .xt1 files. Restart Explorer to " +
                  "refresh files it has already listed.";

        if (comHost is null)
            return "Shell host missing: expected shell\\Xt1.Shell.comhost.dll beside XT1 Studio.exe " +
                   "(publish or rebuild the app to include it).";

        return "Not registered: Explorer shows the generic icon for loose .xt1 files. " +
               "Banks (.dat/.wta/.wtb) are not thumbnailed.";
    }
}
