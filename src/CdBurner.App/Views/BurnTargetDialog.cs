using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Devices;

namespace CdBurner.App.Views;

/// <summary>
/// Modal confirmation dialog that prompts the user to select and verify the target optical drive
/// (and inspect its live media status) every time a Burn action is initiated in the GUI.
/// </summary>
public sealed class BurnTargetDialog : Window
{
    private readonly IDiscDeviceService _deviceService;
    private readonly ComboBox _driveCombo;
    private readonly TextBlock _driveBadgeText;
    private readonly Border _driveBadgeBorder;
    private readonly TextBlock _mediaStatusText;
    private readonly TextBlock _warningText;
    private readonly Border _warningBorder;
    private readonly Button _burnButton;

    public IDiscRecorderBackend? ConfirmedRecorder { get; private set; }

    public BurnTargetDialog(
        IDiscDeviceService deviceService,
        IEnumerable<IDiscRecorderBackend> recorders,
        IDiscRecorderBackend? currentSelection,
        string burnOperationTitle)
    {
        _deviceService = deviceService;

        Title = $"Confirm Target Drive — {burnOperationTitle}";
        Width = 540;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = Owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;

        Background = TryFindBrush("SurfaceBackgroundBrush", Color.FromRgb(18, 18, 24));
        Foreground = TryFindBrush("TextPrimaryBrush", Colors.White);

        var rootBorder = new Border
        {
            Padding = new Thickness(22),
            Background = Background
        };

        var mainStack = new StackPanel();

        // Title header
        mainStack.Children.Add(new TextBlock
        {
            Text = $"🔥 {burnOperationTitle}",
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            Foreground = TryFindBrush("TextPrimaryBrush", Colors.White),
            Margin = new Thickness(0, 0, 0, 4)
        });

        mainStack.Children.Add(new TextBlock
        {
            Text = "Verify and confirm which optical drive you want to burn to:",
            FontSize = 13,
            Foreground = TryFindBrush("TextSecondaryBrush", Color.FromRgb(176, 184, 196)),
            Margin = new Thickness(0, 0, 0, 14)
        });

        // Drive selector row
        var selectorLabel = new TextBlock
        {
            Text = "Burn To Optical Drive:",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = TryFindBrush("TextSecondaryBrush", Color.FromRgb(176, 184, 196)),
            Margin = new Thickness(0, 0, 0, 6)
        };
        mainStack.Children.Add(selectorLabel);

        var driveRowGrid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        driveRowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        driveRowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        driveRowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _driveCombo = new ComboBox
        {
            Height = 36,
            VerticalContentAlignment = VerticalAlignment.Center,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Padding = new Thickness(10, 4, 10, 4)
        };
        _driveCombo.SelectionChanged += (_, _) => UpdateSelectedDriveDetails();
        Grid.SetColumn(_driveCombo, 0);
        driveRowGrid.Children.Add(_driveCombo);

        var refreshBtn = new Button
        {
            Content = "↻ Refresh",
            Height = 36,
            Padding = new Thickness(12, 0, 12, 0),
            Margin = new Thickness(8, 0, 0, 0),
            ToolTip = "Re-scan optical drives and inserted disc status"
        };
        refreshBtn.Click += (_, _) => ReloadDrives( SelectedDrive );
        Grid.SetColumn(refreshBtn, 1);
        driveRowGrid.Children.Add(refreshBtn);

        var ejectBtn = new Button
        {
            Content = "⏏ Eject",
            Height = 36,
            Padding = new Thickness(12, 0, 12, 0),
            Margin = new Thickness(6, 0, 0, 0),
            ToolTip = "Eject the selected drive tray"
        };
        ejectBtn.Click += (_, _) =>
        {
            try
            {
                SelectedDrive?.EjectMedia();
                UpdateSelectedDriveDetails();
            }
            catch { }
        };
        Grid.SetColumn(ejectBtn, 2);
        driveRowGrid.Children.Add(ejectBtn);

        mainStack.Children.Add(driveRowGrid);

        // Drive & Media Info Card
        var infoCard = new Border
        {
            Background = TryFindBrush("CardBackgroundBrush", Color.FromRgb(26, 27, 38)),
            BorderBrush = TryFindBrush("CardBorderBrush", Color.FromRgb(46, 50, 68)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 12, 14, 12),
            Margin = new Thickness(0, 0, 0, 12)
        };

        var infoStack = new StackPanel();

        _driveBadgeBorder = new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 3, 8, 3),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 8)
        };
        _driveBadgeText = new TextBlock
        {
            FontSize = 11,
            FontWeight = FontWeights.Bold
        };
        _driveBadgeBorder.Child = _driveBadgeText;
        infoStack.Children.Add(_driveBadgeBorder);

        _mediaStatusText = new TextBlock
        {
            FontSize = 13,
            Foreground = TryFindBrush("TextPrimaryBrush", Colors.White),
            TextWrapping = TextWrapping.Wrap
        };
        infoStack.Children.Add(_mediaStatusText);

        infoCard.Child = infoStack;
        mainStack.Children.Add(infoCard);

        // Warning banner (shown when Virtual Drive is selected or when Physical Drive has no blank disc)
        _warningBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(45, 245, 158, 11)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(245, 158, 11)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 0, 0, 16),
            Visibility = Visibility.Collapsed
        };
        _warningText = new TextBlock
        {
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(252, 211, 77)),
            TextWrapping = TextWrapping.Wrap
        };
        _warningBorder.Child = _warningText;
        mainStack.Children.Add(_warningBorder);

        // Action buttons row
        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var cancelBtn = new Button
        {
            Content = "Cancel",
            Width = 105,
            Height = 36,
            IsCancel = true,
            Margin = new Thickness(0, 0, 10, 0)
        };
        cancelBtn.Click += (_, _) =>
        {
            ConfirmedRecorder = null;
            DialogResult = false;
            Close();
        };
        buttonPanel.Children.Add(cancelBtn);

        _burnButton = new Button
        {
            Content = "🔥 Start Burn",
            Width = 155,
            Height = 36,
            IsDefault = true,
            FontWeight = FontWeights.Bold,
            Background = TryFindBrush("AccentBrush", Color.FromRgb(255, 107, 53)),
            Foreground = TryFindBrush("AccentForegroundBrush", Colors.White)
        };
        _burnButton.Click += (_, _) =>
        {
            if (SelectedDrive == null)
            {
                return;
            }
            ConfirmedRecorder = SelectedDrive;
            DialogResult = true;
            Close();
        };
        buttonPanel.Children.Add(_burnButton);

        mainStack.Children.Add(buttonPanel);
        rootBorder.Child = mainStack;
        Content = rootBorder;

        PopulateDrivesList(recorders, currentSelection);
    }

    private IDiscRecorderBackend? SelectedDrive => (_driveCombo.SelectedItem as DriveComboItem)?.Recorder;

    private void ReloadDrives(IDiscRecorderBackend? preferred)
    {
        if (_deviceService is DiscDeviceManager ddm)
        {
            try { ddm.RefreshRecorders(); } catch { }
        }
        PopulateDrivesList(_deviceService.GetRecorders(), preferred);
    }

    private void PopulateDrivesList(IEnumerable<IDiscRecorderBackend> recorders, IDiscRecorderBackend? preferred)
    {
        // Order physical drives first so real hardware is always top priority
        var ordered = recorders
            .OrderBy(r => r.IsVirtual ? 1 : 0)
            .ThenBy(r => r.VolumePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _driveCombo.Items.Clear();
        DriveComboItem? itemToSelect = null;

        foreach (var rec in ordered)
        {
            var item = new DriveComboItem(rec);
            _driveCombo.Items.Add(item);
            if (preferred != null && string.Equals(rec.UniqueId, preferred.UniqueId, StringComparison.OrdinalIgnoreCase))
            {
                itemToSelect = item;
            }
        }

        // If preferred was virtual or null, and a physical drive exists, pre-select the physical drive if preferred wasn't explicitly non-null
        if (itemToSelect == null)
        {
            itemToSelect = _driveCombo.Items.OfType<DriveComboItem>().FirstOrDefault(i => !i.Recorder.IsVirtual)
                           ?? _driveCombo.Items.OfType<DriveComboItem>().FirstOrDefault();
        }

        _driveCombo.SelectedItem = itemToSelect;
        UpdateSelectedDriveDetails();
    }

    private void UpdateSelectedDriveDetails()
    {
        var rec = SelectedDrive;
        if (rec == null)
        {
            _driveBadgeText.Text = "NO DRIVE SELECTED";
            _mediaStatusText.Text = "No optical drive is currently selected.";
            _warningBorder.Visibility = Visibility.Collapsed;
            _burnButton.IsEnabled = false;
            return;
        }

        MediaStatusDto status;
        try
        {
            status = rec.QueryMediaStatus();
        }
        catch
        {
            status = new MediaStatusDto { MediaPresent = false, MediaType = DiscMediaType.NoMedia };
        }

        if (rec.IsVirtual)
        {
            _driveBadgeBorder.Background = new SolidColorBrush(Color.FromArgb(50, 245, 158, 11));
            _driveBadgeText.Foreground = new SolidColorBrush(Color.FromRgb(252, 211, 77));
            _driveBadgeText.Text = "VIRTUAL SIMULATOR DRIVE";

            double mb = status.TotalCapacityBytes / (1024.0 * 1024.0);
            _mediaStatusText.Text = status.MediaPresent
                ? $"Virtual Media: {status.MediaType.GetDisplayName()} ({mb:F0} MB, {status.FreeSectors:N0} free sectors)"
                : "Virtual Tray: Blank virtual media will be mounted automatically.";

            _warningText.Text = "⚠ You have selected the Virtual Simulator Drive (V:). This will only simulate burning in memory and WILL NOT write to a physical CD/DVD. Select your physical drive above if you want to burn a real disc.";
            _warningBorder.Visibility = Visibility.Visible;
            _burnButton.IsEnabled = true;
        }
        else
        {
            _driveBadgeBorder.Background = new SolidColorBrush(Color.FromArgb(50, 16, 185, 129));
            _driveBadgeText.Foreground = new SolidColorBrush(Color.FromRgb(52, 211, 153));
            _driveBadgeText.Text = $"PHYSICAL OPTICAL DRIVE ({rec.VolumePath})";

            if (status.MediaPresent && status.MediaType != DiscMediaType.NoMedia)
            {
                double mb = status.TotalCapacityBytes / (1024.0 * 1024.0);
                string stateDesc = status.IsBlank
                    ? "Blank / Ready to Burn"
                    : (status.IsAppendable
                        ? "Appendable"
                        : (status.RequiresEraseToWrite
                            ? "Finalized (Erase Required)"
                            : (status.IsRewritable ? "Rewritable" : "Non-Blank")));
                _mediaStatusText.Text = $"Inserted Disc: {status.MediaType.GetDisplayName()} — {stateDesc}\nCapacity: {mb:F1} MB ({status.FreeSectors:N0} free sectors)";

                if (status.RequiresEraseToWrite)
                {
                    _warningText.Text = $"⚠ The {status.MediaType.GetDisplayName()} in {rec.VolumePath} already has finalized data on it and must be erased before burning again (Drive Utilities -> Erase).";
                    _warningBorder.Visibility = Visibility.Visible;
                    _burnButton.IsEnabled = false;
                }
                else if (status.HasDataThatBurningReplaces)
                {
                    _warningText.Text = $"⚠ The {status.MediaType.GetDisplayName()} in {rec.VolumePath} already has files on it. Burning will REPLACE everything on the disc. Click Burn only if you no longer need those files.";
                    _warningBorder.Visibility = Visibility.Visible;
                    _burnButton.IsEnabled = true;
                }
                else if (!status.IsBlank && !status.IsAppendable && !status.IsRewritable)
                {
                    _warningText.Text = $"⚠ The disc in {rec.VolumePath} does not appear to be blank. Please insert a blank writable disc and click Refresh.";
                    _warningBorder.Visibility = Visibility.Visible;
                    _burnButton.IsEnabled = false;
                }
                else
                {
                    _warningBorder.Visibility = Visibility.Collapsed;
                    _burnButton.IsEnabled = true;
                }
            }
            else
            {
                _mediaStatusText.Text = $"Tray Status: Empty or spinning up ({rec.VendorId} {rec.ProductId})";
                _warningText.Text = $"⚠ No writable disc detected in {rec.VolumePath}. Please insert a blank CD/DVD into {rec.VolumePath} and click '↻ Refresh'.";
                _warningBorder.Visibility = Visibility.Visible;
                _burnButton.IsEnabled = false;
            }
        }
    }

    private static Brush TryFindBrush(string key, Color fallback)
    {
        if (Application.Current?.TryFindResource(key) is Brush b)
        {
            return b;
        }
        return new SolidColorBrush(fallback);
    }

    /// <summary>
    /// Returns true when the interactive WPF main window is visible on screen (false during headless automated tests).
    /// </summary>
    public static bool IsInteractiveSession =>
        Application.Current?.Dispatcher?.CheckAccess() == true &&
        Application.Current?.MainWindow is { IsLoaded: true, IsVisible: true };

    /// <summary>
    /// Prompts the user to select/confirm the target optical recorder when running in an interactive GUI session.
    /// </summary>
    public static IDiscRecorderBackend? ConfirmRecorderSelection(
        IDiscDeviceService deviceService,
        IEnumerable<IDiscRecorderBackend> availableRecorders,
        IDiscRecorderBackend? currentSelection,
        string burnOperationTitle)
    {
        var list = availableRecorders?.ToList() ?? deviceService.GetRecorders().ToList();
        if (list.Count == 0)
        {
            list = deviceService.GetRecorders().ToList();
        }

        var dlg = new BurnTargetDialog(deviceService, list, currentSelection, burnOperationTitle);
        if (Application.Current?.MainWindow is { IsVisible: true } mainWin)
        {
            dlg.Owner = mainWin;
            dlg.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        return dlg.ShowDialog() == true ? dlg.ConfirmedRecorder : null;
    }

    /// <summary>
    /// For screens that burn without showing this dialog: if the disc in <paramref name="recorder"/> is a used
    /// rewritable disc whose files a burn would replace, asks the user first. Returns false only when the user declined.
    /// </summary>
    public static bool ConfirmReplaceExistingData(IDiscRecorderBackend recorder, out bool replaceAllowed)
    {
        replaceAllowed = false;
        MediaStatusDto status;
        try
        {
            status = recorder.QueryMediaStatus();
        }
        catch
        {
            return true;
        }

        if (!status.HasDataThatBurningReplaces || !IsInteractiveSession)
        {
            return true;
        }

        var answer = MessageBox.Show(
            Application.Current.MainWindow,
            $"The {status.MediaType.GetDisplayName()} in {recorder.VolumePath} already has files on it.\n\n" +
            "Burning will replace everything on the disc. Continue?",
            "Replace the files on this disc?",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);

        replaceAllowed = answer == MessageBoxResult.OK;
        return replaceAllowed;
    }

    private sealed class DriveComboItem
    {
        public IDiscRecorderBackend Recorder { get; }

        public DriveComboItem(IDiscRecorderBackend recorder)
        {
            Recorder = recorder;
        }

        public override string ToString()
        {
            string tag = Recorder.IsVirtual ? "[Virtual Simulator]" : "[Physical Drive]";
            return $"{Recorder.VolumePath}  {Recorder.VendorId} {Recorder.ProductId}  {tag}".Trim();
        }
    }
}
