using Microsoft.Win32;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Xt1.Shell;

/// <summary>
/// Writes (and removes) the registry entries that make Explorer ask
/// <see cref="Xt1ThumbnailProvider"/> for <c>.xt1</c> thumbnails.
/// <para>
/// Everything lives under <c>Software\Classes</c> in HKCU by default, so registering needs no
/// administrator; <c>machine: true</c> uses HKLM instead for an all-users install. The CLSID block
/// is what the COM host needs to be found: <c>InprocServer32</c> names the native
/// <c>Xt1.Shell.comhost.dll</c>, and <c>Class</c>/<c>Assembly</c> name the managed type it loads.
/// </para>
/// </summary>
public static class ShellRegistration
{
    private const string ManagedClass = "Xt1.Shell.Xt1ThumbnailProvider";
    private const string ManagedAssembly = "Xt1.Shell";
    private const string RuntimeVersion = "v4.0.30319";
    private const string FriendlyName = "XT1 Studio thumbnail handler";
    private const string PerceivedType = "image";

    public static string Clsid => "{" + ShellIds.HandlerClsid + "}";

    /// <summary>Where the published/`built` app keeps the COM host: a `shell` folder beside the exe.</summary>
    public static string DefaultComHostPath =>
        Path.Combine(AppContext.BaseDirectory, "shell", "Xt1.Shell.comhost.dll");

    /// <summary>
    /// Locate the COM host: an explicit path, the `shell` folder beside the app, or the app folder
    /// itself (a plain project-reference build drops it there).
    /// </summary>
    public static string? FindComHost(string? explicitPath = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return File.Exists(explicitPath) ? Path.GetFullPath(explicitPath) : null;

        foreach (var candidate in new[]
                 {
                     DefaultComHostPath,
                     Path.Combine(AppContext.BaseDirectory, "Xt1.Shell.comhost.dll"),
                 })
        {
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }

        return null;
    }

