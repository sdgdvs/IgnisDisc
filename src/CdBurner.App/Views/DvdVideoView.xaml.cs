using System.IO;
using System.Windows;
using System.Windows.Controls;
using CdBurner.App.ViewModels;

namespace CdBurner.App.Views;

/// <summary>
/// Interaction logic for DvdVideoView.xaml.
/// </summary>
public partial class DvdVideoView : UserControl
{
    public DvdVideoView()
    {
        InitializeComponent();
    }

    private void OnPreviewDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
    }

    private void OnFileDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
            e.Data.GetData(DataFormats.FileDrop) is string[] files &&
            files.Length > 0 &&
            DataContext is DvdVideoViewModel vm)
        {
            if (files.Length == 1 && Directory.Exists(files[0]))
            {
                vm.VideoTsFolderPath = files[0];
            }
            else
            {
                vm.AddMovieFiles(files);
            }
            e.Handled = true;
        }
    }
}
