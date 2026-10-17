using CdBurner.App.Mvvm;

namespace CdBurner.App.ViewModels;

/// <summary>
/// Represents a row in the DVD-Video physical layout inspector, displaying sector LBA extents,
/// file sizes, and 32 KB (16-sector) ECC alignment padding gaps between .IFO and .BUP files.
/// </summary>
public class DvdExtentAlignmentRow : ObservableObject
{
    private string _fileName = string.Empty;
    private int _startLba;
    private int _endLba;
    private int _sectorCount;
    private long _sizeBytes;
    private bool _isPadding;
    private int _eccPaddingSectors;
    private string _status = string.Empty;

    /// <summary>
    /// File name or padding descriptor (e.g. "VIDEO_TS.IFO", "[PADDING_VIDEO_TS]", "VIDEO_TS.BUP").
    /// </summary>
    public string FileName
    {
        get => _fileName;
        set => SetProperty(ref _fileName, value);
    }

    /// <summary>
    /// Starting Logical Block Address on optical disc media.
    /// </summary>
    public int StartLba
    {
        get => _startLba;
        set => SetProperty(ref _startLba, value);
    }

    /// <summary>
    /// Ending LBA (exclusive).
    /// </summary>
    public int EndLba
    {
        get => _endLba;
        set => SetProperty(ref _endLba, value);
    }

    /// <summary>
    /// Total 2048-byte optical sectors occupied.
    /// </summary>
    public int SectorCount
    {
        get => _sectorCount;
        set => SetProperty(ref _sectorCount, value);
    }

    /// <summary>
    /// Payload size in bytes.
    /// </summary>
    public long SizeBytes
    {
        get => _sizeBytes;
        set
        {
            if (SetProperty(ref _sizeBytes, value))
            {
                OnPropertyChanged(nameof(DisplaySize));
            }
        }
    }

    /// <summary>
    /// Indicates whether this row represents a virtual 32 KB ECC gap padding sector extent.
    /// </summary>
    public bool IsPadding
    {
        get => _isPadding;
        set
        {
            if (SetProperty(ref _isPadding, value))
            {
                OnPropertyChanged(nameof(DisplaySize));
                OnPropertyChanged(nameof(IconGlyph));
            }
        }
    }

    /// <summary>
    /// Number of ECC padding sectors inserted for 32 KB boundary alignment.
    /// </summary>
    public int EccPaddingSectors
    {
        get => _eccPaddingSectors;
        set => SetProperty(ref _eccPaddingSectors, value);
    }

    /// <summary>
    /// Status description or validation classification.
    /// </summary>
    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    /// <summary>
    /// Formatted display size string.
    /// </summary>
    public string DisplaySize => IsPadding
        ? $"{SectorCount * 2048:N0} B ({SectorCount} sectors)"
        : CompilationItemViewModel.FormatBytes(SizeBytes);

    /// <summary>
    /// Icon representation for layout inspection table.
    /// </summary>
    public string IconGlyph => IsPadding ? "🛡️" : (FileName.EndsWith(".VOB") ? "🎞️" : "📋");
}
