using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using CdBurner.App.ViewModels;
using CdBurner.Core.Compilation;
using CdBurner.Core.Utilities;

namespace CdBurner.App.Converters;

/// <summary>
/// Converts between <see cref="NavigationTab"/> enum values and boolean for navigation rail toggle/radio buttons.
/// </summary>
public class NavigationTabToBooleanConverter : IValueConverter
{
    public static readonly NavigationTabToBooleanConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is NavigationTab selectedTab && parameter != null)
        {
            if (parameter is NavigationTab targetTab)
            {
                return selectedTab == targetTab;
            }
            if (Enum.TryParse<NavigationTab>(parameter.ToString(), true, out var parsed))
            {
                return selectedTab == parsed;
            }
        }
        return false;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isChecked && isChecked && parameter != null)
        {
            if (parameter is NavigationTab targetTab)
            {
                return targetTab;
            }
            if (Enum.TryParse<NavigationTab>(parameter.ToString(), true, out var parsed))
            {
                return parsed;
            }
        }
        return Binding.DoNothing;
    }
}

/// <summary>
/// Converts a <see cref="CapacityStatus"/> enum into a Fluent Theme brush for capacity progress indicator.
/// </summary>
public class CapacityStatusToBrushConverter : IValueConverter
{
    public static readonly CapacityStatusToBrushConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var status = value is CapacityStatus s ? s : CapacityStatus.FitsComfortably;

        string brushKey = status switch
        {
            CapacityStatus.FitsComfortably => "CapacitySafeBrush",
            CapacityStatus.NearCapacityWarning => "CapacityWarningBrush",
            CapacityStatus.OverburnRequired => "CapacityOverburnBrush",
            CapacityStatus.ExceedsMediaCapacity => "CapacityOverburnBrush",
            _ => "CapacitySafeBrush"
        };

        if (Application.Current?.Resources.Contains(brushKey) == true)
        {
            return (Brush)Application.Current.Resources[brushKey];
        }

        // Fallback brushes if Application.Current is null during testing
        return status switch
        {
            CapacityStatus.FitsComfortably => new SolidColorBrush(Color.FromRgb(0x10, 0x7C, 0x41)),
            CapacityStatus.NearCapacityWarning => new SolidColorBrush(Color.FromRgb(0xFF, 0x8C, 0x00)),
            _ => new SolidColorBrush(Color.FromRgb(0xD1, 0x34, 0x38))
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// Converts media presence boolean to green (present) or gray (empty) indicator brush.
/// </summary>
public class MediaPresenceToBrushConverter : IValueConverter
{
    public static readonly MediaPresenceToBrushConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool present = value is bool b && b;
        if (present)
        {
            if (Application.Current?.Resources.Contains("CapacitySafeBrush") == true)
            {
                return (Brush)Application.Current.Resources["CapacitySafeBrush"];
            }
            return new SolidColorBrush(Color.FromRgb(0x10, 0x7C, 0x41));
        }

        if (Application.Current?.Resources.Contains("TextMutedBrush") == true)
        {
            return (Brush)Application.Current.Resources["TextMutedBrush"];
        }
        return new SolidColorBrush(Color.FromRgb(0x76, 0x76, 0x76));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// Converts boolean to <see cref="Visibility"/> with static instance helper.
/// </summary>
public class BooleanToVisibilityConverter : IValueConverter
{
    public static readonly BooleanToVisibilityConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool b = value is bool flag && flag;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is Visibility v && v == Visibility.Visible;
    }
}

/// <summary>
/// Inverts boolean to <see cref="Visibility"/> (true -> Collapsed, false -> Visible).
/// </summary>
public class InverseBooleanToVisibilityConverter : IValueConverter
{
    public static readonly InverseBooleanToVisibilityConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool b = value is bool flag && flag;
        return b ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is Visibility v && v != Visibility.Visible;
    }
}

/// <summary>
/// Converts null/non-null reference to <see cref="Visibility"/>.
/// Default: null -> Collapsed, non-null -> Visible.
/// Parameter "Inverse" or "HideWhenNotNull": null -> Visible, non-null -> Collapsed.
/// </summary>
public class NullToVisibilityConverter : IValueConverter
{
    public static readonly NullToVisibilityConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool isNull = value == null;
        bool invert = parameter is string paramStr && (paramStr.Equals("Inverse", StringComparison.OrdinalIgnoreCase) || paramStr.Equals("HideWhenNotNull", StringComparison.OrdinalIgnoreCase));

