using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace CdBurner.Setup;

internal static class Program
{
    public const string AppTitle = "IgnisDisc Optical Studio";
    public const string AppVersion = "1.04";
    public const string Publisher = "Ignis Optical Engineering";
    public const string UninstallRegKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\IgnisDisc";
    public const string LegacyUninstallRegKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\CdBurner";

    [STAThread]
    public static int Main(string[] args)
    {
        string defaultInstallDir = GetExistingInstallLocation() ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            "IgnisDisc");

        bool silent = false;
        bool uninstall = false;
        bool createDesktopShortcut = true;
        bool createStartMenuShortcut = true;
        bool addToPath = true;
        string installDir = defaultInstallDir;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (string.Equals(arg, "--silent", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(arg, "/S", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(arg, "/quiet", StringComparison.OrdinalIgnoreCase))
            {
                silent = true;
            }
            else if (string.Equals(arg, "--uninstall", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(arg, "/uninstall", StringComparison.OrdinalIgnoreCase))
            {
                uninstall = true;
            }
            else if (string.Equals(arg, "--no-shortcuts", StringComparison.OrdinalIgnoreCase))
            {
                createDesktopShortcut = false;
                createStartMenuShortcut = false;
            }
            else if (string.Equals(arg, "--no-path", StringComparison.OrdinalIgnoreCase))
            {
                addToPath = false;
            }
            else if (string.Equals(arg, "--install-dir", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                installDir = Path.GetFullPath(args[++i]);
            }
        }

        if (uninstall)
        {
            return RunUninstall(installDir, silent);
        }

        if (silent)
        {
            try
            {
                InstallPayload(
                    installDir,
                    createDesktopShortcut,
                    createStartMenuShortcut,
                    addToPath,
                    registerUninstaller: installDir.Equals(defaultInstallDir, StringComparison.OrdinalIgnoreCase),
                    progress: null);
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Installation failed: {ex.Message}");
                return 1;
            }
        }

        var app = new System.Windows.Application();
        var window = new SetupWizardWindow(installDir);
        app.Run(window);
        return window.ExitCode;
    }

    public static string? GetExistingInstallLocation()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallRegKeyPath)
                            ?? Registry.CurrentUser.OpenSubKey(LegacyUninstallRegKeyPath);
            if (key?.GetValue("InstallLocation") is string loc && Directory.Exists(loc))
            {
                return loc;
            }
        }
        catch { }

