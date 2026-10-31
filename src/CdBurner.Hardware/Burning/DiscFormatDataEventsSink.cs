using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Interop;

namespace CdBurner.Hardware.Burning;

/// <summary>
/// Managed COM connection point event sink helper subscribing to Windows IMAPIv2
/// <see cref="DDiscFormat2DataEvents"/> (<c>IID_DDiscFormat2DataEvents</c>, DISPID 0x200)
/// and translating <see cref="IDiscFormat2DataEventArgs"/> into domain <see cref="BurnProgressSnapshot"/> instances.
/// </summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
public class DiscFormatDataEventsSink : DDiscFormat2DataEvents, IDisposable
{
    private IConnectionPoint? _connectionPoint;
    private int _cookie;
    private readonly IProgress<BurnProgressSnapshot>? _progress;
    private readonly Action<BurnProgressSnapshot>? _onSnapshot;
    private bool _disposed;

    /// <summary>
    /// Event raised when a new progress snapshot is received from IMAPIv2.
    /// </summary>
    public event EventHandler<BurnProgressSnapshot>? ProgressReported;

    /// <summary>
    /// Indicates whether this sink is currently connected to an IMAPIv2 connection point.
    /// </summary>
    public bool IsConnected => _connectionPoint != null && _cookie != 0;

    /// <summary>
    /// Initializes a new instance of <see cref="DiscFormatDataEventsSink"/>.
    /// </summary>
    /// <param name="progress">Optional progress reporter receiving translated snapshots.</param>
    /// <param name="onSnapshot">Optional delegate callback invoked for each translated snapshot.</param>
    public DiscFormatDataEventsSink(
        IProgress<BurnProgressSnapshot>? progress = null,
        Action<BurnProgressSnapshot>? onSnapshot = null)
    {
        _progress = progress;
        _onSnapshot = onSnapshot;
    }

    /// <summary>
    /// Connects this sink to the <see cref="IConnectionPointContainer"/> of an IMAPIv2 data writer
    /// using <c>IID_DDiscFormat2DataEvents</c>.
    /// </summary>
    /// <param name="discFormatData">The IMAPIv2 data writer object implementing <see cref="IConnectionPointContainer"/>.</param>
    public void Connect(object discFormatData)
    {
        ArgumentNullException.ThrowIfNull(discFormatData);
        Disconnect();

        if (discFormatData is IConnectionPointContainer cpc)
        {
            Guid iid = Imapi2Guids.IidDDiscFormat2DataEvents;
            cpc.FindConnectionPoint(ref iid, out var cp);
            if (cp != null)
            {
                try
                {
                    cp.Advise(this, out int cookie);
                    _connectionPoint = cp;
                    _cookie = cookie;
                }
                catch
                {
                    if (Marshal.IsComObject(cp))
                    {
                        try { Marshal.ReleaseComObject(cp); } catch { }
                    }
                    throw;
                }
            }
        }
    }

    /// <summary>
    /// Disconnects this sink from the active IMAPIv2 connection point, revoking the advise cookie.
    /// </summary>
    public void Disconnect()
    {
        if (_connectionPoint != null && _cookie != 0)
        {
            try
            {
                _connectionPoint.Unadvise(_cookie);
            }
            catch
            {
                // Suppress COM unadvise failures during teardown
            }
            finally
            {
                if (_connectionPoint != null && Marshal.IsComObject(_connectionPoint))
                {
                    try { Marshal.ReleaseComObject(_connectionPoint); } catch { }
                }
                _cookie = 0;
                _connectionPoint = null;
            }
        }
    }

    /// <summary>
    /// IMAPIv2 DISPID 0x200 event handler invoked by the native burning engine.
    /// </summary>
    /// <param name="sender">The originating <see cref="IDiscFormat2Data"/> COM instance.</param>
    /// <param name="progress">The progress object implementing <see cref="IDiscFormat2DataEventArgs"/>.</param>
    [DispId(0x200)]
    public void Update(
        [In, MarshalAs(UnmanagedType.IDispatch)] object sender,
        [In, MarshalAs(UnmanagedType.IDispatch)] object progress)
    {
        try
        {
            var snapshot = ExtractSnapshot(sender, progress);
            if (snapshot != null)
            {
                _progress?.Report(snapshot);
                _onSnapshot?.Invoke(snapshot);
                ProgressReported?.Invoke(this, snapshot);
            }
        }
        finally
        {
            if (progress != null && !ReferenceEquals(progress, sender) && Marshal.IsComObject(progress))
            {
                try { Marshal.ReleaseComObject(progress); } catch { }
            }
        }
    }

