using System.Windows;
using System.Windows.Controls;
using CdBurner.App.ViewModels;

namespace CdBurner.App.Views;

/// <summary>
/// Interaction logic for AudioBurnView.xaml.
/// </summary>
public partial class AudioBurnView : UserControl
{
    public AudioBurnView()
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
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (DataContext is not AudioBurnViewModel vm)
        {
            return;
        }

        if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
            e.Data.GetData(DataFormats.FileDrop) is string[] droppedPaths)
        {
            vm.AddAudioFiles(droppedPaths);
        }
    }
}