        string defaultDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            "IgnisDisc");
        if (Directory.Exists(defaultDir) && File.Exists(Path.Combine(defaultDir, "IgnisDisc.exe")))
        {
            return defaultDir;
        }

        string legacyDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            "CdBurner");
        if (Directory.Exists(legacyDir) && File.Exists(Path.Combine(legacyDir, "CdBurner.App.exe")))
        {
            return legacyDir;
        }

        return null;
    }

    public static string? GetExistingInstallVersion()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallRegKeyPath)
                            ?? Registry.CurrentUser.OpenSubKey(LegacyUninstallRegKeyPath);
            if (key?.GetValue("DisplayVersion") is string ver && !string.IsNullOrWhiteSpace(ver))
            {
                return ver;
            }
        }
        catch { }

        string defaultDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            "IgnisDisc");
        string exe = Path.Combine(defaultDir, "IgnisDisc.exe");
        if (File.Exists(exe))
        {
            try
            {
                var vi = FileVersionInfo.GetVersionInfo(exe);
                return vi.ProductVersion?.Split('+')[0] ?? vi.FileVersion;
            }
            catch { }
        }

        string legacyDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            "CdBurner");
        string legacyExe = Path.Combine(legacyDir, "CdBurner.App.exe");
        if (File.Exists(legacyExe))
        {
            try
            {
                var vi = FileVersionInfo.GetVersionInfo(legacyExe);
                return vi.ProductVersion?.Split('+')[0] ?? vi.FileVersion;
            }
            catch { }
        }

        return null;
    }

    public static bool EnsureRunningProcessesClosed(string installDir, bool silent)
    {
        string[] processNames = { "IgnisDisc", "CdBurner.App", "ignisdisc-cli", "cdburner-cli" };
        var running = new List<Process>();

        foreach (var name in processNames)
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                try
                {
                    string? exePath = null;
                    try { exePath = p.MainModule?.FileName; } catch { }

                    if (string.IsNullOrEmpty(exePath) ||
                        string.IsNullOrEmpty(installDir) ||
                        exePath.StartsWith(Path.GetFullPath(installDir), StringComparison.OrdinalIgnoreCase))
                    {
                        running.Add(p);
                    }
                }
                catch
                {
                    running.Add(p);
                }
            }
        }

        if (running.Count == 0)
        {
            return true;
        }

        if (!silent)
        {
            var res = System.Windows.MessageBox.Show(
                $"{AppTitle} is currently running.\n\nClick OK to close it automatically and proceed with the update, or Cancel to abort.",
                $"{AppTitle} Setup",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Information);

            if (res != MessageBoxResult.OK)
            {
                return false;
            }
        }

        foreach (var p in running)
        {
            try
            {
                if (!p.HasExited)
                {
                    p.CloseMainWindow();
                    if (!p.WaitForExit(2000))
                    {
                        p.Kill();
                        p.WaitForExit(1000);
                    }
                }
            }
            catch { }
            finally
            {
                p.Dispose();
            }
        }

        return true;
    }

    public static Stream OpenPayloadStream()
    {
        var asm = Assembly.GetExecutingAssembly();
        var stream = asm.GetManifestResourceStream("IgnisDisc.Payload.zip")
                     ?? asm.GetManifestResourceStream("CdBurner.Payload.zip");
        if (stream != null)
        {
            return stream;
        }

        string exeDir = AppContext.BaseDirectory;
        string fallbackZip = Path.Combine(exeDir, "IgnisDisc-Portable-win-x64.zip");
        if (File.Exists(fallbackZip))
        {
            return File.OpenRead(fallbackZip);
        }

        throw new FileNotFoundException("Embedded installer payload (IgnisDisc.Payload.zip) was not found.");
    }

    public static void InstallPayload(
        string installDir,
        bool createDesktopShortcut,
        bool createStartMenuShortcut,
        bool addToPath,
        bool registerUninstaller,
        IProgress<(double Percent, string Status)>? progress)
    {
        Directory.CreateDirectory(installDir);
        progress?.Report((2, "Checking for running instances..."));

        if (!EnsureRunningProcessesClosed(installDir, silent: progress == null))
        {
            throw new InvalidOperationException($"Update was cancelled because {AppTitle} is currently running.");
        }

        progress?.Report((5, $"Preparing target directory: {installDir}"));

        using (Stream payloadStream = OpenPayloadStream())
        using (var archive = new ZipArchive(payloadStream, ZipArchiveMode.Read))
        {
            int totalEntries = Math.Max(1, archive.Entries.Count);
            int processed = 0;

            foreach (var entry in archive.Entries)
            {
                processed++;
                string destinationPath = Path.GetFullPath(Path.Combine(installDir, entry.FullName));
                if (!destinationPath.StartsWith(Path.GetFullPath(installDir), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(destinationPath);
                    continue;
                }

                string? parentDir = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrEmpty(parentDir))
                {
                    Directory.CreateDirectory(parentDir);
                }

                entry.ExtractToFile(destinationPath, overwrite: true);

                double pct = 5.0 + (processed * 80.0 / totalEntries);
                progress?.Report((pct, $"Extracting ({processed}/{totalEntries}): {entry.Name}"));
            }
        }

        string appExePath = Path.Combine(installDir, "IgnisDisc.exe");
        string uninstallerPath = Path.Combine(installDir, "Uninstall-IgnisDisc.exe");

        try
        {
            string? currentProcessPath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(currentProcessPath) &&
                File.Exists(currentProcessPath) &&
                !string.Equals(Path.GetFullPath(currentProcessPath), Path.GetFullPath(uninstallerPath), StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(currentProcessPath, uninstallerPath, overwrite: true);
            }
        }
        catch
        {
        }

        if (createDesktopShortcut)
        {
            progress?.Report((88, "Creating Desktop shortcut..."));
            string desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            CreateWindowsShortcut(
                Path.Combine(desktopDir, $"{AppTitle}.lnk"),
                appExePath,
                installDir,
                "IgnisDisc Optical Studio — Master CD, DVD, ISO/CUE & Red Book Audio Discs");
        }

        if (createStartMenuShortcut)
        {
            progress?.Report((92, "Creating Start Menu shortcut..."));
            string startMenuDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                AppTitle);
            Directory.CreateDirectory(startMenuDir);
            CreateWindowsShortcut(
                Path.Combine(startMenuDir, $"{AppTitle}.lnk"),
                appExePath,
                installDir,
                "IgnisDisc Optical Studio — Master CD, DVD, ISO/CUE & Red Book Audio Discs");
        }

        if (addToPath)
        {
            progress?.Report((95, "Adding ignisdisc-cli to User PATH..."));
            AddDirectoryToUserPath(installDir);
        }

        if (registerUninstaller)
        {
            progress?.Report((98, "Registering IgnisDisc with Windows Installed Apps..."));
            RegisterUninstallerInRegistry(installDir, appExePath, uninstallerPath);
        }

        progress?.Report((100, $"Installation complete! Installed to {installDir}"));
    }

    private static void CreateWindowsShortcut(string shortcutPath, string targetPath, string workingDir, string description)
    {
        try
        {
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return;
            dynamic? shell = Activator.CreateInstance(shellType);
            if (shell == null) return;
            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            shortcut.TargetPath = targetPath;
            shortcut.WorkingDirectory = workingDir;
            shortcut.Description = description;
            shortcut.IconLocation = $"{targetPath},0";
            shortcut.Save();
        }
        catch
        {
        }
    }

    private static void AddDirectoryToUserPath(string installDir)
    {
        try
        {
            string? currentPath = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User);
            var parts = (currentPath ?? string.Empty)
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();

            if (!parts.Any(p => string.Equals(p.TrimEnd('\\'), installDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
            {
                parts.Add(installDir);
                Environment.SetEnvironmentVariable("Path", string.Join(";", parts), EnvironmentVariableTarget.User);
            }
        }
        catch
        {
        }
    }

    private static void RegisterUninstallerInRegistry(string installDir, string appExePath, string uninstallerPath)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.CreateSubKey(UninstallRegKeyPath);
            if (key != null)
            {
                key.SetValue("DisplayName", AppTitle);
                key.SetValue("DisplayVersion", AppVersion);
                key.SetValue("Publisher", Publisher);
                key.SetValue("InstallLocation", installDir);
                key.SetValue("DisplayIcon", appExePath);
                key.SetValue("UninstallString", $"\"{uninstallerPath}\" --uninstall --install-dir \"{installDir}\"");
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }

            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(LegacyUninstallRegKeyPath, throwOnMissingSubKey: false);
            }
            catch { }
        }
        catch
        {
        }
    }

    private static int RunUninstall(string installDir, bool silent)
    {
        try
        {
            if (!silent)
            {
                var confirm = System.Windows.MessageBox.Show(
                    $"Are you sure you want to uninstall {AppTitle} from:\n{installDir}?",
                    $"{AppTitle} Uninstaller",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (confirm != MessageBoxResult.Yes)
                {
                    return 0;
                }
            }

            string desktopLink = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), $"{AppTitle}.lnk");
            if (File.Exists(desktopLink)) File.Delete(desktopLink);

            string startMenuFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppTitle);
            if (Directory.Exists(startMenuFolder)) Directory.Delete(startMenuFolder, recursive: true);

            Registry.CurrentUser.DeleteSubKeyTree(UninstallRegKeyPath, throwOnMissingSubKey: false);

            if (Directory.Exists(installDir))
            {
                foreach (string file in Directory.GetFiles(installDir, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        if (!string.Equals(Path.GetFullPath(file), Path.GetFullPath(Environment.ProcessPath ?? string.Empty), StringComparison.OrdinalIgnoreCase))
                        {
                            File.Delete(file);
                        }
                    }
                    catch { }
                }
            }

            if (!silent)
            {
                System.Windows.MessageBox.Show(
                    $"{AppTitle} has been uninstalled.",
                    $"{AppTitle} Uninstaller",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }

            return 0;
        }
        catch (Exception ex)
        {
            if (!silent)
            {
                System.Windows.MessageBox.Show(
                    $"Error during uninstall: {ex.Message}",
                    $"{AppTitle} Uninstaller",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            return 1;
        }
    }
}

