using System.Globalization;
using System.Resources;

namespace CdBurner.App.Resources;

/// <summary>
/// User-visible dialog and status text. The text lives in <c>Strings.resx</c>; one property per key so a
/// misspelled key fails to compile. Strings with <c>{0}</c> placeholders are filled in with <see cref="Format"/>.
/// </summary>
public static class Strings
{
    private static readonly ResourceManager Manager = new("CdBurner.App.Resources.Strings", typeof(Strings).Assembly);

    /// <summary>Looks up a key for the current UI language. Throws if the key is missing, so a gap never shows as blank text.</summary>
    public static string Get(string key)
    {
        // The .resx may be checked out with CRLF line endings; the original literals used LF, so normalise.
        var text = Manager.GetString(key, CultureInfo.CurrentUICulture)
            ?? throw new MissingManifestResourceException($"Missing UI string: {key}");
        return text.Replace("\r\n", "\n");
    }

    /// <summary>Looks up a key and fills in its numbered placeholders.</summary>
    public static string Format(string key, params object?[] args)
    {
        return string.Format(CultureInfo.CurrentCulture, Get(key), args);
    }

    public static string DialogAboutBody => Get(nameof(DialogAboutBody));
    public static string DialogAboutTitle => Get(nameof(DialogAboutTitle));
    public static string DialogAppTitle => Get(nameof(DialogAppTitle));
    public static string DialogBurnErrorBody => Get(nameof(DialogBurnErrorBody));
    public static string DialogBurnErrorTitle => Get(nameof(DialogBurnErrorTitle));
    public static string DialogBurnFailedBody => Get(nameof(DialogBurnFailedBody));
    public static string DialogBurnFailedTitle => Get(nameof(DialogBurnFailedTitle));
    public static string DialogCopySuspectAudioQuestion => Get(nameof(DialogCopySuspectAudioQuestion));
    public static string DialogDamagedAudioBody => Get(nameof(DialogDamagedAudioBody));
    public static string DialogDamagedAudioTitle => Get(nameof(DialogDamagedAudioTitle));
    public static string DialogDiscCopyTitle => Get(nameof(DialogDiscCopyTitle));
    public static string DialogDrmSkippedBody => Get(nameof(DialogDrmSkippedBody));
    public static string DialogDrmSkippedTitle => Get(nameof(DialogDrmSkippedTitle));
    public static string DialogExitDuringBurnBody => Get(nameof(DialogExitDuringBurnBody));
    public static string DialogHardToReadBody => Get(nameof(DialogHardToReadBody));
    public static string DialogHardToReadTitle => Get(nameof(DialogHardToReadTitle));
    public static string DialogInsertBlankForCopyBody => Get(nameof(DialogInsertBlankForCopyBody));
    public static string DialogNoBlankDetectedBody => Get(nameof(DialogNoBlankDetectedBody));
    public static string DialogQueueInsertDiscBody => Get(nameof(DialogQueueInsertDiscBody));
    public static string DialogReplaceDiscBody => Get(nameof(DialogReplaceDiscBody));
    public static string DialogReplaceDiscTitle => Get(nameof(DialogReplaceDiscTitle));
    public static string DialogUnexpectedErrorBody => Get(nameof(DialogUnexpectedErrorBody));
    public static string DialogUnsupportedFilesBody => Get(nameof(DialogUnsupportedFilesBody));
    public static string DialogUnsupportedFilesTitle => Get(nameof(DialogUnsupportedFilesTitle));
    public static string StatusArchiveBurnCancelled => Get(nameof(StatusArchiveBurnCancelled));
    public static string StatusArchiveCancellingArchiveOperation => Get(nameof(StatusArchiveCancellingArchiveOperation));
    public static string StatusArchiveDownloadingPar2cmdlinePar2Exe => Get(nameof(StatusArchiveDownloadingPar2cmdlinePar2Exe));
    public static string StatusArchiveNoOpticalRecorderSelected => Get(nameof(StatusArchiveNoOpticalRecorderSelected));
    public static string StatusArchivePar2ExecutableNotFound => Get(nameof(StatusArchivePar2ExecutableNotFound));
    public static string StatusArchivePar2RecoveryDataRequiresPar2cmdlinePar2 => Get(nameof(StatusArchivePar2RecoveryDataRequiresPar2cmdlinePar2));
    public static string StatusArchivePlanningArchiveSpanning => Get(nameof(StatusArchivePlanningArchiveSpanning));
    public static string StatusArchiveRepairCancelled => Get(nameof(StatusArchiveRepairCancelled));
    public static string StatusArchiveRepairIsNotAvailableForThe => Get(nameof(StatusArchiveRepairIsNotAvailableForThe));
    public static string StatusArchiveSourceFolderIsNotSpecified => Get(nameof(StatusArchiveSourceFolderIsNotSpecified));
    public static string StatusArchiveStagingCancelled => Get(nameof(StatusArchiveStagingCancelled));
    public static string StatusArchiveStagingFolderIsNotSpecified => Get(nameof(StatusArchiveStagingFolderIsNotSpecified));
    public static string StatusArchiveVerificationCancelled => Get(nameof(StatusArchiveVerificationCancelled));
    public static string StatusArchiveVerifyPathIsEmpty => Get(nameof(StatusArchiveVerifyPathIsEmpty));
    public static string StatusAudioBurnAddAtLeastOneTrackBefore => Get(nameof(StatusAudioBurnAddAtLeastOneTrackBefore));
    public static string StatusAudioBurnAudioCdBurnCancelledByUser => Get(nameof(StatusAudioBurnAudioCdBurnCancelledByUser));
    public static string StatusAudioBurnAudioCdRipCancelledByUser => Get(nameof(StatusAudioBurnAudioCdRipCancelledByUser));
    public static string StatusAudioBurnAudioPreviewStopped => Get(nameof(StatusAudioBurnAudioPreviewStopped));
    public static string StatusAudioBurnCancellingAudioCdBurn => Get(nameof(StatusAudioBurnCancellingAudioCdBurn));
    public static string StatusAudioBurnClearedAllTracksFromCompilation => Get(nameof(StatusAudioBurnClearedAllTracksFromCompilation));
    public static string StatusAudioBurnDiscNotFoundInAccurateripDatabase => Get(nameof(StatusAudioBurnDiscNotFoundInAccurateripDatabase));
    public static string StatusAudioBurnNoAudioTracksFoundOnInserted => Get(nameof(StatusAudioBurnNoAudioTracksFoundOnInserted));
    public static string StatusAudioBurnNoAudioTracksInCompilationTo => Get(nameof(StatusAudioBurnNoAudioTracksInCompilationTo));
    public static string StatusAudioBurnNoOpticalRecorderSelectedOrAvailable => Get(nameof(StatusAudioBurnNoOpticalRecorderSelectedOrAvailable));
    public static string StatusAudioBurnOffsetDetectionCancelled => Get(nameof(StatusAudioBurnOffsetDetectionCancelled));
    public static string StatusBurnFailedNoSessionAtOnce => Get(nameof(StatusBurnFailedNoSessionAtOnce));
    public static string StatusDataBurnCancellingDataDiscBurn => Get(nameof(StatusDataBurnCancellingDataDiscBurn));
    public static string StatusDataBurnCompilingOpticalDiscImage => Get(nameof(StatusDataBurnCompilingOpticalDiscImage));
    public static string StatusDataBurnDataDiscBurnCancelled => Get(nameof(StatusDataBurnDataDiscBurnCancelled));
    public static string StatusDataBurnDataDiscBurnCancelledByUser => Get(nameof(StatusDataBurnDataDiscBurnCancelledByUser));
    public static string StatusDataBurnIsoExportCancelled => Get(nameof(StatusDataBurnIsoExportCancelled));
    public static string StatusDataBurnPreviousSessionImportedSuccessfully => Get(nameof(StatusDataBurnPreviousSessionImportedSuccessfully));
    public static string StatusDataBurnSessionImportCancelled => Get(nameof(StatusDataBurnSessionImportCancelled));
    public static string StatusDiscUnchangedSwitchOffDirect => Get(nameof(StatusDiscUnchangedSwitchOffDirect));
    public static string StatusDriveUtilitiesCancellingHardwareOperation => Get(nameof(StatusDriveUtilitiesCancellingHardwareOperation));
    public static string StatusDriveUtilitiesCheckingTheDiscReadingEverySector => Get(nameof(StatusDriveUtilitiesCheckingTheDiscReadingEverySector));
    public static string StatusDriveUtilitiesDiscCheckCancelled => Get(nameof(StatusDriveUtilitiesDiscCheckCancelled));
    public static string StatusDriveUtilitiesDriveCompatibilityReportGeneratedAndReady => Get(nameof(StatusDriveUtilitiesDriveCompatibilityReportGeneratedAndReady));
    public static string StatusDriveUtilitiesEraseOperationCancelledByUser => Get(nameof(StatusDriveUtilitiesEraseOperationCancelledByUser));
    public static string StatusDriveUtilitiesNoDriveSelectedForTheDisc => Get(nameof(StatusDriveUtilitiesNoDriveSelectedForTheDisc));
    public static string StatusDriveUtilitiesNoOpticalRecorderDriveSelected => Get(nameof(StatusDriveUtilitiesNoOpticalRecorderDriveSelected));
    public static string StatusDriveUtilitiesNoOpticalRecorderSelectedForBitsetting => Get(nameof(StatusDriveUtilitiesNoOpticalRecorderSelectedForBitsetting));
    public static string StatusDriveUtilitiesNoOpticalRecorderSelectedForErase => Get(nameof(StatusDriveUtilitiesNoOpticalRecorderSelectedForErase));
    public static string StatusDriveUtilitiesNoOpticalRecorderSelectedForSurface => Get(nameof(StatusDriveUtilitiesNoOpticalRecorderSelectedForSurface));
    public static string StatusDriveUtilitiesRunASurfaceScanBeforeExporting => Get(nameof(StatusDriveUtilitiesRunASurfaceScanBeforeExporting));
    public static string StatusDriveUtilitiesSurfaceScanCancelledByUser => Get(nameof(StatusDriveUtilitiesSurfaceScanCancelledByUser));
    public static string StatusDvdVideoAuthoringDvdVideoIsoImage => Get(nameof(StatusDvdVideoAuthoringDvdVideoIsoImage));
    public static string StatusDvdVideoBuildingTheDiscImageToPlace => Get(nameof(StatusDvdVideoBuildingTheDiscImageToPlace));
    public static string StatusDvdVideoCancellingDvdVideoOperation => Get(nameof(StatusDvdVideoCancellingDvdVideoOperation));
    public static string StatusDvdVideoDownloadingFfmpegLgpl => Get(nameof(StatusDvdVideoDownloadingFfmpegLgpl));
    public static string StatusDvdVideoDvdVideoBurnCancelled => Get(nameof(StatusDvdVideoDvdVideoBurnCancelled));
    public static string StatusDvdVideoDvdVideoBurnCancelledByUser => Get(nameof(StatusDvdVideoDvdVideoBurnCancelledByUser));
    public static string StatusDvdVideoDvdVideoIsoBuildCancelled => Get(nameof(StatusDvdVideoDvdVideoIsoBuildCancelled));
    public static string StatusDvdVideoPreparingDvdVideoAuthoringPipeline => Get(nameof(StatusDvdVideoPreparingDvdVideoAuthoringPipeline));
    public static string StatusDvdVideoTranscodingAuthoringDvdVideoStructure => Get(nameof(StatusDvdVideoTranscodingAuthoringDvdVideoStructure));
    public static string StatusImageStudioBurnCompleteVerifyingDiscBitFor => Get(nameof(StatusImageStudioBurnCompleteVerifyingDiscBitFor));
    public static string StatusImageStudioBurnQueueCancelledByUser => Get(nameof(StatusImageStudioBurnQueueCancelledByUser));
    public static string StatusImageStudioBurnQueueIsEmpty => Get(nameof(StatusImageStudioBurnQueueIsEmpty));
    public static string StatusImageStudioCancellingOperation => Get(nameof(StatusImageStudioCancellingOperation));
    public static string StatusImageStudioDiscCopyCancelledByUser => Get(nameof(StatusImageStudioDiscCopyCancelledByUser));
    public static string StatusImageStudioDiscRipCancelledByUser => Get(nameof(StatusImageStudioDiscRipCancelledByUser));
    public static string StatusImageStudioImageBurnCancelledByUser => Get(nameof(StatusImageStudioImageBurnCancelledByUser));
    public static string StatusImageStudioNoOpticalRecorderDriveSelectedOr => Get(nameof(StatusImageStudioNoOpticalRecorderDriveSelectedOr));
    public static string StatusImageStudioSourceDiscImageFileDoesNot => Get(nameof(StatusImageStudioSourceDiscImageFileDoesNot));
    public static string StatusImageStudioSourceOrTargetOpticalDriveIs => Get(nameof(StatusImageStudioSourceOrTargetOpticalDriveIs));
    public static string StatusSettingsCheckingGithubReleasesForUpdates => Get(nameof(StatusSettingsCheckingGithubReleasesForUpdates));
    public static string StatusSettingsSettingsLoaded => Get(nameof(StatusSettingsSettingsLoaded));
    public static string StatusSettingsSettingsResetToDefaultValues => Get(nameof(StatusSettingsSettingsResetToDefaultValues));
    public static string StatusSettingsSettingsSavedSuccessfully => Get(nameof(StatusSettingsSettingsSavedSuccessfully));
    public static string StatusSettingsUpdateCheckHasNotBeenRun => Get(nameof(StatusSettingsUpdateCheckHasNotBeenRun));
    public static string StatusVirtualDriveCannotInjectBadLbaNoMedia => Get(nameof(StatusVirtualDriveCannotInjectBadLbaNoMedia));
    public static string StatusVirtualDriveClearedAllSimulatedBadLbas => Get(nameof(StatusVirtualDriveClearedAllSimulatedBadLbas));
    public static string StatusVirtualDriveNoVirtualOpticalDriveIsAvailable => Get(nameof(StatusVirtualDriveNoVirtualOpticalDriveIsAvailable));
    public static string StatusVirtualDriveNoVirtualOpticalDriveIsCurrently => Get(nameof(StatusVirtualDriveNoVirtualOpticalDriveIsCurrently));
    public static string StatusVirtualDriveVirtualDriveTrayClosed => Get(nameof(StatusVirtualDriveVirtualDriveTrayClosed));
    public static string StatusVirtualDriveVirtualDriveTrayOpened => Get(nameof(StatusVirtualDriveVirtualDriveTrayOpened));
}
