using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using CdBurner.App.ViewModels;
using CdBurner.Core.Compilation;

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
