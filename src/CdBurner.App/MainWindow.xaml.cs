using CdBurner.App.Resources;
using System.Windows;
using System.Windows.Interop;
using CdBurner.App.Themes;
using CdBurner.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace CdBurner.App;

/// <summary>
/// Interaction logic for MainWindow.xaml hosting the Windows 11 Fluent 3-zone optical studio shell.
/// </summary>
public partial class MainWindow : Window
{
    private HwndSource? _hwndSource;

    /// <summary>
    /// Parameterless constructor resolving <see cref="MainViewModel"/> via DI or fallback instance.
    /// </summary>
    public MainWindow()
        : this(App.Services?.GetService<MainViewModel>() ?? new MainViewModel())
    {
    }

    /// <summary>
    /// Initializes a new <see cref="MainWindow"/> with the specified <see cref="MainViewModel"/>.
    /// </summary>
    /// <param name="viewModel">The view model backing the main window.</param>
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        SourceInitialized += OnMainWindowSourceInitialized;
        Closing += OnMainWindowClosing;
        Loaded += (s, e) =>
        {
            ThemeManager.UpdateWindow(this);
            viewModel.StartHardwareMediaMonitor();
        };
    }

    private void OnMainWindowSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != nint.Zero)
        {
            _hwndSource = HwndSource.FromHwnd(handle);
            _hwndSource?.AddHook(WndProc);
        }
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (DataContext is MainViewModel mainVm)
        {
            mainVm.ProcessWindowMessage(msg, wParam, lParam);
        }
        return nint.Zero;
    }

    private void OnMainWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (DataContext is MainViewModel mainVm && mainVm.IsAnyBurnActive)
        {
            var res = MessageBox.Show(
                Strings.DialogExitDuringBurnBody,
                Strings.DialogAppTitle,
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (res != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }

            mainVm.CancelAllActiveOperations();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_hwndSource != null)
        {
            _hwndSource.RemoveHook(WndProc);
            _hwndSource = null;
        }
        base.OnClosed(e);
        if (DataContext is MainViewModel mainVm)
        {
            mainVm.CancelAllActiveOperations();
            mainVm.Dispose();
        }
    }
}
