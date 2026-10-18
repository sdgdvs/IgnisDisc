using System.Windows;
using CdBurner.App.Themes;
using CdBurner.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace CdBurner.App;

/// <summary>
/// Interaction logic for MainWindow.xaml hosting the Windows 11 Fluent 3-zone optical studio shell.
/// </summary>
public partial class MainWindow : Window
{
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
        Closing += OnMainWindowClosing;
        Loaded += (s, e) => ThemeManager.UpdateWindow(this);
    }

    private void OnMainWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (DataContext is MainViewModel mainVm && mainVm.IsAnyBurnActive)
        {
            var res = MessageBox.Show(
                "An optical disc operation is currently in progress.\n\nAre you sure you want to abort the burn and exit? The drive motor will be stopped immediately.",
                "IgnisDisc Optical Studio",
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
        base.OnClosed(e);
        if (DataContext is MainViewModel mainVm)
        {
            mainVm.CancelAllActiveOperations();
            mainVm.Dispose();
        }
    }
}
