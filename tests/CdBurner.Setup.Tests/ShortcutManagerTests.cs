using System.IO;
using CdBurner.Setup;

namespace CdBurner.Setup.Tests;

/// <summary>
/// Shortcut handling on install, reinstall and uninstall. Every test uses its own temporary Desktop and Start menu
/// folders, so the real shortcuts of the machine running the tests are never read or changed.
/// </summary>
public sealed class ShortcutManagerTests : IDisposable
{
    private const string LinkName = "IgnisDisc Optical Studio.lnk";
    private const string OldLinkName = "CdBurner Optical Studio.lnk";

    private readonly string _root;
    private readonly string _desktop;
    private readonly string _programs;
    private readonly string _installDir;
    private readonly string _appExe;
    private readonly string _retiredDir;
    private readonly ShortcutManager _shortcuts;

    public ShortcutManagerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ignisdisc-shortcut-tests-" + Guid.NewGuid().ToString("N"));
        _desktop = Path.Combine(_root, "shell", "Desktop");
        _programs = Path.Combine(_root, "shell", "Programs");
        _installDir = Path.Combine(_root, "Programs", "IgnisDisc");
        _retiredDir = Path.Combine(_root, "Programs", "CdBurner");
        Directory.CreateDirectory(_desktop);
        Directory.CreateDirectory(_programs);
        Directory.CreateDirectory(_installDir);
        _appExe = Path.Combine(_installDir, "IgnisDisc.exe");
        File.WriteAllText(_appExe, "stand-in for the installed program");
        _shortcuts = new ShortcutManager(_desktop, _programs, new[] { _retiredDir });
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string DesktopLink => Path.Combine(_desktop, LinkName);

    private string StartMenuLink => Path.Combine(_programs, "IgnisDisc Optical Studio", LinkName);

    private static void MakeLink(string linkPath, string target) =>
        Assert.Null(ShellLink.Write(linkPath, target, Path.GetDirectoryName(target)!, "test shortcut"));

    private string[] AllLinks() =>
        Directory.GetFiles(Path.Combine(_root, "shell"), "*.lnk", SearchOption.AllDirectories);

