using System.Collections.ObjectModel;
using CdBurner.App.Mvvm;
using CdBurner.Core.Abstractions;

namespace CdBurner.App.ViewModels;

/// <summary>
/// View model managing real-time burning progress, speed telemetry, buffer gauges,
/// sector LBA progress, elapsed/remaining time, live expandable diagnostic logs, and safe cancellation.
/// </summary>
public class BurnProgressViewModel : ObservableObject
{
    private string _jobTitle = "Optical Mastering Operation";
    private string _currentActionText = "Ready";
    private double _percentComplete;
    private int _currentLba;
    private int _totalSectors;
    private string? _lbaText;
    private string _currentSpeedText = "0.0x";
    private double _bufferFillPercent;
    private string _elapsedTimeText = "00:00";
    private string _remainingTimeText = "--:--";
    private bool _isRunning;
    private bool _canCancel = true;
    private string? _lastActionDescription;
    private CancellationTokenSource? _cancellationTokenSource;

    /// <summary>
    /// User-facing descriptive title of the running mastering or blanking operation.
    /// </summary>
    public string JobTitle
    {
        get => _jobTitle;
        set => SetProperty(ref _jobTitle, value);
    }

    /// <summary>
    /// Description of the current action in progress (e.g. "Writing Track 1...", "Flushing Cache...").
    /// </summary>
    public string CurrentActionText
    {
        get => _currentActionText;
        set => SetProperty(ref _currentActionText, value);
    }

    /// <summary>
    /// Overall progress percentage (0.0 to 100.0).
    /// </summary>
    public double PercentComplete
    {
        get => _percentComplete;
        set
        {
            if (SetProperty(ref _percentComplete, value))
            {
                OnPropertyChanged(nameof(PercentCompleteText));
            }
        }
    }

    /// <summary>
    /// Formatted progress percent text for UI binding.
    /// </summary>
    public string PercentCompleteText => $"{PercentComplete:F1}%";

    /// <summary>
    /// Current logical block address (LBA) being read or written.
    /// </summary>
    public int CurrentLba
    {
        get => _currentLba;
        set
        {
            if (SetProperty(ref _currentLba, value))
            {
                OnPropertyChanged(nameof(LbaText));
            }
        }
    }

    /// <summary>
    /// Total number of logical sectors scheduled for this operation.
    /// </summary>
    public int TotalSectors
    {
        get => _totalSectors;
        set
        {
            if (SetProperty(ref _totalSectors, value))
            {
                OnPropertyChanged(nameof(LbaText));
            }
        }
    }

    /// <summary>
    /// Formatted sector progress string (e.g. "12,450 / 350,000").
    /// </summary>
    public string LbaText
    {
        get => _lbaText ?? $"{CurrentLba:N0} / {TotalSectors:N0}";
        set => SetProperty(ref _lbaText, value);
    }

    /// <summary>
    /// Formatted read/write throughput speed string (e.g. "8.2x (11.3 MB/s)").
    /// </summary>
    public string CurrentSpeedText
    {
        get => _currentSpeedText;
        set => SetProperty(ref _currentSpeedText, value);
    }

    /// <summary>
    /// Hardware/system buffer fill level percentage (0.0 to 100.0).
    /// </summary>
    public double BufferFillPercent
    {
        get => _bufferFillPercent;
        set
        {
            if (SetProperty(ref _bufferFillPercent, value))
            {
                OnPropertyChanged(nameof(BufferPercentText));
            }
        }
    }

    /// <summary>
    /// Formatted buffer fill text for display.
    /// </summary>
    public string BufferPercentText => $"{BufferFillPercent:F0}%";

    /// <summary>
    /// Formatted string representing elapsed time since operation start.
    /// </summary>
    public string ElapsedTimeText
    {
        get => _elapsedTimeText;
        set => SetProperty(ref _elapsedTimeText, value);
    }

    /// <summary>
    /// Formatted string representing estimated remaining time to completion.
    /// </summary>
    public string RemainingTimeText
    {
        get => _remainingTimeText;
        set => SetProperty(ref _remainingTimeText, value);
    }

    /// <summary>
    /// Indicates whether the burning or scanning task is actively executing.
    /// </summary>
    public bool IsRunning
    {
        get => _isRunning;
        set => SetProperty(ref _isRunning, value);
    }

