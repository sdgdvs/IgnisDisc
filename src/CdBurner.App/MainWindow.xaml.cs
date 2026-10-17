using System.Windows;
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
    }
}
