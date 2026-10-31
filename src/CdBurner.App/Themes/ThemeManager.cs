using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using CdBurner.Core.Persistence;
using ThemeMode = CdBurner.Core.Persistence.ThemeMode;

namespace CdBurner.App.Themes;

/// <summary>
/// Manages application-wide visual themes (Dark, Light, System) and applies runtime color changes
/// to dynamic resources and Windows desktop window frames.
/// </summary>
public static class ThemeManager
{
    private static ThemeMode _currentMode = ThemeMode.System;
    private static bool _initialized;

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    /// <summary>
    /// Gets the current theme mode setting.
    /// </summary>
    public static ThemeMode CurrentMode => _currentMode;

    /// <summary>
    /// Determines whether the currently applied theme is Light mode.
    /// </summary>
    public static bool CurrentIsLight => _currentMode switch
    {
        ThemeMode.Light => true,
        ThemeMode.Dark => false,
        _ => IsWindowsSystemLightTheme()
    };

    /// <summary>
    /// Initializes the theme manager with the user's configured theme mode and subscribes
    /// to Windows system theme preference changes.
    /// </summary>
    public static void Initialize(ThemeMode initialMode)
    {
        _currentMode = initialMode;

        if (!_initialized && OperatingSystem.IsWindows())
        {
            _initialized = true;
            try
            {
                Microsoft.Win32.SystemEvents.UserPreferenceChanged += (sender, args) =>
                {
                    if (_currentMode == ThemeMode.System &&
                        (args.Category == Microsoft.Win32.UserPreferenceCategory.General ||
                         args.Category == Microsoft.Win32.UserPreferenceCategory.VisualStyle))
                    {
                        try
                        {
                            Application.Current?.Dispatcher?.BeginInvoke(() => ApplyTheme(ThemeMode.System));
                        }
                        catch
                        {
                        }
                    }
                };
            }
            catch
            {
                // Fallback gracefully if registry/system events are restricted
            }
        }

        ApplyTheme(initialMode);
    }

    /// <summary>
    /// Switches the application palette at runtime between Dark and Light mode.
    /// </summary>
    public static void ApplyTheme(ThemeMode mode)
    {
        _currentMode = mode;
        bool isLight = mode switch
        {
            ThemeMode.Light => true,
            ThemeMode.Dark => false,
            _ => IsWindowsSystemLightTheme()
        };

        if (Application.Current == null || Application.Current.Dispatcher.HasShutdownStarted)
        {
            return;
        }

        if (!Application.Current.Dispatcher.CheckAccess())
        {
            try
            {
                Application.Current.Dispatcher.BeginInvoke(() => ApplyTheme(mode));
            }
            catch
            {
            }
            return;
        }

        try
        {
            var dictUri = new Uri(
                $"pack://application:,,,/IgnisDisc;component/Themes/{(isLight ? "LightColors" : "DarkColors")}.xaml",
                UriKind.Absolute);

            var newDict = new ResourceDictionary { Source = dictUri };

            // Update individual dynamic resource keys directly for instantaneous visual updates
            foreach (var key in newDict.Keys)
            {
                Application.Current.Resources[key] = newDict[key];
            }

            // Update any merged dictionary instances matching DarkColors / LightColors (including nested dictionaries)
            ReplaceThemeMergedDictionaries(Application.Current.Resources, newDict);

            // Update title bars of all open application windows
            try
            {
                foreach (Window window in Application.Current.Windows)
                {
                    UpdateWindow(window);
                }
            }
            catch
            {
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to apply visual theme: {ex.Message}");
        }
    }

    private static void ReplaceThemeMergedDictionaries(ResourceDictionary parentDict, ResourceDictionary newThemeDict)
    {
        for (int i = 0; i < parentDict.MergedDictionaries.Count; i++)
        {
            var md = parentDict.MergedDictionaries[i];
            if (md.Source != null &&
                (md.Source.OriginalString.Contains("DarkColors", StringComparison.OrdinalIgnoreCase) ||
                 md.Source.OriginalString.Contains("LightColors", StringComparison.OrdinalIgnoreCase)))
            {
                parentDict.MergedDictionaries[i] = newThemeDict;
            }
            else if (md.MergedDictionaries.Count > 0)
            {
                ReplaceThemeMergedDictionaries(md, newThemeDict);
                foreach (var key in newThemeDict.Keys)
                {
                    if (md.Contains(key))
                    {
                        md[key] = newThemeDict[key];
                    }
                }
            }
        }
    }

    /// <summary>
    /// Applies the immersive dark or light mode title bar attribute to a window.
    /// </summary>
    public static void UpdateWindow(Window window)
    {
        if (!OperatingSystem.IsWindows() || window == null)
        {
            return;
        }

        try
        {
            var helper = new WindowInteropHelper(window);
            var hwnd = helper.Handle;
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            bool isLight = CurrentIsLight;
            int useDarkMode = isLight ? 0 : 1;
            int hr = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDarkMode, sizeof(int));
            if (hr != 0)
            {
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref useDarkMode, sizeof(int));
            }
        }
        catch
        {
        }
    }

    /// <summary>
    /// Queries the Windows registry to determine if the operating system is set to light theme for apps.
    /// </summary>
    public static bool IsWindowsSystemLightTheme()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int val)
            {
                return val == 1;
            }
        }
        catch
        {
        }

        return false;
    }
}
