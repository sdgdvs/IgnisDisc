using System.Windows;
using CdBurner.App.ViewModels;
using CdBurner.Core.Abstractions;
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

        var mainWindow = Services.GetRequiredService<MainWindow>();
        MainWindow = mainWindow;
        mainWindow.Show();
        mainWindow.Activate();

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
                catch
                {
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
        }
        catch
        {
        }

        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            System.IO.File.WriteAllText(
                System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "startup_error.log"),
                e.Exception.ToString());
        }
        catch { }

        MessageBox.Show(
            $"An unexpected error occurred:\n\n{e.Exception.Message}",
            "IgnisDisc Optical Studio",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }
}