    /// <summary>
    /// Extracts and translates an incoming progress COM object or test mock into a <see cref="BurnProgressSnapshot"/>.
    /// </summary>
    public static BurnProgressSnapshot? ExtractSnapshot(object? sender, object? progress)
    {
        if (progress == null)
        {
            return null;
        }

        if (progress is BurnProgressSnapshot alreadySnapshot)
        {
            return alreadySnapshot;
        }

        if (progress is IDiscFormat2DataEventArgs typedArgs)
        {
            return CreateSnapshot(typedArgs, sender);
        }

        // Fallback: Reflection / duck-typing for mocks or non-typed COM wrappers
        try
        {
            Type type = progress.GetType();
            int elapsed = ReadIntProp(type, progress, "ElapsedTime");
            int remaining = ReadIntProp(type, progress, "RemainingTime", "EstimatedRemainingTime");
            int startLba = ReadIntProp(type, progress, "StartLba");
            int sectorCount = ReadIntProp(type, progress, "SectorCount");
            int lastReadLba = ReadIntProp(type, progress, "LastReadLba");
            int lastWrittenLba = ReadIntProp(type, progress, "LastWrittenLba");
            int totalBuffer = ReadIntProp(type, progress, "TotalSystemBuffer");
            int usedBuffer = ReadIntProp(type, progress, "UsedSystemBuffer");
            int freeBuffer = ReadIntProp(type, progress, "FreeSystemBuffer");

            ImapiFormat2DataWriteAction action = ImapiFormat2DataWriteAction.WritingData;
            var actionProp = type.GetProperty("CurrentAction");
            if (actionProp != null)
            {
                var val = actionProp.GetValue(progress);
                if (val != null)
                {
                    action = (ImapiFormat2DataWriteAction)Convert.ToInt32(val);
                }
            }

            return CreateSnapshot(
                elapsed, remaining, startLba, sectorCount,
                lastReadLba, lastWrittenLba, totalBuffer, usedBuffer, freeBuffer,
                action, sender);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Translates an <see cref="IDiscFormat2DataEventArgs"/> COM instance into a <see cref="BurnProgressSnapshot"/>.
    /// </summary>
    public static BurnProgressSnapshot CreateSnapshot(IDiscFormat2DataEventArgs args, object? sender = null)
    {
        ArgumentNullException.ThrowIfNull(args);

        int elapsed = 0;
        int remaining = 0;
        int startLba = 0;
        int sectorCount = 0;
        int lastReadLba = 0;
        int lastWrittenLba = -1;
        int totalBuffer = 0;
        int usedBuffer = 0;
        int freeBuffer = 0;
        ImapiFormat2DataWriteAction action = ImapiFormat2DataWriteAction.WritingData;

        try { elapsed = args.ElapsedTime; } catch { }
        try { remaining = args.RemainingTime; } catch { }
        try { startLba = args.StartLba; } catch { }
        try { sectorCount = args.SectorCount; } catch { }
        try { lastReadLba = args.LastReadLba; } catch { }
        try { lastWrittenLba = args.LastWrittenLba; } catch { }
        try { totalBuffer = args.TotalSystemBuffer; } catch { }
        try { usedBuffer = args.UsedSystemBuffer; } catch { }
        try { freeBuffer = args.FreeSystemBuffer; } catch { }
        try { action = args.CurrentAction; } catch { }

        return CreateSnapshot(
            elapsed, remaining, startLba, sectorCount,
            lastReadLba, lastWrittenLba, totalBuffer, usedBuffer, freeBuffer,
            action, sender);
    }

    /// <summary>
    /// Constructs a <see cref="BurnProgressSnapshot"/> from raw progress metrics.
    /// </summary>
    public static BurnProgressSnapshot CreateSnapshot(
        int elapsedTimeSec,
        int remainingTimeSec,
        int startLba,
        int sectorCount,
        int lastReadLba,
        int lastWrittenLba,
        int totalSystemBuffer,
        int usedSystemBuffer,
        int freeSystemBuffer,
        ImapiFormat2DataWriteAction currentAction,
        object? sender = null)
    {
        int relativeLba = 0;
        if (lastWrittenLba >= startLba && sectorCount > 0)
        {
            relativeLba = Math.Min(sectorCount, lastWrittenLba - startLba + 1);
        }
        else if (lastWrittenLba > 0)
        {
            relativeLba = Math.Min(sectorCount, lastWrittenLba);
        }

        if (currentAction == ImapiFormat2DataWriteAction.Completed && sectorCount > 0)
        {
            relativeLba = sectorCount;
        }

        double bufferPercent = 100.0;
        if (totalSystemBuffer > 0)
        {
            bufferPercent = Math.Clamp((double)usedSystemBuffer / totalSystemBuffer * 100.0, 0.0, 100.0);
        }
        else if (currentAction == ImapiFormat2DataWriteAction.Completed)
        {
            bufferPercent = 0.0;
        }

        // Speed calculations
        double speedMultiplier = 0.0;
        long bytesPerSec = 0;

        if (sender is IDiscFormat2Data formatData)
        {
            try
            {
                int sectorsPerSec = formatData.CurrentWriteSpeed;
                if (sectorsPerSec > 0)
                {
                    bytesPerSec = (long)sectorsPerSec * OpticalConstants.DataSectorSize;
                    speedMultiplier = (double)bytesPerSec / OpticalConstants.CdData1XBytesPerSec;
                }
            }
            catch
            {
            }
        }

        if (bytesPerSec == 0 && elapsedTimeSec > 0 && relativeLba > 0)
        {
            long bytesWritten = (long)relativeLba * OpticalConstants.DataSectorSize;
            bytesPerSec = (long)(bytesWritten / (double)elapsedTimeSec);
            speedMultiplier = (double)bytesPerSec / OpticalConstants.CdData1XBytesPerSec;
        }

        var elapsed = TimeSpan.FromSeconds(Math.Max(0, elapsedTimeSec));
        var remaining = currentAction == ImapiFormat2DataWriteAction.Completed
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds(Math.Max(0, remainingTimeSec));

        return new BurnProgressSnapshot
        {
            ElapsedTime = elapsed,
            RemainingTime = remaining,
            CurrentLba = relativeLba,
            TotalLba = sectorCount,
            BufferLevelPercent = bufferPercent,
            CurrentSpeedMultiplier = speedMultiplier,
            CurrentBytesPerSecond = bytesPerSec,
            CurrentActionDescription = currentAction.ToString()
        };
    }

    private static int ReadIntProp(Type type, object target, params string[] names)
    {
        foreach (var name in names)
        {
            var prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (prop != null)
            {
                var val = prop.GetValue(target);
                if (val != null)
                {
                    return Convert.ToInt32(val);
                }
            }
        }
        return 0;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            Disconnect();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}
