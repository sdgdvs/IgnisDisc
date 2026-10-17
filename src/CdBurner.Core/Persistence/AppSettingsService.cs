using System.Text.Json;

namespace CdBurner.Core.Persistence;

/// <summary>
/// Application visual theme mode.
/// </summary>
public enum ThemeMode
{
    Dark,
    Light,
    System
}

/// <summary>
/// Persistent user configuration and defaults for burning, ripping, staging, and UI preferences.
/// </summary>
public class AppSettings
{
    /// <summary>
    /// Default optical burn speed multiplier (e.g. 8 for 8X; 0 indicates maximum speed).
    /// </summary>
    public int DefaultWriteSpeedMultiplier { get; set; } = 8;

    /// <summary>
    /// Whether to automatically verify written media sectors against source hash after burning.
    /// </summary>
    public bool VerifyAfterBurnByDefault { get; set; } = true;

    /// <summary>
    /// Whether to eject the optical drive tray after a burn operation completes.
    /// </summary>
    public bool EjectAfterBurnByDefault { get; set; } = false;

    /// <summary>
    /// Whether to enable buffer underrun protection (BURN-Proof / Burn-Free) by default.
    /// </summary>
    public bool EnableBurnFreeByDefault { get; set; } = true;

    /// <summary>
    /// Temporary directory path used for ISO staging, audio decoding, and DVD transcode scratch files.
    /// </summary>
    public string TempStagingDirectory { get; set; } = Path.Combine(Path.GetTempPath(), "CdBurner", "Staging");

    /// <summary>
    /// Visual theme selection (Dark, Light, or follow Windows System preference).
    /// </summary>
    public ThemeMode ThemeMode { get; set; } = ThemeMode.System;

    /// <summary>
    /// Full file path or command name for the FFmpeg transcoder executable used in DVD-Video authoring.
    /// </summary>
    public string FfmpegExecutablePath { get; set; } = "ffmpeg.exe";

    /// <summary>
    /// Whether to expose the software-defined virtual optical drive backend simulator.
    /// </summary>
    public bool EnableVirtualDrive { get; set; } = true;

    /// <summary>
    /// In-memory diagnostic ring buffer capacity for the structured burn logger.
    /// </summary>
    public int LogRingBufferCapacity { get; set; } = 1000;

    /// <summary>
    /// Creates a deep copy clone of the current settings.
    /// </summary>
    public AppSettings Clone()
    {
        return new AppSettings
        {
            DefaultWriteSpeedMultiplier = DefaultWriteSpeedMultiplier,
            VerifyAfterBurnByDefault = VerifyAfterBurnByDefault,
            EjectAfterBurnByDefault = EjectAfterBurnByDefault,
            EnableBurnFreeByDefault = EnableBurnFreeByDefault,
            TempStagingDirectory = TempStagingDirectory,
            ThemeMode = ThemeMode,
            FfmpegExecutablePath = FfmpegExecutablePath,
            EnableVirtualDrive = EnableVirtualDrive,
            LogRingBufferCapacity = LogRingBufferCapacity
        };
    }
}

/// <summary>
/// Service providing thread-safe persistence, loading, and retrieval of <see cref="AppSettings"/>.
/// </summary>
public class AppSettingsService
{
    private readonly object _lock = new();
    private readonly string _settingsFilePath;
    private AppSettings _currentSettings;

    /// <summary>
    /// Current in-memory configuration settings snapshot. Setting a new value updates the snapshot thread-safely.
    /// </summary>
    public AppSettings CurrentSettings
    {
        get
        {
            lock (_lock)
            {
                return _currentSettings.Clone();
            }
        }
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_lock)
            {
                _currentSettings = value.Clone();
            }
        }
    }

    /// <summary>
    /// Convenience property to read current configuration snapshot.
    /// </summary>
    public AppSettings Settings => CurrentSettings;

    /// <summary>
    /// Full file path on disk where settings are persisted.
    /// </summary>
    public string SettingsFilePath => _settingsFilePath;

    /// <summary>
    /// Initializes a new settings service instance.
    /// </summary>
    /// <param name="customSettingsFilePath">Optional custom path to the settings JSON file.</param>
    public AppSettingsService(string? customSettingsFilePath = null)
    {
        _settingsFilePath = customSettingsFilePath ?? GetDefaultSettingsFilePath();
        _currentSettings = new AppSettings();
    }

    /// <summary>
    /// Computes the default application settings file path under %APPDATA%\CdBurner\settings.json.
    /// </summary>
    public static string GetDefaultSettingsFilePath()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "CdBurner", "settings.json");
    }

    /// <summary>
    /// Loads settings from disk synchronously. If the file does not exist, defaults are retained.
    /// </summary>
    public void Load()
    {
        lock (_lock)
        {
            _currentSettings = LoadFromFile(_settingsFilePath);
        }
    }

    /// <summary>
    /// Loads settings from disk asynchronously. If the file does not exist, defaults are retained.
    /// </summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var settings = await LoadFromFileAsync(_settingsFilePath, cancellationToken).ConfigureAwait(false);
        lock (_lock)
        {
            _currentSettings = settings;
        }
    }

    /// <summary>
    /// Saves current settings to disk synchronously.
    /// </summary>
    public void Save()
    {
        AppSettings snapshot;
        lock (_lock)
        {
            snapshot = _currentSettings.Clone();
        }

        SaveToFile(snapshot, _settingsFilePath);
    }

    /// <summary>
    /// Saves current settings to disk asynchronously.
    /// </summary>
    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        AppSettings snapshot;
        lock (_lock)
        {
            snapshot = _currentSettings.Clone();
        }

        await SaveToFileAsync(snapshot, _settingsFilePath, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Resets all settings to default values.
    /// </summary>
    public void ResetToDefaults()
    {
        lock (_lock)
        {
            _currentSettings = new AppSettings();
        }
    }

    /// <summary>
    /// Synchronously reads an <see cref="AppSettings"/> instance from a JSON file.
    /// Returns default settings if the file does not exist or fails to parse.
    /// </summary>
    public static AppSettings LoadFromFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return new AppSettings();
        }

        try
        {
            string json = File.ReadAllText(filePath);
            return JsonSerializer.Deserialize<AppSettings>(json, ProjectSerializer.JsonOptions) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    /// <summary>
    /// Asynchronously reads an <see cref="AppSettings"/> instance from a JSON file.
    /// Returns default settings if the file does not exist or fails to parse.
    /// </summary>
    public static async Task<AppSettings> LoadFromFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return new AppSettings();
        }

        try
        {
            await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
            var settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, ProjectSerializer.JsonOptions, cancellationToken).ConfigureAwait(false);
            return settings ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    /// <summary>
    /// Synchronously writes an <see cref="AppSettings"/> instance to a JSON file.
    /// </summary>
    public static void SaveToFile(AppSettings settings, string filePath)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        string? dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string json = JsonSerializer.Serialize(settings, ProjectSerializer.JsonOptions);
        File.WriteAllText(filePath, json);
    }

    /// <summary>
    /// Asynchronously writes an <see cref="AppSettings"/> instance to a JSON file.
    /// </summary>
    public static async Task SaveToFileAsync(AppSettings settings, string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        string? dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
        await JsonSerializer.SerializeAsync(stream, settings, ProjectSerializer.JsonOptions, cancellationToken).ConfigureAwait(false);
    }
}
