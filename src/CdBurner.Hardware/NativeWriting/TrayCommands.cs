using CdBurner.Hardware.Scsi;

namespace CdBurner.Hardware.NativeWriting;

/// <summary>Outcome of an eject or load request.</summary>
/// <param name="Success">The drive did what was asked.</param>
/// <param name="CommandSent">A START STOP UNIT command went to the drive (false when the load was not tried).</param>
/// <param name="NeedsManualClose">The tray has to be pushed in by hand: the drive cannot close it, or refused to.</param>
/// <param name="DiscPresent">After a load: the drive has a disc and is ready. Null after an eject or a failed load.</param>
/// <param name="Message">Plain-language result for the user.</param>
/// <param name="Mechanism">What the drive says about its loading mechanism; null when it does not report it.</param>
public sealed record TrayCommandResult(
    bool Success,
    bool CommandSent,
    bool NeedsManualClose,
    bool? DiscPresent,
    string Message,
    RemovableMediumInfo? Mechanism);

/// <summary>
/// Opens and closes the tray with START STOP UNIT (MMC-6 6.43) and says plainly when the drive cannot close it
/// itself. Whether it can is read first from the Removable Medium feature (0x0003): slim USB drives such as the
/// GP60NB50 have a spring tray and report the Eject bit but not the Load bit; they accept an eject and refuse a load.
/// </summary>
public static class TrayCommands
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PollStep = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Consecutive "medium not present" answers after a load before the drive is taken to be empty. A drive can
    /// answer it briefly while the tray is still moving; three answers is 1.5 s of the drive saying the same.
    /// </summary>
    private const int NoDiscAnswersBeforeEmpty = 3;

    /// <summary>Opens the tray (START STOP UNIT, LoEj=1, Start=0).</summary>
    public static TrayCommandResult Eject(IScsiTransport transport)
    {
        ArgumentNullException.ThrowIfNull(transport);

        var info = TrayReload.ReadRemovableMedium(transport);
        var r = transport.ExecuteCommand(MmcCommandBuilder.BuildStartStopUnitCdb(start: false, loadEject: true), Span<byte>.Empty,
            ScsiPassThroughNative.SCSI_IOCTL_DATA_UNSPECIFIED, CommandTimeout);
        if (!r.IsSuccess)
        {
            return new TrayCommandResult(false, true, false, null, $"The drive refused to open the tray: {Describe(r)}.", info);
        }

        string message = info is not null && !CanCloseItself(info)
            ? $"Tray opened. This drive cannot close its own tray ({DescribeMechanism(info)}): push it in by hand."
            : "Tray opened.";
        return new TrayCommandResult(true, true, false, null, message, info);
    }

    /// <summary>
    /// Closes the tray (START STOP UNIT, LoEj=1, Start=1) and waits until the drive is ready or reports no disc. A
    /// drive whose Removable Medium feature has no Load bit, or that has a pop-up tray, is not sent the command: the
    /// result says to push the tray in by hand instead of failing without a word.
    /// </summary>
    public static TrayCommandResult Load(IScsiTransport transport, Action<TimeSpan> sleep, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(sleep);

        var info = TrayReload.ReadRemovableMedium(transport);
        if (info is not null && !CanCloseItself(info))
        {
            return new TrayCommandResult(false, false, true, null,
                $"This drive cannot close its own tray ({DescribeMechanism(info)}, no Load capability reported). Push the tray in by hand.", info);
        }

        var r = transport.ExecuteCommand(MmcCommandBuilder.BuildStartStopUnitCdb(start: true, loadEject: true), Span<byte>.Empty,
            ScsiPassThroughNative.SCSI_IOCTL_DATA_UNSPECIFIED, CommandTimeout);
        if (!r.IsSuccess)
        {
            // The drive did not say it lacks the capability (or did not report the feature) but refused: say how to go on.
            return new TrayCommandResult(false, true, true, null,
                $"The drive refused to close the tray: {Describe(r)}. If the tray is open, push it in by hand.", info);
        }

        return WaitAfterLoad(transport, sleep, ct) switch
        {
            true => new TrayCommandResult(true, true, false, true, "Tray closed; the drive is ready.", info),
            false => new TrayCommandResult(true, true, false, false, "Tray closed; there is no disc in the drive.", info),
            null => new TrayCommandResult(false, true, false, null,
                $"The tray closed but the drive was not ready within {TrayReload.ReadyTimeout.TotalSeconds:0} s.", info)
        };
    }

    /// <summary>
    /// True when the Removable Medium feature says the drive can close its tray: the Load bit is set and the tray is
    /// not a pop-up (spring) tray, which MMC lists as loading mechanism 010b and which no drive can pull in.
    /// </summary>
    public static bool CanCloseItself(RemovableMediumInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        return info.CanLoad && info.LoadingMechanism != 2;
    }

    /// <summary>Polls TEST UNIT READY: true ready with a disc, false ready to report no disc, null timed out.</summary>
    private static bool? WaitAfterLoad(IScsiTransport transport, Action<TimeSpan> sleep, CancellationToken ct)
    {
        int noDiscAnswers = 0;
        for (var waited = TimeSpan.Zero; waited <= TrayReload.ReadyTimeout; waited += PollStep)
        {
            ct.ThrowIfCancellationRequested();
            var r = transport.ExecuteCommand(new byte[6], Span<byte>.Empty, ScsiPassThroughNative.SCSI_IOCTL_DATA_UNSPECIFIED, CommandTimeout);
            if (r.IsSuccess)
            {
                return true;
            }
            // 2/3A/xx: medium not present. Anything else (2/04/xx becoming ready, 6/28/00 medium changed): keep waiting.
            noDiscAnswers = r.SenseInfo is { SenseKey: 0x02, Asc: 0x3A } ? noDiscAnswers + 1 : 0;
            if (noDiscAnswers >= NoDiscAnswersBeforeEmpty)
            {
                return false;
            }
            sleep(PollStep);
        }
        return null;
    }

    private static string Describe(ScsiExecutionResult r) => r.SenseInfo?.ToString() ?? r.ErrorMessage ?? "no reason given";

    private static string DescribeMechanism(RemovableMediumInfo info) => info.LoadingMechanism switch
    {
        0 => "caddy",
        1 => "tray",
        2 => "pop-up tray",
        4 or 5 => "changer",
        _ => $"loading mechanism {info.LoadingMechanism}"
    };
}
