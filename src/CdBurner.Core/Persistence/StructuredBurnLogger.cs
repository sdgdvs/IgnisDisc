using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CdBurner.Core.Persistence;

/// <summary>
/// Diagnostic severity levels for optical burn and rip operations.
/// </summary>
public enum BurnLogLevel
{
    Info,
    Warn,
    Error,
    Debug
}

/// <summary>
/// Structured diagnostic log event recording optical session telemetry, SCSI diagnostics, and burn status.
/// </summary>
public sealed record BurnLogEntry
{
    /// <summary>
    /// Event timestamp in UTC.
    /// </summary>
    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Log severity level.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public BurnLogLevel Level { get; init; } = BurnLogLevel.Info;

    /// <summary>
    /// Primary diagnostic message.
    /// </summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>
    /// Functional subsystem category (e.g. "BurnEngine", "ScsiPassThrough", "Verification", "Ripper").
    /// </summary>
    public string? Category { get; init; }

    /// <summary>
    /// Target Logical Block Address (LBA) associated with this event, if applicable.
    /// </summary>
    public int? Lba { get; init; }

    /// <summary>
    /// Optional exception details, stack trace, or error message.
    /// </summary>
    public string? ExceptionDetail { get; init; }

    /// <summary>
    /// Arbitrary key-value metadata dictionary for structured telemetry.
    /// </summary>
    public IReadOnlyDictionary<string, object?>? Data { get; init; }

    /// <summary>
    /// Standard uppercase level identifier string ("INFO", "WARN", "ERROR", "DEBUG").
    /// </summary>
    public string LevelString => Level switch
    {
        BurnLogLevel.Info => "INFO",
        BurnLogLevel.Warn => "WARN",
        BurnLogLevel.Error => "ERROR",
        BurnLogLevel.Debug => "DEBUG",
        _ => Level.ToString().ToUpperInvariant()
    };

    /// <summary>
    /// Formats this entry into standard human-readable diagnostic text:
    /// [yyyy-MM-dd HH:mm:ss.fff] [LEVEL] [Category] [LBA: 12345] Message
    /// </summary>
    public string ToFormattedText()
    {
        var sb = new StringBuilder();
        sb.Append($"[{TimestampUtc:yyyy-MM-dd HH:mm:ss.fff}] [{LevelString}]");

        if (!string.IsNullOrEmpty(Category))
        {
            sb.Append($" [{Category}]");
        }

        if (Lba.HasValue)
        {
            sb.Append($" [LBA: {Lba.Value}]");
        }

        sb.Append(' ').Append(Message);

        if (!string.IsNullOrEmpty(ExceptionDetail))
        {
            sb.AppendLine().Append("  Exception: ").Append(ExceptionDetail);
        }

        return sb.ToString();
    }

    public override string ToString() => ToFormattedText();
}

