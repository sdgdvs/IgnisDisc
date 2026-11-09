using System.Windows;
using CdBurner.App.Themes;
using CdBurner.App.ViewModels;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Persistence;
using CdBurner.Hardware.Burning;
using CdBurner.Hardware.Devices;
using Microsoft.Extensions.DependencyInjection;

namespace CdBurner.App;

/// <summary>
/// Interaction logic for App.xaml with dependency injection hosting.
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// Global application service provider.
    /// </summary>
    public static IServiceProvider Services { get; private set; } = null!;

    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        Services = CreateServiceProvider();
    }

    public static ServiceProvider CreateServiceProvider()
    {
        var services = new ServiceCollection();
        ConfigureServices(services);
        return services.BuildServiceProvider();
    }

    public static void ConfigureServices(IServiceCollection services)
    {
        // Core & Hardware device services (defer physical IMAPI spin-up until after MainWindow is rendered so GUI opens instantaneously)
        services.AddSingleton<IDiscDeviceService>(_ =>
            new DiscDeviceManager(enablePhysicalEnumeration: false));

        // Optical burn engine backend (use factory delegate so DI does not treat optional IBurnEngineBackend parameters as circular dependencies)
        services.AddSingleton<IBurnEngineBackend>(_ => new CompositeBurnEngine());

        // ViewModels
        services.AddSingleton<MainViewModel>(sp => new MainViewModel(
            sp.GetRequiredService<IDiscDeviceService>(),
            sp.GetRequiredService<IBurnEngineBackend>()));

        // Windows
        services.AddTransient<MainWindow>(sp => new MainWindow(
            sp.GetRequiredService<MainViewModel>()));
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            var settingsService = new AppSettingsService();
            settingsService.Load();
            ThemeManager.Initialize(settingsService.CurrentSettings.ThemeMode);
        }
        catch (Exception ex)
        {
            LogStartupIssue("Theme/settings initialization failed", ex);
        }

        var mainVm = Services.GetRequiredService<MainViewModel>();
        try
        {
            mainVm.Settings.SettingsService.Load();
            mainVm.Settings.LoadSettings();
            mainVm.ApplySettingsToWorkspaces();
            mainVm.SelectDefaultWriteSpeed();
        }
        catch (Exception ex)
        {
            LogStartupIssue("Loading saved settings failed; defaults are in use", ex);
        }

        // "Shut down" after burning: Windows' own shutdown.exe with a 60-second countdown that Windows announces and
        // that `shutdown /a` cancels. Set only here, in the real app, so tests can never shut a machine down.
        CdBurner.Core.Burning.PostBurnActionExecutor.DefaultShutdownInitiator = () =>
        {
            using var _ = System.Diagnostics.Process.Start(CdBurner.Core.Burning.PostBurnActionExecutor.CreateShutdownStartInfo());
        };

        var mainWindow = Services.GetRequiredService<MainWindow>();
        MainWindow = mainWindow;
        mainWindow.Show();
        mainWindow.Activate();

        if (mainVm.Settings.CheckForUpdatesOnStartup)
        {
            _ = mainVm.Settings.CheckForUpdatesOnStartupAsync();
        }

        // Asynchronously enumerate any physical IMAPIv2 optical drives in the background after window is visible
        if (OperatingSystem.IsWindows())
        {
            _ = Task.Run(() =>
            {
                try
                {
                    if (Services.GetService<IDiscDeviceService>() is DiscDeviceManager ddm)
                    {
                        ddm.EnablePhysicalDiscovery(true);
                    }
                }
                catch (Exception ex)
                {
                    LogStartupIssue("Physical drive discovery failed", ex);
                }
            });
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            if (Services?.GetService<MainViewModel>() is MainViewModel mainVm)
            {
                mainVm.CancelAllActiveOperations();
                mainVm.Dispose();
            }

            if (Services?.GetService<IDiscDeviceService>() is IDisposable deviceDisposable)
            {
                deviceDisposable.Dispose();
            }
        }
        catch
        {
        }

        base.OnExit(e);
    }

    /// <summary>
    /// Appends a non-fatal startup problem to %LOCALAPPDATA%\IgnisDisc\Logs\startup.log so failures are diagnosable.
    /// </summary>
    internal static void LogStartupIssue(string context, Exception ex)
    {
        try
        {
            string logDir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IgnisDisc", "Logs");
            System.IO.Directory.CreateDirectory(logDir);
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(logDir, "startup.log"),
                $"[{DateTime.Now:O}] {context}: {ex}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never take the app down.
        }
    }

    private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            System.IO.File.WriteAllText(
                System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "startup_error.log"),
                e.Exception.ToString());
        }
        catch
        {
            try
            {
                string logDir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "IgnisDisc",
                    "Logs");
                System.IO.Directory.CreateDirectory(logDir);
                System.IO.File.WriteAllText(
                    System.IO.Path.Combine(logDir, "startup_error.log"),
                    e.Exception.ToString());
            }
            catch
            {
            }
        }

        MessageBox.Show(
            $"An unexpected error occurred:\n\n{e.Exception.Message}",
            "IgnisDisc Optical Studio",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }
}
