using CdBurner.Hardware.Scsi;

namespace CdBurner.Hardware.NativeWriting;

/// <summary>What the drive says about how it takes discs (MMC Removable Medium feature, 0x0003).</summary>
/// <param name="LoadingMechanism">0 caddy, 1 tray, 2 pop-up tray, 4 changer (individual discs), 5 changer (magazine).</param>
/// <param name="CanLoad">The Load bit: START STOP UNIT can close the tray / pull the disc in.</param>
/// <param name="CanEject">The Eject bit: START STOP UNIT can open the tray / push the disc out.</param>
public sealed record RemovableMediumInfo(int LoadingMechanism, bool CanLoad, bool CanEject)
{
    /// <summary>
    /// True only for a caddy or an ordinary tray that the drive itself says it can both open and close. A pop-up
    /// (spring) tray, a changer, or a tray without the Load bit is never cycled: a slim USB drive such as the GP60NB50
    /// reports a tray that ejects but cannot load, and would be left open.
    /// </summary>
    public bool CanReloadByItself => CanLoad && CanEject && LoadingMechanism is 0 or 1;
}

/// <summary>
/// Clears the simulated session a drive shows after a test write by ejecting and reloading the disc, on drives that
/// can close their own tray. Without this, a test write followed by the real write in one run stops for a reload by
/// hand (the GP60NB50 refuses the cue sheet with 5/30/06 until the disc is reloaded).
/// </summary>
public static class TrayReload
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Longest wait for the drive to be ready again after loading.</summary>
    public static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Reads the Removable Medium feature (GET CONFIGURATION, RT=2); null when the drive does not report it.</summary>
    public static RemovableMediumInfo? ReadRemovableMedium(IScsiTransport transport)
    {
        ArgumentNullException.ThrowIfNull(transport);
        byte[] data = new byte[32];
        byte[] cdb = [0x46, 0x02, 0x00, 0x03, 0, 0, 0, 0, (byte)data.Length, 0];
        var r = transport.ExecuteCommand(cdb, data, ScsiPassThroughNative.SCSI_IOCTL_DATA_IN, CommandTimeout);
        if (!r.IsSuccess || r.DataTransferLength < 13 || data[8] != 0x00 || data[9] != 0x03 || data[11] < 1)
        {
            return null;
        }

        byte flags = data[12];
        return new RemovableMediumInfo((flags >> 5) & 0x07, (flags & 0x10) != 0, (flags & 0x08) != 0);
    }

    /// <summary>
    /// Ejects and reloads the disc if, and only if, the drive says it can close its own tray, then waits until it is
    /// ready and checks with <paramref name="discReadsBlank"/>. Returns true when the disc reads blank again; false
    /// (with a note saying why) when the drive cannot or the reload did not clear the session.
    /// </summary>
    public static bool TryReload(IScsiTransport transport, Action<string> note, Action<TimeSpan> sleep, Func<bool> discReadsBlank, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(note);
        ArgumentNullException.ThrowIfNull(sleep);
        ArgumentNullException.ThrowIfNull(discReadsBlank);

        var info = ReadRemovableMedium(transport);
        if (info is null)
        {
            note("The drive does not say how it loads discs, so the tray is not cycled.");
            return false;
        }
        if (!info.CanReloadByItself)
        {
            note($"The drive cannot close its own tray (loading mechanism {info.LoadingMechanism}, load {(info.CanLoad ? "yes" : "no")}), so the tray is not cycled.");
            return false;
        }

        note("The drive can close its own tray: ejecting and reloading the disc to clear the test write.");
        var eject = transport.ExecuteCommand(MmcCommandBuilder.BuildStartStopUnitCdb(start: false, loadEject: true), Span<byte>.Empty,
            ScsiPassThroughNative.SCSI_IOCTL_DATA_UNSPECIFIED, CommandTimeout);
        if (!eject.IsSuccess)
        {
            note($"Eject refused: {eject.SenseInfo?.ToString() ?? eject.ErrorMessage}.");
            return false;
        }

        sleep(TimeSpan.FromSeconds(1));
        var load = transport.ExecuteCommand(MmcCommandBuilder.BuildStartStopUnitCdb(start: true, loadEject: true), Span<byte>.Empty,
            ScsiPassThroughNative.SCSI_IOCTL_DATA_UNSPECIFIED, CommandTimeout);
        if (!load.IsSuccess)
        {
            note($"Load refused: {load.SenseInfo?.ToString() ?? load.ErrorMessage}. The tray may be open.");
            return false;
        }

        if (!WaitUntilReady(transport, sleep, ct))
        {
            note($"The drive was not ready within {ReadyTimeout.TotalSeconds:0} s of loading the disc.");
            return false;
        }

        if (!discReadsBlank())
        {
            note("After the reload the disc still does not read blank.");
            return false;
        }

        note("Disc reloaded; it reads blank again.");
        return true;
    }

    private static bool WaitUntilReady(IScsiTransport transport, Action<TimeSpan> sleep, CancellationToken ct)
    {
        var step = TimeSpan.FromMilliseconds(500);
        for (var waited = TimeSpan.Zero; waited <= ReadyTimeout; waited += step)
        {
            ct.ThrowIfCancellationRequested();
            var r = transport.ExecuteCommand(new byte[6], Span<byte>.Empty, ScsiPassThroughNative.SCSI_IOCTL_DATA_UNSPECIFIED, CommandTimeout);
            if (r.IsSuccess)
            {
                return true;
            }
            // Becoming ready (2/04/xx), media changed (6/28/00) and no disc yet (2/3A/xx) while loading: wait.
            sleep(step);
        }
        return false;
    }
}