        if (invert)
        {
            return isNull ? Visibility.Visible : Visibility.Collapsed;
        }

        return isNull ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// Converts boolean state to green (success/valid) or red (error/invalid) brush.
/// </summary>
public class BooleanToBrushConverter : IValueConverter
{
    public static readonly BooleanToBrushConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool isValid = value is bool b && b;
        if (isValid)
        {
            return Application.Current?.Resources["CapacitySafeBrush"] as Brush
                   ?? new SolidColorBrush(Color.FromRgb(0x10, 0x7C, 0x41));
        }

        return Application.Current?.Resources["CapacityOverburnBrush"] as Brush
               ?? new SolidColorBrush(Color.FromRgb(0xD1, 0x34, 0x38));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// Formats a <see cref="FileSystemPreset"/> enum value into a readable display string.
/// </summary>
public class FileSystemPresetToStringConverter : IValueConverter
{
    public static readonly FileSystemPresetToStringConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is FileSystemPreset preset)
        {
            return preset switch
            {
                FileSystemPreset.IsoJolietUdf => "ISO 9660 + Joliet + UDF 1.02 (Universal)",
                FileSystemPreset.IsoOnlyLevel1 => "ISO 9660 Level 1 (Legacy DOS)",
                FileSystemPreset.UdfOnly201 => "UDF 2.01 (Modern Data)",
                FileSystemPreset.DvdVideoBridge => "DVD-Video Bridge (UDF 1.02 + ISO 9660)",
                _ => preset.ToString()
            };
        }
        return value?.ToString() ?? string.Empty;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// Converts between <see cref="CdBurner.Core.Audio.AudioRecordingMode"/> enum values and boolean for recording mode radio buttons.
/// </summary>
public class AudioRecordingModeToBooleanConverter : IValueConverter
{
    public static readonly AudioRecordingModeToBooleanConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is CdBurner.Core.Audio.AudioRecordingMode mode && parameter != null)
        {
            if (parameter is CdBurner.Core.Audio.AudioRecordingMode targetMode)
            {
                return mode == targetMode;
            }
            if (Enum.TryParse<CdBurner.Core.Audio.AudioRecordingMode>(parameter.ToString(), true, out var parsed))
            {
                return mode == parsed;
            }
        }
        return false;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isChecked && isChecked && parameter != null)
        {
            if (parameter is CdBurner.Core.Audio.AudioRecordingMode targetMode)
            {
                return targetMode;
            }
            if (Enum.TryParse<CdBurner.Core.Audio.AudioRecordingMode>(parameter.ToString(), true, out var parsed))
            {
                return parsed;
            }
        }
        return Binding.DoNothing;
    }
}

/// <summary>
/// Converts a <see cref="SectorHealthStatus"/> into an indicator color brush for surface scanner visualization.
/// </summary>
public class SectorHealthStatusToBrushConverter : IValueConverter
{
    public static readonly SectorHealthStatusToBrushConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var status = value is SectorHealthStatus s ? s : SectorHealthStatus.Good;
        return status switch
        {
            SectorHealthStatus.Good => Application.Current?.Resources["CapacitySafeBrush"] as Brush
                ?? new SolidColorBrush(Color.FromRgb(0x10, 0x7C, 0x41)),
            SectorHealthStatus.Slow => Application.Current?.Resources["CapacityWarningBrush"] as Brush
                ?? new SolidColorBrush(Color.FromRgb(0xFF, 0x8C, 0x00)),
            SectorHealthStatus.Damaged => Application.Current?.Resources["CapacityOverburnBrush"] as Brush
                ?? new SolidColorBrush(Color.FromRgb(0xD1, 0x34, 0x38)),
            _ => new SolidColorBrush(Color.FromRgb(0x76, 0x76, 0x76))
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}


