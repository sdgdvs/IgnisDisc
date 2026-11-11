using System.IO;

namespace CdBurner.Setup;

/// <summary>
/// Creates, replaces and removes the IgnisDisc Desktop and Start menu shortcuts.
/// </summary>
/// <remarks>
/// Every released version (1.01 to 1.12) made the same two per-user shortcuts:
/// <c>Desktop\IgnisDisc Optical Studio.lnk</c> and
/// <c>Start Menu\Programs\IgnisDisc Optical Studio\IgnisDisc Optical Studio.lnk</c>.
/// The first installer build used "CdBurner Optical Studio" for both names and installed
/// <c>CdBurner.App.exe</c> into <c>%LOCALAPPDATA%\Programs\CdBurner</c>. No version wrote to the all-users
/// Start menu or Public Desktop (that would need administrator rights, which this per-user installer never asks for).
///
/// A shortcut is only removed when it carries one of those names, sits in one of those places, targets an IgnisDisc
/// program file, and that target is in the folder being installed or removed, in the retired CdBurner folder, or
/// no longer exists. Shortcuts that point at another IgnisDisc copy that still exists are left alone.
/// </remarks>
internal sealed class ShortcutManager
{
    public const string ShortcutFileName = Program.AppTitle + ".lnk";
    public const string Description = "IgnisDisc Optical Studio — Master CD, DVD, ISO/CUE & Red Book Audio Discs";

    /// <summary>Shortcut and Start menu folder names used by this and earlier versions.</summary>
    internal static readonly string[] KnownNames = { Program.AppTitle, "CdBurner Optical Studio" };

    /// <summary>Program files a shortcut of ours can point at.</summary>
    internal static readonly string[] KnownAppExeNames = { "IgnisDisc.exe", "CdBurner.App.exe" };

    private readonly string[] _retiredInstallDirs;

    public ShortcutManager(string desktopDir, string programsDir, IEnumerable<string> retiredInstallDirs)
    {
        DesktopDir = Path.GetFullPath(desktopDir);
        ProgramsDir = Path.GetFullPath(programsDir);
        _retiredInstallDirs = retiredInstallDirs.Select(NormalizeDir).ToArray();
    }

    public string DesktopDir { get; }

    public string ProgramsDir { get; }

    public string DesktopShortcutPath => Path.Combine(DesktopDir, ShortcutFileName);

    public string StartMenuFolder => Path.Combine(ProgramsDir, Program.AppTitle);

    public string StartMenuShortcutPath => Path.Combine(StartMenuFolder, ShortcutFileName);

