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
        var services = new ServiceCollection();
        ConfigureServices(services);
        Services = services.BuildServiceProvider();
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        // Core & Hardware device services
        services.AddSingleton<IDiscDeviceService>(sp =>
            new DiscDeviceManager(enablePhysicalEnumeration: OperatingSystem.IsWindows()));

        // Optical burn engine backend
        services.AddSingleton<IBurnEngineBackend, CompositeBurnEngine>();

        // ViewModels
        services.AddSingleton<MainViewModel>();

        // Windows
        services.AddTransient<MainWindow>();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var mainWindow = Services.GetRequiredService<MainWindow>();
        mainWindow.Show();
    }
}
