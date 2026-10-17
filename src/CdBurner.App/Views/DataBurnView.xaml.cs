using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CdBurner.App.ViewModels;

namespace CdBurner.App.Views;

/// <summary>
/// Interaction logic for DataBurnView.xaml.
/// </summary>
public partial class DataBurnView : UserControl
{
    public DataBurnView()
    {
        InitializeComponent();
    }

    private void OnDataGridDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is DataBurnViewModel vm && vm.SelectedItem is { IsDirectory: true } folder)
        {
            vm.NavigateToFolder(folder.VirtualPath);
        }
    }

    private void OnFileDrop(object sender, DragEventArgs e)
    {
        if (DataContext is not DataBurnViewModel vm) return;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

        if (e.Data.GetData(DataFormats.FileDrop) is string[] droppedPaths)
        {
            foreach (var path in droppedPaths)
            {
                if (Directory.Exists(path))
                {
                    vm.AddFolder(path);
                }
                else if (File.Exists(path))
                {
                    vm.AddFile(path);
                }
            }
        }
    }
}