    [Fact]
    public void Install_replaces_shortcuts_from_old_versions_and_leaves_one_per_place()
    {
        // Start menu shortcut from an install in a folder that no longer exists.
        MakeLink(StartMenuLink, Path.Combine(_root, "OldPlace", "IgnisDisc", "IgnisDisc.exe"));
        // First-build names, pointing at the retired CdBurner folder (still on disk) and at a missing file.
        Directory.CreateDirectory(_retiredDir);
        File.WriteAllText(Path.Combine(_retiredDir, "CdBurner.App.exe"), "old");
        MakeLink(Path.Combine(_desktop, OldLinkName), Path.Combine(_retiredDir, "CdBurner.App.exe"));
        MakeLink(Path.Combine(_programs, "CdBurner Optical Studio", OldLinkName), Path.Combine(_root, "gone", "CdBurner.App.exe"));
        // A second copy of the shortcut directly in Programs, pointing at this install.
        MakeLink(Path.Combine(_programs, LinkName), _appExe);

        IReadOnlyList<string> warnings = _shortcuts.Install(_installDir, _appExe, desktop: true, startMenu: true);

        Assert.Empty(warnings);
        string[] links = AllLinks();
        Assert.Equal(2, links.Length);
        Assert.Contains(links, l => string.Equals(l, DesktopLink, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(links, l => string.Equals(l, StartMenuLink, StringComparison.OrdinalIgnoreCase));
        Assert.All(links, l => Assert.Equal(_appExe, ShellLink.ReadTarget(l), ignoreCase: true));
        Assert.False(Directory.Exists(Path.Combine(_programs, "CdBurner Optical Studio")));
        Assert.True(File.Exists(Path.Combine(_retiredDir, "CdBurner.App.exe")), "only shortcuts are removed, never program files");
    }

    [Fact]
    public void Reinstall_repoints_a_current_shortcut_that_opens_another_folder()
    {
        string otherCopy = Path.Combine(_root, "Elsewhere", "IgnisDisc.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(otherCopy)!);
        File.WriteAllText(otherCopy, "another copy");
        MakeLink(StartMenuLink, otherCopy);
        MakeLink(DesktopLink, otherCopy);

        _shortcuts.Install(_installDir, _appExe, desktop: true, startMenu: true);

        Assert.Equal(_appExe, ShellLink.ReadTarget(StartMenuLink), ignoreCase: true);
        Assert.Equal(_appExe, ShellLink.ReadTarget(DesktopLink), ignoreCase: true);
        Assert.Equal(2, AllLinks().Length);
    }

    [Fact]
    public void Install_leaves_shortcuts_to_other_existing_copies_and_unrelated_shortcuts()
    {
        string portableCopy = Path.Combine(_root, "Portable", "IgnisDisc.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(portableCopy)!);
        File.WriteAllText(portableCopy, "portable copy");
        string userLink = Path.Combine(_programs, LinkName);
        MakeLink(userLink, portableCopy);
        string unrelated = Path.Combine(_desktop, "Notepad.lnk");
        MakeLink(unrelated, Path.Combine(_root, "gone", "notepad.exe"));

        _shortcuts.Install(_installDir, _appExe, desktop: true, startMenu: true);

        Assert.Equal(portableCopy, ShellLink.ReadTarget(userLink), ignoreCase: true);
        Assert.True(File.Exists(unrelated));
    }

    [Fact]
    public void Install_without_shortcuts_keeps_a_working_one_and_removes_a_dead_one()
    {
        MakeLink(DesktopLink, _appExe);
        MakeLink(StartMenuLink, Path.Combine(_root, "gone", "IgnisDisc.exe"));

        IReadOnlyList<string> warnings = _shortcuts.Install(_installDir, _appExe, desktop: false, startMenu: false);

        Assert.Empty(warnings);
        Assert.Equal(_appExe, ShellLink.ReadTarget(DesktopLink), ignoreCase: true);
        Assert.False(File.Exists(StartMenuLink));
        Assert.False(Directory.Exists(Path.GetDirectoryName(StartMenuLink)));
    }

    [Fact]
    public void Uninstall_removes_shortcuts_into_that_folder_and_keeps_other_copies()
    {
        _shortcuts.Install(_installDir, _appExe, desktop: true, startMenu: true);
        string portableCopy = Path.Combine(_root, "Portable", "IgnisDisc.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(portableCopy)!);
        File.WriteAllText(portableCopy, "portable copy");
        string userLink = Path.Combine(_programs, LinkName);
        MakeLink(userLink, portableCopy);
        MakeLink(Path.Combine(_desktop, OldLinkName), Path.Combine(_root, "gone", "CdBurner.App.exe"));

        _shortcuts.Uninstall(_installDir);

        Assert.False(File.Exists(DesktopLink));
        Assert.False(File.Exists(StartMenuLink));
        Assert.False(Directory.Exists(Path.GetDirectoryName(StartMenuLink)));
        Assert.False(File.Exists(Path.Combine(_desktop, OldLinkName)));
        Assert.Equal(portableCopy, ShellLink.ReadTarget(userLink), ignoreCase: true);
    }

    [Fact]
    public void A_copy_on_a_drive_that_is_not_present_is_not_treated_as_gone()
    {
        char? missingDrive = null;
        var present = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        for (char c = 'Z'; c >= 'D'; c--)
        {
            if (!present.Contains(c)) { missingDrive = c; break; }
        }

        if (missingDrive == null) return; // every drive letter is in use on this machine

        string unpluggedCopy = $@"{missingDrive}:\IgnisDisc\IgnisDisc.exe";
        Assert.False(_shortcuts.IsDeadOrRetired(unpluggedCopy));
        Assert.True(_shortcuts.IsDeadOrRetired(Path.Combine(_root, "gone", "IgnisDisc.exe")));
        Assert.False(_shortcuts.IsDeadOrRetired(Path.Combine(_root, "gone", "SomethingElse.exe")));
    }

    [Fact]
    public void Existing_version_comes_from_the_installed_program_file()
    {
        // The setup program carries the same version stamp as IgnisDisc.exe; use it as the stand-in program file.
        string setupExe = Path.Combine(AppContext.BaseDirectory, "Uninstall-IgnisDisc.exe");
        Assert.True(File.Exists(setupExe), setupExe);
        File.Copy(setupExe, _appExe, overwrite: true);

        Assert.Equal(Program.AppVersion, Program.GetExistingInstallVersion(_installDir));
    }
}