/// <summary>
/// Thread-safe structured diagnostic logger maintaining an in-memory fixed-capacity ring buffer,
/// optional real-time JSONL file streaming, and export to human-readable .log text files.
/// </summary>
public sealed class StructuredBurnLogger : IDisposable, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonlSerializerOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly object _syncLock = new();
    private readonly int _capacity;
    private readonly BurnLogEntry?[] _ringBuffer;
    private int _head;
    private int _count;

    private readonly string? _jsonlFilePath;
    private StreamWriter? _jsonlWriter;
    private bool _isDisposed;

    /// <summary>
    /// Event triggered whenever a new log entry is recorded.
    /// Invocations occur outside the internal logger lock.
    /// </summary>
    public event Action<BurnLogEntry>? EntryLogged;

    /// <summary>
    /// Capacity of the circular ring buffer.
    /// </summary>
    public int Capacity => _capacity;

    /// <summary>
    /// Current number of entries retained in the ring buffer.
    /// </summary>
    public int Count
    {
        get
        {
            lock (_syncLock)
            {
                return _count;
            }
        }
    }

    /// <summary>
    /// Destination JSONL file path, if file logging is active.
    /// </summary>
    public string? JsonlFilePath => _jsonlFilePath;

    /// <summary>
    /// Initializes a new structured burn logger.
    /// </summary>
    /// <param name="capacity">Maximum entries to hold in memory before overwriting oldest entries (default 1000).</param>
    /// <param name="jsonlFilePath">Optional destination file path for streaming JSONL log entries.</param>
    public StructuredBurnLogger(int capacity = 1000, string? jsonlFilePath = null)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be greater than zero.");
        }

        _capacity = capacity;
        _ringBuffer = new BurnLogEntry?[capacity];
        _head = 0;
        _count = 0;
        _jsonlFilePath = jsonlFilePath;

        if (!string.IsNullOrWhiteSpace(jsonlFilePath))
        {
            string? dir = Path.GetDirectoryName(jsonlFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var stream = new FileStream(jsonlFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            _jsonlWriter = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
        }
    }

    /// <summary>
    /// Records a structured diagnostic entry thread-safely into the ring buffer and JSONL stream.
    /// </summary>
    public void Log(BurnLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        Action<BurnLogEntry>? callback;

        lock (_syncLock)
        {
            if (_isDisposed)
            {
                return;
            }

            // Insert into circular ring buffer
            _ringBuffer[_head] = entry;
            _head = (_head + 1) % _capacity;
            if (_count < _capacity)
            {
                _count++;
            }

            // Stream to JSONL file if configured
            if (_jsonlWriter != null)
            {
                try
                {
                    string jsonLine = JsonSerializer.Serialize(entry, JsonlSerializerOptions);
                    _jsonlWriter.WriteLine(jsonLine);
                }
                catch
                {
                    // Swallowing file I/O exceptions on diagnostic log write to prevent caller crash
                }
            }

            callback = EntryLogged;
        }

        callback?.Invoke(entry);
    }

    /// <summary>
    /// Logs an informational message.
    /// </summary>
    public void LogInfo(string message, string? category = null, int? lba = null, IReadOnlyDictionary<string, object?>? data = null)
    {
        Log(new BurnLogEntry
        {
            TimestampUtc = DateTime.UtcNow,
            Level = BurnLogLevel.Info,
            Message = message,
            Category = category,
            Lba = lba,
            Data = data
        });
    }

    /// <summary>
    /// Logs a warning message.
    /// </summary>
    public void LogWarn(string message, string? category = null, int? lba = null, IReadOnlyDictionary<string, object?>? data = null)
    {
        Log(new BurnLogEntry
        {
            TimestampUtc = DateTime.UtcNow,
            Level = BurnLogLevel.Warn,
            Message = message,
            Category = category,
            Lba = lba,
            Data = data
        });
    }

    /// <summary>
    /// Logs an error message and optional exception.
    /// </summary>
    public void LogError(string message, Exception? ex = null, string? category = null, int? lba = null, IReadOnlyDictionary<string, object?>? data = null)
    {
        Log(new BurnLogEntry
        {
            TimestampUtc = DateTime.UtcNow,
            Level = BurnLogLevel.Error,
            Message = message,
            Category = category,
            Lba = lba,
            ExceptionDetail = ex?.ToString(),
            Data = data
        });
    }

    /// <summary>
    /// Logs a debug diagnostic message.
    /// </summary>
    public void LogDebug(string message, string? category = null, int? lba = null, IReadOnlyDictionary<string, object?>? data = null)
    {
        Log(new BurnLogEntry
        {
            TimestampUtc = DateTime.UtcNow,
            Level = BurnLogLevel.Debug,
            Message = message,
            Category = category,
            Lba = lba,
            Data = data
        });
    }

    /// <summary>
    /// Returns all entries currently stored in the ring buffer in chronological order (oldest to newest).
    /// </summary>
    public IReadOnlyList<BurnLogEntry> GetAllEntries()
    {
        lock (_syncLock)
        {
            var result = new List<BurnLogEntry>(_count);
            int start = (_count < _capacity) ? 0 : _head;

            for (int i = 0; i < _count; i++)
            {
                int index = (start + i) % _capacity;
                var item = _ringBuffer[index];
                if (item != null)
                {
                    result.Add(item);
                }
            }

            return result;
        }
    }

    /// <summary>
    /// Exports all entries currently in the ring buffer to a standard human-readable text .log file.
    /// </summary>
    public void ExportToLogFile(string destinationFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationFilePath);

        var entries = GetAllEntries();

        string? dir = Path.GetDirectoryName(destinationFilePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using var writer = new StreamWriter(destinationFilePath, append: false, Encoding.UTF8);
        foreach (var entry in entries)
        {
            writer.WriteLine(entry.ToFormattedText());
        }
    }

    /// <summary>
    /// Asynchronously exports all entries currently in the ring buffer to a standard human-readable text .log file.
    /// </summary>
    public async Task ExportToLogFileAsync(string destinationFilePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationFilePath);

        var entries = GetAllEntries();

        string? dir = Path.GetDirectoryName(destinationFilePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await using var writer = new StreamWriter(destinationFilePath, append: false, Encoding.UTF8);
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await writer.WriteLineAsync(entry.ToFormattedText()).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Clears all entries from the in-memory ring buffer.
    /// </summary>
    public void Clear()
    {
        lock (_syncLock)
        {
            Array.Clear(_ringBuffer, 0, _ringBuffer.Length);
            _head = 0;
            _count = 0;
        }
    }

    public void Dispose()
    {
        lock (_syncLock)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            _jsonlWriter?.Flush();
            _jsonlWriter?.Dispose();
            _jsonlWriter = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        StreamWriter? writer = null;

        lock (_syncLock)
        {
            if (!_isDisposed)
            {
                _isDisposed = true;
                writer = _jsonlWriter;
                _jsonlWriter = null;
            }
        }

        if (writer != null)
        {
            await writer.FlushAsync().ConfigureAwait(false);
            await writer.DisposeAsync().ConfigureAwait(false);
        }
    }
}