    /// <summary>The signed-in user's real Desktop and Start menu.</summary>
    public static ShortcutManager ForCurrentUser() => new(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        Environment.GetFolderPath(Environment.SpecialFolder.Programs),
        new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "CdBurner")
        });

    /// <summary>
    /// <c>&lt;root&gt;\Desktop</c> and <c>&lt;root&gt;\Programs</c> instead of the real ones (the <c>--shortcut-root</c>
    /// option), so installer tests never touch the user's own shortcuts.
    /// </summary>
    public static ShortcutManager ForRoot(string root) => new(
        Path.Combine(root, "Desktop"),
        Path.Combine(root, "Programs"),
        Array.Empty<string>());

    /// <summary>
    /// Removes stale and duplicate IgnisDisc shortcuts, then creates (or replaces) the requested shortcuts so they
    /// point at <paramref name="appExePath"/>. Returns a message for each shortcut that could not be made.
    /// </summary>
    public IReadOnlyList<string> Install(string installDir, string appExePath, bool desktop, bool startMenu)
    {
        string installDirWithSep = NormalizeDir(installDir) + Path.DirectorySeparatorChar;
        var warnings = new List<string>();

        foreach (string link in FindKnownShortcuts())
        {
            bool isWantedDesktop = desktop && SamePath(link, DesktopShortcutPath);
            bool isWantedStartMenu = startMenu && SamePath(link, StartMenuShortcutPath);
            if (isWantedDesktop || isWantedStartMenu)
            {
                // Replaced below. Deleting first means a failed rewrite never leaves the old target in place.
                TryDelete(link);
                continue;
            }

            bool isCanonical = SamePath(link, DesktopShortcutPath) || SamePath(link, StartMenuShortcutPath);
            string? target = ShellLink.ReadTarget(link);
            if (target == null) continue;

            // A shortcut the user chose not to recreate this time stays if it still opens this install.
            bool stale = isCanonical
                ? IsDeadOrRetired(target)
                : IsOurs(target, installDirWithSep);
            if (stale) TryDelete(link);
        }

        if (desktop)
        {
            CreateAndCheck(DesktopShortcutPath, appExePath, installDir, "Desktop", warnings);
        }

        if (startMenu)
        {
            CreateAndCheck(StartMenuShortcutPath, appExePath, installDir, "Start menu", warnings);
        }

        RemoveEmptyStartMenuFolders();
        return warnings;
    }

    /// <summary>
    /// Removes every IgnisDisc shortcut that points into <paramref name="installDir"/>, into the retired CdBurner
    /// folder, or at an IgnisDisc program that no longer exists.
    /// </summary>
    public void Uninstall(string installDir)
    {
        string installDirWithSep = NormalizeDir(installDir) + Path.DirectorySeparatorChar;
        foreach (string link in FindKnownShortcuts())
        {
            string? target = ShellLink.ReadTarget(link);
            if (target != null && IsOurs(target, installDirWithSep)) TryDelete(link);
        }

        RemoveEmptyStartMenuFolders();
    }

    /// <summary>Existing shortcut files in every place this or an earlier version put one.</summary>
    internal IEnumerable<string> FindKnownShortcuts()
    {
        var found = new List<string>();
        foreach (string name in KnownNames)
        {
            AddIfExists(found, Path.Combine(DesktopDir, name + ".lnk"));
            AddIfExists(found, Path.Combine(ProgramsDir, name + ".lnk"));

            string folder = Path.Combine(ProgramsDir, name);
            if (Directory.Exists(folder))
            {
                try
                {
                    foreach (string link in Directory.GetFiles(folder, "*.lnk", SearchOption.TopDirectoryOnly))
                    {
                        AddIfExists(found, link);
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        return found.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>True when the target is an IgnisDisc program that is in this install, a retired folder, or gone.</summary>
    internal bool IsOurs(string target, string installDirWithSep)
    {
        if (!IsKnownAppExe(target)) return false;
        string? full = TryGetFullPath(target);
        return full != null &&
               (full.StartsWith(installDirWithSep, StringComparison.OrdinalIgnoreCase) || IsDeadOrRetired(full));
    }

    internal bool IsDeadOrRetired(string target)
    {
        if (!IsKnownAppExe(target)) return false;
        string? full = TryGetFullPath(target);
        if (full == null) return false;
        if (_retiredInstallDirs.Any(d => full.StartsWith(d + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // A missing target only counts as dead when its drive is present, so a copy on an unplugged USB drive keeps
        // its shortcut.
        string? root = Path.GetPathRoot(full);
        return !File.Exists(full) && !string.IsNullOrEmpty(root) && Directory.Exists(root);
    }

    private static string? TryGetFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool IsKnownAppExe(string target)
    {
        try
        {
            string name = Path.GetFileName(target);
            return KnownAppExeNames.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void CreateAndCheck(string linkPath, string appExePath, string workingDir, string where, List<string> warnings)
    {
        string? error = ShellLink.Write(linkPath, appExePath, workingDir, Description);
        string? written = error == null ? ShellLink.ReadTarget(linkPath) : null;
        if (error == null && written != null && SamePath(written, appExePath)) return;

        TryDelete(linkPath); // never leave a shortcut that opens something else
        warnings.Add($"The {where} shortcut could not be created ({error ?? "it did not point at " + appExePath}).");
    }

    private void RemoveEmptyStartMenuFolders()
    {
        foreach (string name in KnownNames)
        {
            string folder = Path.Combine(ProgramsDir, name);
            try
            {
                if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
                {
                    Directory.Delete(folder);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void AddIfExists(List<string> list, string path)
    {
        if (File.Exists(path)) list.Add(Path.GetFullPath(path));
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeDir(string dir) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));
}

/// <summary>Reads and writes .lnk files through the Windows Script Host shell object that ships with Windows.</summary>
internal static class ShellLink
{
    /// <summary>The shortcut's target path, or null when it cannot be read.</summary>
    public static string? ReadTarget(string linkPath)
    {
        if (!File.Exists(linkPath)) return null;
        try
        {
            dynamic? shell = CreateShell();
            if (shell == null) return null;
            dynamic link = shell.CreateShortcut(linkPath);
            string target = (string)link.TargetPath;
            return string.IsNullOrWhiteSpace(target) ? null : target;
        }
        catch (Exception)
        {
            return null; // damaged or unreadable shortcut: treat as unknown and leave it alone
        }
    }

    /// <summary>Writes the shortcut. Returns null on success, otherwise the reason it failed.</summary>
    public static string? Write(string linkPath, string targetPath, string workingDir, string description)
    {
        try
        {
            string? folder = Path.GetDirectoryName(linkPath);
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

            dynamic? shell = CreateShell();
            if (shell == null) return "the Windows shortcut service is not available";
            dynamic link = shell.CreateShortcut(linkPath);
            link.TargetPath = targetPath;
            link.WorkingDirectory = workingDir;
            link.Description = description;
            link.IconLocation = $"{targetPath},0";
            link.Save();
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private static dynamic? CreateShell()
    {
        Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
        return shellType == null ? null : Activator.CreateInstance(shellType);
    }
}
