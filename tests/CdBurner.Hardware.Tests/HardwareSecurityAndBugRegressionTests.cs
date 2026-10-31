using CdBurner.Hardware.Burning;
using CdBurner.Hardware.Devices;
using CdBurner.Hardware.Interop;
using CdBurner.Hardware.Scsi;
using Xunit;

namespace CdBurner.Hardware.Tests;

/// <summary>
/// Regression tests verifying security & bug fixes across CdBurner.Hardware
/// (MtaBurnWorker uncancelled token handling, DeviceChangeMonitor unmanaged pointer bounds,
/// ManagedComStream MAXULONG64 CopyTo, and MMC CDB 24-bit bounds validation).
/// </summary>
public sealed class HardwareSecurityAndBugRegressionTests
{
    [Fact]
    public async Task MtaBurnWorker_WhenWorkThrowsDefaultOperationCanceledException_TransitionsToCanceledWithoutCrashing()
    {
        // Throwing new OperationCanceledException() has CancellationToken.None (IsCancellationRequested == false).
        // Previously, tcs.TrySetCanceled(oce.CancellationToken) threw an unhandled ArgumentOutOfRangeException on the MTA thread.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            MtaBurnWorker.RunAsync<int>((_, _) => throw new OperationCanceledException("Aborted without token")));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            MtaBurnWorker.RunTaskAsync<int>((_, _) => Task.FromException<int>(new OperationCanceledException("Async aborted without token"))));
    }

    [Fact]
    public unsafe void DeviceChangeMonitor_ProcessMessage_InvalidOrUndersizedHeader_ReturnsFalseSafely()
    {
        var monitor = new DeviceChangeMonitor();

        // Low/invalid pointer (< 0xFFFF) must be rejected immediately without reading memory
        Assert.False(monitor.ProcessMessage(
            DeviceChangeMonitor.WM_DEVICECHANGE,
            (nint)DeviceChangeMonitor.DBT_DEVICEARRIVAL,
            (nint)0x0010));

        // Undersized header (dbch_size = 12 < 18 required for DEV_BROADCAST_VOLUME) must be rejected
        int* hdr = stackalloc int[3];
        hdr[0] = 12; // dbch_size = 12
        hdr[1] = (int)DeviceChangeMonitor.DBT_DEVTYP_VOLUME;
        hdr[2] = 0;

        Assert.False(monitor.ProcessMessage(
            DeviceChangeMonitor.WM_DEVICECHANGE,
            (nint)DeviceChangeMonitor.DBT_DEVICEARRIVAL,
            (nint)hdr));
    }

    [Fact]
    public unsafe void ManagedComStream_CopyTo_WithMaxUlong64MinusOne_CopiesEntireStreamAndNotifiesCallback()
    {
        byte[] payload = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
        int callbackBytes = 0;

        using var srcMem = new MemoryStream(payload);
        using var dstMem = new MemoryStream();
        using var srcCom = new ManagedComStream(srcMem, leaveOpen: true, onBytesRead: n => callbackBytes += n);
        using var dstCom = new ManagedComStream(dstMem, leaveOpen: true);

        long readCount = 0;
        long writtenCount = 0;

        // In COM IStream::CopyTo, ULARGE_INTEGER_MAX (0xFFFFFFFFFFFFFFFF) marshals as -1L
        srcCom.CopyTo(dstCom, -1L, (nint)(&readCount), (nint)(&writtenCount));

        Assert.Equal(payload.Length, readCount);
        Assert.Equal(payload.Length, writtenCount);
        Assert.Equal(payload.Length, callbackBytes);
        Assert.Equal(payload, dstMem.ToArray());
    }

    [Fact]
    public void MmcCommandBuilder_OutOfRange24BitFields_ThrowArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MmcCommandBuilder.BuildReadCdCdb(0, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => MmcCommandBuilder.BuildReadCdCdb(0, 0x1000000));
        Assert.Throws<ArgumentOutOfRangeException>(() => MmcCommandBuilder.BuildSendCueSheetCdb(0x1000000u));
    }
}