internal sealed class SetupWizardWindow : Window
{
    private readonly System.Windows.Controls.TextBox _installDirBox;
    private readonly System.Windows.Controls.CheckBox _desktopShortcutCheck;
    private readonly System.Windows.Controls.CheckBox _startMenuShortcutCheck;
    private readonly System.Windows.Controls.CheckBox _addToPathCheck;
    private readonly System.Windows.Controls.ProgressBar _progressBar;
    private readonly TextBlock _statusText;
    private readonly System.Windows.Controls.Button _installButton;
    private readonly System.Windows.Controls.Button _launchButton;
    private readonly System.Windows.Controls.Button _closeButton;
    private readonly bool _isUpgrade;
    private bool _isInstalled;

    public int ExitCode { get; private set; }

    public SetupWizardWindow(string defaultInstallDir)
    {
        string? previousVer = Program.GetExistingInstallVersion();
        _isUpgrade = !string.IsNullOrEmpty(previousVer);

        Title = _isUpgrade
            ? $"{Program.AppTitle} — Upgrade to v{Program.AppVersion}"
            : $"{Program.AppTitle} v{Program.AppVersion} — Setup";
        Width = 640;
        Height = 470;
        MinWidth = 600;
        MinHeight = 450;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(18, 22, 31));
        Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(240, 244, 248));
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI Variable, Segoe UI, sans-serif");

        var rootGrid = new Grid { Margin = new Thickness(28) };
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Header
        var headerStack = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };
        headerStack.Children.Add(new TextBlock
        {
            Text = "💿 IgnisDisc Optical Studio",
            FontSize = 24,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(248, 250, 252))
        });
        headerStack.Children.Add(new TextBlock
        {
            Text = _isUpgrade
                ? $"Existing Version {previousVer} Detected • Ready to Upgrade to v{Program.AppVersion}"
                : $"Version {Program.AppVersion} • Self-Contained Windows 10/11 x64 Installer (No .NET Runtime Required)",
            FontSize = 12.5,
            Foreground = new SolidColorBrush(_isUpgrade ? System.Windows.Media.Color.FromRgb(56, 189, 248) : System.Windows.Media.Color.FromRgb(148, 163, 184)),
            Margin = new Thickness(0, 6, 0, 0)
        });
        Grid.SetRow(headerStack, 0);
        rootGrid.Children.Add(headerStack);

        // Body Card
        var cardBorder = new Border
        {
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 36, 51)),
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(51, 65, 85)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(20)
        };

        var bodyStack = new StackPanel();
        bodyStack.Children.Add(new TextBlock
        {
            Text = "Installation Folder",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(226, 232, 240)),
            Margin = new Thickness(0, 0, 0, 6)
        });

        var dirGrid = new Grid { Margin = new Thickness(0, 0, 0, 16) };
        dirGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        dirGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _installDirBox = new System.Windows.Controls.TextBox
        {
            Text = defaultInstallDir,
            Padding = new Thickness(10, 6, 10, 6),
            FontSize = 13,
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(15, 23, 42)),
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(248, 250, 252)),
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(71, 85, 105))
        };
        Grid.SetColumn(_installDirBox, 0);
        dirGrid.Children.Add(_installDirBox);

        var browseBtn = new System.Windows.Controls.Button
        {
            Content = "Browse...",
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(8, 0, 0, 0),
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(51, 65, 85)),
            Foreground = System.Windows.Media.Brushes.White,
            BorderThickness = new Thickness(0)
        };
        browseBtn.Click += (_, _) =>
        {
            using var dlg = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Select installation folder for IgnisDisc Optical Studio",
                SelectedPath = _installDirBox.Text,
                ShowNewFolderButton = true
            };
            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK && !string.IsNullOrWhiteSpace(dlg.SelectedPath))
            {
                _installDirBox.Text = dlg.SelectedPath;
            }
        };
        Grid.SetColumn(browseBtn, 1);
        dirGrid.Children.Add(browseBtn);
        bodyStack.Children.Add(dirGrid);

        _desktopShortcutCheck = new System.Windows.Controls.CheckBox
        {
            Content = "Create Desktop shortcut (IgnisDisc Optical Studio)",
            IsChecked = true,
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(226, 232, 240)),
            Margin = new Thickness(0, 4, 0, 6)
        };
        bodyStack.Children.Add(_desktopShortcutCheck);

        _startMenuShortcutCheck = new System.Windows.Controls.CheckBox
        {
            Content = "Create Start Menu program shortcut",
            IsChecked = true,
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(226, 232, 240)),
            Margin = new Thickness(0, 4, 0, 6)
        };
        bodyStack.Children.Add(_startMenuShortcutCheck);

        _addToPathCheck = new System.Windows.Controls.CheckBox
        {
            Content = "Add ignisdisc-cli.exe to User PATH for command-line automation",
            IsChecked = true,
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(226, 232, 240)),
            Margin = new Thickness(0, 4, 0, 18)
        };
        bodyStack.Children.Add(_addToPathCheck);

        _progressBar = new System.Windows.Controls.ProgressBar
        {
            Height = 12,
            Minimum = 0,
            Maximum = 100,
            Value = 0,
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(15, 23, 42)),
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(56, 189, 248)),
            Margin = new Thickness(0, 0, 0, 8)
        };
        bodyStack.Children.Add(_progressBar);

        _statusText = new TextBlock
        {
            Text = _isUpgrade
                ? $"Ready to upgrade to IgnisDisc v{Program.AppVersion}. User settings in AppData are safely preserved."
                : "Ready to install IgnisDisc Optical Studio (GUI) and ignisdisc-cli.",
            FontSize = 12,
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(148, 163, 184)),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        bodyStack.Children.Add(_statusText);

        cardBorder.Child = bodyStack;
        Grid.SetRow(cardBorder, 1);
        rootGrid.Children.Add(cardBorder);

        // Footer Buttons
        var footerStack = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(0, 20, 0, 0)
        };

        _installButton = new System.Windows.Controls.Button
        {
            Content = _isUpgrade ? "Upgrade Now" : "Install Now",
            Padding = new Thickness(22, 9, 22, 9),
            FontWeight = FontWeights.SemiBold,
            Background = new SolidColorBrush(_isUpgrade ? System.Windows.Media.Color.FromRgb(16, 185, 129) : System.Windows.Media.Color.FromRgb(2, 132, 199)),
            Foreground = System.Windows.Media.Brushes.White,
            BorderThickness = new Thickness(0),
            Margin = new Thickness(0, 0, 10, 0)
        };
        _installButton.Click += OnInstallClicked;
        footerStack.Children.Add(_installButton);

        _launchButton = new System.Windows.Controls.Button
        {
            Content = "🚀 Launch IgnisDisc Studio",
            Padding = new Thickness(18, 9, 18, 9),
            FontWeight = FontWeights.SemiBold,
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(16, 185, 129)),
            Foreground = System.Windows.Media.Brushes.White,
            BorderThickness = new Thickness(0),
            Margin = new Thickness(0, 0, 10, 0),
            Visibility = Visibility.Collapsed
        };
        _launchButton.Click += OnLaunchClicked;
        footerStack.Children.Add(_launchButton);

        _closeButton = new System.Windows.Controls.Button
        {
            Content = "Close",
            Padding = new Thickness(18, 9, 18, 9),
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(51, 65, 85)),
            Foreground = System.Windows.Media.Brushes.White,
            BorderThickness = new Thickness(0)
        };
        _closeButton.Click += (_, _) => Close();
        footerStack.Children.Add(_closeButton);

        Grid.SetRow(footerStack, 2);
        rootGrid.Children.Add(footerStack);

        Content = rootGrid;
    }

    private async void OnInstallClicked(object sender, RoutedEventArgs e)
    {
        if (_isInstalled) return;

        string targetDir = _installDirBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(targetDir))
        {
            System.Windows.MessageBox.Show("Please specify a valid installation folder.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _installButton.IsEnabled = false;
        _installDirBox.IsEnabled = false;
        _desktopShortcutCheck.IsEnabled = false;
        _startMenuShortcutCheck.IsEnabled = false;
        _addToPathCheck.IsEnabled = false;

        bool desktop = _desktopShortcutCheck.IsChecked == true;
        bool startMenu = _startMenuShortcutCheck.IsChecked == true;
        bool addPath = _addToPathCheck.IsChecked == true;

        var progress = new Progress<(double Percent, string Status)>(report =>
        {
            _progressBar.Value = report.Percent;
            _statusText.Text = report.Status;
        });

        try
        {
            await Task.Run(() => Program.InstallPayload(
                targetDir,
                desktop,
                startMenu,
                addPath,
                registerUninstaller: true,
                progress));

            _isInstalled = true;
            _installButton.Content = _isUpgrade ? "✓ Upgraded" : "✓ Installed";
            _statusText.Text = _isUpgrade
                ? $"IgnisDisc was successfully upgraded to v{Program.AppVersion}."
                : $"IgnisDisc v{Program.AppVersion} installed successfully.";
            _launchButton.Visibility = Visibility.Visible;
            ExitCode = 0;
        }
        catch (Exception ex)
        {
            ExitCode = 1;
            _statusText.Text = $"Installation failed: {ex.Message}";
            _installButton.IsEnabled = true;
            System.Windows.MessageBox.Show(
                $"Failed to install {Program.AppTitle}:\n\n{ex.Message}",
                Title,
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void OnLaunchClicked(object sender, RoutedEventArgs e)
    {
        string appExe = Path.Combine(_installDirBox.Text.Trim(), "IgnisDisc.exe");
        if (File.Exists(appExe))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = appExe,
                WorkingDirectory = Path.GetDirectoryName(appExe) ?? string.Empty,
                UseShellExecute = true
            });
            Close();
        }
    }
}