    /// <summary>
    /// Indicates whether cancellation can be safely requested.
    /// </summary>
    public bool CanCancel
    {
        get => _canCancel;
        set
        {
            if (SetProperty(ref _canCancel, value))
            {
                CancelBurnCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// Diagnostic telemetry log entries emitted throughout the operation lifecycle.
    /// </summary>
    public ObservableCollection<string> LiveLogLines { get; } = new();

    /// <summary>
    /// Active cancellation token source associated with the operation.
    /// </summary>
    public CancellationTokenSource? CancellationTokenSource
    {
        get => _cancellationTokenSource;
        set => _cancellationTokenSource = value;
    }

    /// <summary>
    /// Command to abort and safely cancel the ongoing burn or blanking operation.
    /// </summary>
    public RelayCommand CancelBurnCommand { get; }

    /// <summary>
    /// Initializes a new instance of <see cref="BurnProgressViewModel"/>.
    /// </summary>
    public BurnProgressViewModel()
    {
        CancelBurnCommand = new RelayCommand(
            execute: Cancel,
            canExecute: () => CanCancel && IsRunning);
    }

    /// <summary>
    /// Requests cancellation of the running operation.
    /// </summary>
    public void Cancel()
    {
        if (CanCancel)
        {
            CanCancel = false;
            CurrentActionText = "Cancellation requested...";
            LiveLogLines.Add("Burn cancellation requested.");
            _cancellationTokenSource?.Cancel();
        }
    }

    /// <summary>
    /// Resets progress metrics and prepares the view model for a new mastering session.
    /// </summary>
    /// <param name="jobTitle">Job title to display.</param>
    /// <param name="cts">Optional cancellation token source to bind.</param>
    public void Reset(string jobTitle = "Optical Mastering Operation", CancellationTokenSource? cts = null)
    {
        JobTitle = jobTitle;
        CancellationTokenSource = cts;
        PercentComplete = 0.0;
        CurrentLba = 0;
        TotalSectors = 0;
        LbaText = "0 / 0";
        CurrentSpeedText = "0.0x";
        BufferFillPercent = 0.0;
        ElapsedTimeText = "00:00";
        RemainingTimeText = "--:--";
        CurrentActionText = "Starting...";
        IsRunning = true;
        CanCancel = true;
        _lastActionDescription = null;
        LiveLogLines.Clear();
        LiveLogLines.Add($"[{DateTime.Now:HH:mm:ss}] Job started: {jobTitle}");
    }

    /// <summary>
    /// Updates real-time telemetry from an engine progress snapshot.
    /// </summary>
    /// <param name="snapshot">The latest progress snapshot emitted by the burner backend.</param>
    public void ApplySnapshot(BurnProgressSnapshot snapshot)
    {
        if (snapshot == null) return;

        CurrentLba = snapshot.CurrentLba;
        TotalSectors = snapshot.TotalLba;
        PercentComplete = Math.Clamp(snapshot.ProgressPercentage, 0.0, 100.0);
        BufferFillPercent = Math.Clamp(snapshot.BufferLevelPercent, 0.0, 100.0);
        LbaText = $"{CurrentLba:N0} / {TotalSectors:N0}";

        if (snapshot.CurrentBytesPerSecond > 0)
        {
            double kb = snapshot.CurrentBytesPerSecond / 1024.0;
            if (kb >= 1024.0)
            {
                CurrentSpeedText = $"{snapshot.CurrentSpeedMultiplier:F1}x ({(kb / 1024.0):F1} MB/s)";
            }
            else
            {
                CurrentSpeedText = $"{snapshot.CurrentSpeedMultiplier:F1}x ({kb:F0} KB/s)";
            }
        }
        else
        {
            CurrentSpeedText = $"{snapshot.CurrentSpeedMultiplier:F1}x";
        }

        ElapsedTimeText = snapshot.ElapsedTime >= TimeSpan.FromHours(1)
            ? snapshot.ElapsedTime.ToString(@"hh\:mm\:ss")
            : snapshot.ElapsedTime.ToString(@"mm\:ss");

        RemainingTimeText = snapshot.RemainingTime > TimeSpan.Zero
            ? (snapshot.RemainingTime >= TimeSpan.FromHours(1)
                ? snapshot.RemainingTime.ToString(@"hh\:mm\:ss")
                : snapshot.RemainingTime.ToString(@"mm\:ss"))
            : "--:--";

        if (!string.IsNullOrWhiteSpace(snapshot.CurrentActionDescription))
        {
            CurrentActionText = snapshot.CurrentActionDescription;

            if (!string.Equals(_lastActionDescription, snapshot.CurrentActionDescription, StringComparison.Ordinal))
            {
                _lastActionDescription = snapshot.CurrentActionDescription;
                LiveLogLines.Add(snapshot.CurrentActionDescription);
            }
        }
    }

    /// <summary>
    /// Concludes the session, updating final status and logging outcome.
    /// </summary>
    /// <param name="success">True if the operation succeeded; false if failed.</param>
    /// <param name="message">Optional completion message or error detail.</param>
    public void Complete(bool success, string? message = null)
    {
        IsRunning = false;
        CanCancel = false;
        if (success)
        {
            PercentComplete = 100.0;
            CurrentActionText = message ?? "Operation completed successfully.";
            LiveLogLines.Add($"[{DateTime.Now:HH:mm:ss}] SUCCESS: {CurrentActionText}");
        }
        else
        {
            CurrentActionText = message ?? "Operation failed.";
            LiveLogLines.Add($"[{DateTime.Now:HH:mm:ss}] FAILED: {CurrentActionText}");
        }
    }
}