    public static void Register(string comHostPath, bool machine = false)
    {
        var full = Path.GetFullPath(comHostPath);
        if (!File.Exists(full))
            throw new FileNotFoundException("the COM host DLL was not found", full);

        // The host loads the managed assembly from its own folder, so both halves must be together.
        var managed = Path.Combine(Path.GetDirectoryName(full)!, ManagedAssembly + ".dll");
        if (!File.Exists(managed))
            throw new FileNotFoundException($"{ManagedAssembly}.dll must sit beside the COM host", managed);
        if (machine && !IsMachineSafePath(full))
            throw new InvalidOperationException(
                "All-users registration requires the shell handler to be installed under Program Files. " +
                "Registering a portable copy from a user-writable folder would let an unprivileged user " +
                "replace code loaded for every user.");

        using var classes = OpenClassesForWrite(machine);

        // 1. Explorer looks up the thumbnail handler by the file type's extension.
        using (var extension = classes.CreateSubKey(ShellIds.Extension))
        {
            extension.SetValue("PerceivedType", PerceivedType, RegistryValueKind.String);
            using var handlers = extension.CreateSubKey(@"ShellEx\{" + ShellIds.ThumbnailProviderInterface + "}");
            handlers.SetValue(string.Empty, Clsid, RegistryValueKind.String);
        }

        // 2. The CLSID block the handler is resolved through.
        using (var clsid = classes.CreateSubKey(@"CLSID\" + Clsid))
        {
            clsid.SetValue(string.Empty, FriendlyName, RegistryValueKind.String);
            using var inproc = clsid.CreateSubKey("InprocServer32");
            inproc.SetValue(string.Empty, full, RegistryValueKind.String);
            // "ThreadingModel must be Apartment for shell extension handlers" - the shell's folder
            // views run on an STA thread, and Free/Both cause the intermittent failures the docs
            // warn about. Paint.NET registers its own thumbnail handler the same way.
            inproc.SetValue("ThreadingModel", "Apartment", RegistryValueKind.String);
            inproc.SetValue("Class", ManagedClass, RegistryValueKind.String);
            inproc.SetValue("Assembly", ManagedAssembly, RegistryValueKind.String);
            inproc.SetValue("RuntimeVersion", RuntimeVersion, RegistryValueKind.String);
        }

    }

    public static void Unregister(bool machine = false)
    {
        using var classes = OpenClassesForWrite(machine);

        var handlerPath = Path.Combine(ShellIds.Extension, "ShellEx",
            "{" + ShellIds.ThumbnailProviderInterface + "}");
        using (var handler = classes.OpenSubKey(handlerPath, writable: false))
        {
            if (handler?.GetValue(string.Empty) as string == Clsid)
                TryDeleteTree(classes, handlerPath);
        }

        // The CLSID is ours only while it still names our managed class/assembly. Do not remove a
        // key another installation may have deliberately taken over.
        var inprocPath = @"CLSID\" + Clsid + @"\InprocServer32";
        using (var inproc = classes.OpenSubKey(inprocPath, writable: false))
        {
            if (inproc?.GetValue("Class") as string == ManagedClass &&
                inproc.GetValue("Assembly") as string == ManagedAssembly)
                TryDeleteTree(classes, @"CLSID\" + Clsid);
        }

        // Only remove the hint if it is the one we wrote.
        using var extension = classes.OpenSubKey(ShellIds.Extension, writable: true);
        if (extension?.GetValue("PerceivedType") as string == PerceivedType)
            extension.DeleteValue("PerceivedType", throwOnMissingValue: false);
    }

    /// <summary>
    /// Whether this scope contains XT1 Studio's registry ownership markers. This intentionally does
    /// not require the target DLL to exist, so a moved/deleted installation can still unregister.
    /// </summary>
    public static bool IsRegistered(bool machine = false)
    {
        using var classes = OpenClassesForRead(machine);
        if (classes is null) return false;

        using var handler = classes.OpenSubKey(
            Path.Combine(ShellIds.Extension, "ShellEx", "{" + ShellIds.ThumbnailProviderInterface + "}"));
        if (handler?.GetValue(string.Empty) as string != Clsid) return false;

        using var inproc = classes.OpenSubKey(@"CLSID\" + Clsid + @"\InprocServer32");
        return inproc?.GetValue("Class") as string == ManagedClass &&
               inproc.GetValue("Assembly") as string == ManagedAssembly;
    }

    private static bool IsMachineSafePath(string path)
    {
        bool underProgramFiles = false;
        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                 })
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                underProgramFiles = true;
                break;
            }
        }
        if (!underProgramFiles) return false;

        var lowPrivilegeSids = new HashSet<SecurityIdentifier>
        {
            new(WellKnownSidType.WorldSid, null),
            new(WellKnownSidType.AuthenticatedUserSid, null),
            new(WellKnownSidType.BuiltinUsersSid, null),
        };
        const FileSystemRights writes = FileSystemRights.Write | FileSystemRights.Modify |
                                        FileSystemRights.FullControl | FileSystemRights.Delete |
                                        FileSystemRights.DeleteSubdirectoriesAndFiles;
        return !AllowsLowPrivilegeWrite(new DirectoryInfo(Path.GetDirectoryName(path)!).GetAccessControl()) &&
               !AllowsLowPrivilegeWrite(new FileInfo(path).GetAccessControl());

        bool AllowsLowPrivilegeWrite(FileSystemSecurity acl)
        {
            foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType == AccessControlType.Allow &&
                    rule.IdentityReference is SecurityIdentifier sid && lowPrivilegeSids.Contains(sid) &&
                    (rule.FileSystemRights & writes) != 0)
                    return true;
            }
            return false;
        }
    }

    private static RegistryKey OpenClassesForWrite(bool machine) =>
        (machine ? Registry.LocalMachine : Registry.CurrentUser)
        .CreateSubKey(@"Software\Classes", writable: true)!;

    private static RegistryKey? OpenClassesForRead(bool machine) =>
        (machine ? Registry.LocalMachine : Registry.CurrentUser)
        .OpenSubKey(@"Software\Classes", writable: false);

    private static void TryDeleteTree(RegistryKey root, string path)
    {
        try
        {
            root.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // Reporting beats a half-removed registration blocking the unregister.
            throw new InvalidOperationException($"could not remove the registry key {path}: {ex.Message}", ex);
        }
    }
}
