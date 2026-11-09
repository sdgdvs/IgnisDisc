using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Archival;
using CdBurner.Core.Audio;
using CdBurner.Core.Burning;
using CdBurner.Core.Compilation;
using CdBurner.Core.DvdVideo;
using CdBurner.Core.Images;
using CdBurner.Core.Iso;
using CdBurner.Core.Primitives;
using CdBurner.Core.Utilities;
using CdBurner.Hardware.Audio;
using CdBurner.Hardware.Burning;
using CdBurner.Hardware.Devices;
using CdBurner.Hardware.NativeWriting;
using CdBurner.Hardware.Simulation;

namespace CdBurner.Cli;

/// <summary>
/// Exception thrown for command-line syntax or argument validation errors.
/// </summary>
public sealed class CommandLineException : Exception
{
    public CommandLineException(string message) : base(message) { }
}

/// <summary>
/// Command-line interface orchestration engine for optical drive inspection, ISO mastering,
/// burning, ripping, verification, erasing, and surface scanning.
/// </summary>
public static class CliCommandRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly HashSet<string> KnownFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "json", "udf", "joliet", "verify", "close", "no-close", "raw", "full", "dao", "help", "h", "version", "v",
        "lookup", "keep-wav", "continue-on-error", "verify-accuraterip", "secure", "plan-only",
        "overwrite"
    };

    /// <summary>
    /// Executes a command-line request asynchronously and returns the process exit code (0 = success, non-zero = failure).
    /// </summary>
    public static async Task<int> RunAsync(
        string[] args,
        TextWriter stdout,
        TextWriter stderr,
        IDiscDeviceService? customDeviceService = null,
        IBurnEngineBackend? customBurnEngine = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        var parsed = CommandLineArgs.Parse(args);

        // General help / version flags without subcommand
        if (string.IsNullOrEmpty(parsed.Subcommand))
        {
            if (parsed.HasFlag("version") || parsed.HasFlag("v"))
            {
                string? version = typeof(CliCommandRunner).Assembly
                    .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                    .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
                    .FirstOrDefault()?.InformationalVersion?.Split('+')[0];
                stdout.WriteLine($"IgnisDisc (CdBurner CLI) v{version ?? "dev"} (.NET {Environment.Version.ToString(2)} Windows) — ignisdisc.tetherworks.dev — Free & Open Source (GPLv3)");
                return 0;
            }

            if (parsed.HasFlag("help") || parsed.HasFlag("h") || args.Length == 0)
            {
                PrintHelp(stdout);
                return args.Length == 0 ? 1 : 0;
            }
        }

        if (parsed.HasFlag("help") || parsed.HasFlag("h"))
        {
            PrintSubcommandHelp(parsed.Subcommand, stdout);
            return 0;
        }

        bool ownsDeviceService = customDeviceService == null;
        var deviceService = customDeviceService ?? new DiscDeviceManager(enablePhysicalEnumeration: OperatingSystem.IsWindows());
        var burnEngine = customBurnEngine ?? new CompositeBurnEngine();

        try
        {
            return await DispatchCommandAsync(parsed, stdout, stderr, deviceService, burnEngine).ConfigureAwait(false);
        }
        catch (CommandLineException cle)
        {
            return HandleError(parsed, stdout, stderr, cle.Message);
        }
        catch (FileNotFoundException fnfe)
        {
            return HandleError(parsed, stdout, stderr, fnfe.Message);
        }
        catch (DirectoryNotFoundException dnfe)
        {
            return HandleError(parsed, stdout, stderr, dnfe.Message);
        }
        catch (Exception ex)
        {
            return HandleError(parsed, stdout, stderr, ex.Message);
        }
        finally
        {
            if (ownsDeviceService && deviceService is IDisposable disp)
            {
                disp.Dispose();
            }
        }
    }

    private static int HandleError(CommandLineArgs parsed, TextWriter stdout, TextWriter stderr, string errorMessage)
    {
        if (parsed.IsJson)
        {
            var errObj = new { success = false, error = errorMessage };
            stdout.WriteLine(JsonSerializer.Serialize(errObj, JsonOptions));
        }
        else
        {
            stderr.WriteLine($"Error: {errorMessage}");
        }
        return 1;
    }

    private static async Task<int> DispatchCommandAsync(
        CommandLineArgs cmd,
        TextWriter stdout,
        TextWriter stderr,
        IDiscDeviceService deviceService,
        IBurnEngineBackend burnEngine)
    {
        string subcommand = cmd.Subcommand?.ToLowerInvariant() ?? string.Empty;

        return subcommand switch
        {
            "drives" or "list-drives" => HandleDrives(cmd, stdout, deviceService),
            "media-info" or "mediainfo" or "info" => HandleMediaInfo(cmd, stdout, deviceService),
            "build-iso" or "buildiso" => await HandleBuildIsoAsync(cmd, stdout).ConfigureAwait(false),
            "burn-iso" or "burniso" => await HandleBurnIsoAsync(cmd, stdout, stderr, deviceService, burnEngine).ConfigureAwait(false),
            "burn-data" or "burndata" => await HandleBurnDataAsync(cmd, stdout, stderr, deviceService, burnEngine).ConfigureAwait(false),
            "burn-audio" or "burnaudio" => await HandleBurnAudioAsync(cmd, stdout, stderr, deviceService, burnEngine).ConfigureAwait(false),
            "burn-dvd" or "burndvd" => await HandleBurnDvdAsync(cmd, stdout, stderr, deviceService, burnEngine).ConfigureAwait(false),
            "rip" or "rip-disc" => await HandleRipAsync(cmd, stdout, stderr, deviceService, burnEngine).ConfigureAwait(false),
            "rip-audio" or "ripaudio" => await HandleRipAudioAsync(cmd, stdout, stderr, deviceService, burnEngine).ConfigureAwait(false),
            "detect-offset" or "detectoffset" => await HandleDetectOffsetAsync(cmd, stdout, stderr, deviceService, burnEngine).ConfigureAwait(false),
            "archive-stage" or "archivestage" => await HandleArchiveStageAsync(cmd, stdout).ConfigureAwait(false),
            "archive-verify" or "archiveverify" => await HandleArchiveVerifyAsync(cmd, stdout).ConfigureAwait(false),
            "verify" or "verify-disc" => await HandleVerifyAsync(cmd, stdout, stderr, deviceService, burnEngine).ConfigureAwait(false),
            "erase" or "erase-disc" => await HandleEraseAsync(cmd, stdout, stderr, deviceService, burnEngine).ConfigureAwait(false),
            "scan" or "scan-disc" => await HandleScanAsync(cmd, stdout, stderr, deviceService, burnEngine).ConfigureAwait(false),
            "drive-report" or "drivereport" => HandleDriveReport(cmd, stdout, deviceService),
            _ => throw new CommandLineException($"Unknown command '{cmd.Subcommand}'. Use --help for usage information.")
        };
    }

    #region Subcommand Handlers

    private static int HandleDrives(CommandLineArgs cmd, TextWriter stdout, IDiscDeviceService deviceService)
    {
        var recorders = deviceService.GetRecorders(includeVirtual: true);

        if (cmd.IsJson)
        {
            var driveDtos = recorders.Select(r =>
            {
                var caps = r.GetCapabilities();
                bool canWriteCd = caps.CanWriteCdR || caps.CanWriteCdRw;
                bool canWriteDvd = caps.CanWriteDvdR || caps.CanWriteDvdRw || caps.CanWriteDvdPlusR || caps.CanWriteDvdPlusRw || caps.CanWriteDvdRam;
                bool canWriteDl = caps.CanWriteDvdPlusRDL || caps.CanWriteDvdMinusRDL;
                bool canReadCd = caps.SupportedMediaTypes.Any(m => m.IsCd());
                bool canReadDvd = caps.SupportedMediaTypes.Any(m => m.IsDvd());

                return new
                {
                    uniqueId = r.UniqueId,
                    vendorId = r.VendorId,
                    productId = r.ProductId,
                    productRevision = r.ProductRevision,
                    volumePath = r.VolumePath,
                    isVirtual = r.IsVirtual,
                    capabilities = new
                    {
                        canWriteCd = canWriteCd,
                        canWriteDvd = canWriteDvd,
                        canWriteDualLayer = canWriteDl,
                        canReadCd = canReadCd,
                        canReadDvd = canReadDvd,
                        supportedMediaTypes = caps.SupportedMediaTypes.Select(m => m.ToString()).ToList(),
                        mmcFeatures = caps.MmcFeatureSummary,
                        supportsNativeTao = caps.SupportsNativeTao,
                        supportsNativeSao = caps.SupportsNativeSao,
                        supportsNativeCdText = caps.SupportsNativeCdText,
                        supportsNativeRawDao = caps.SupportsNativeRawDao,
                        supportsTestWrite = caps.SupportsTestWrite,
                        supportsBufferUnderrunFree = caps.SupportsBufferUnderrunFree,
                        supportsLayerJump = caps.SupportsLayerJump,
                        supportedWriteSpeeds = caps.SupportedWriteSpeeds.Select(s => new
                        {
                            multiplier = s.Multiplier,
                            bytesPerSecond = s.BytesPerSecond,
                            mediaType = s.MediaType.ToString()
                        }).ToList()
                    }
                };
            }).ToList();

            var result = new { success = true, drives = driveDtos };
            stdout.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
            return 0;
        }

        stdout.WriteLine($"Found {recorders.Count} optical drive(s):");
        stdout.WriteLine(new string('-', 85));
        stdout.WriteLine($"{"Drive ID",-12} {"Vendor",-10} {"Product",-24} {"Type",-10} {"Path",-8} {"Write Capabilities",-20}");
        stdout.WriteLine(new string('-', 85));

        foreach (var r in recorders)
        {
            var caps = r.GetCapabilities();
            var writeModes = new List<string>(3);
            if (caps.CanWriteCdR || caps.CanWriteCdRw) writeModes.Add("CD");
            if (caps.CanWriteDvdR || caps.CanWriteDvdRw || caps.CanWriteDvdPlusR || caps.CanWriteDvdPlusRw || caps.CanWriteDvdRam) writeModes.Add("DVD");
            if (caps.CanWriteDvdPlusRDL || caps.CanWriteDvdMinusRDL) writeModes.Add("DL");
            string writeCapStr = writeModes.Count > 0 ? string.Join("/", writeModes) : "Read-Only";

            stdout.WriteLine($"{r.UniqueId,-12} {r.VendorId,-10} {r.ProductId,-24} {(r.IsVirtual ? "Virtual" : "Physical"),-10} {r.VolumePath,-8} {writeCapStr,-20}");
        }

        return 0;
    }

    private static int HandleDriveReport(CommandLineArgs cmd, TextWriter stdout, IDiscDeviceService deviceService)
    {
        string driveId = cmd.GetRequiredOption("drive", "--drive <id>");
        var recorder = FindRecorder(deviceService, driveId);

        var caps = recorder.GetCapabilities();
        var media = recorder.WithManufacturerId(recorder.QueryMediaStatus());
        string report = DriveCompatibilityReportBuilder.BuildMarkdownReport(recorder, caps, media);

        if (cmd.IsJson)
        {
            var result = new
            {
                success = true,
                driveId = recorder.UniqueId,
                report = report
            };
            stdout.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
            return 0;
        }

        stdout.WriteLine(report);
        return 0;
    }

    private static int HandleMediaInfo(CommandLineArgs cmd, TextWriter stdout, IDiscDeviceService deviceService)
    {
        string driveId = cmd.GetRequiredOption("drive", "--drive <id>");
        var recorder = FindRecorder(deviceService, driveId);

        var status = recorder.WithManufacturerId(recorder.QueryMediaStatus());
        var caps = recorder.GetCapabilities();
        // Overwrite-only media (DVD+RW, DVD-RAM) are never "finalized": each burn rewrites them from the start.
        bool isFinalized = status.MediaPresent && !status.IsBlank && !status.IsAppendable && !status.IsOverwriteOnly;
        long usedBytes = status.UsedSectors > 0
            ? (long)status.UsedSectors * OpticalConstants.DataSectorSize
            : Math.Max(0L, status.TotalCapacityBytes - status.FreeCapacityBytes);

        if (cmd.IsJson)
        {
            var result = new
            {
                success = true,
                driveId = recorder.UniqueId,
                vendorId = recorder.VendorId,
                productId = recorder.ProductId,
                productRevision = recorder.ProductRevision,
                volumePath = recorder.VolumePath,
                isVirtual = recorder.IsVirtual,
                mediaPresent = status.MediaPresent,
                mediaType = status.MediaType.ToString(),
                mediaTypeName = status.MediaType.GetDisplayName(),
                mediaManufacturerId = status.MediaManufacturerId,
                mmcFeatures = caps.MmcFeatureSummary,
                isBlank = status.IsBlank,
                isRewritable = status.IsRewritable,
                isAppendable = status.IsAppendable,
                isFinalized = isFinalized,
                isOverwriteOnly = status.IsOverwriteOnly,
                totalCapacityBytes = status.TotalCapacityBytes,
                freeCapacityBytes = status.FreeCapacityBytes,
                usedCapacityBytes = usedBytes,
                totalSectors = status.TotalSectors,
                freeSectors = status.FreeSectors,
                usedSectors = status.UsedSectors,
                sessionCount = status.SessionCount,
                lastRecordedLba = status.LastRecordedLba,
                nextWritableLba = status.NextWritableLba,
                supportedWriteSpeeds = caps.SupportedWriteSpeeds.Select(s => new
                {
                    multiplier = s.Multiplier,
                    bytesPerSecond = s.BytesPerSecond,
                    mediaType = s.MediaType.ToString()
                }).ToList()
            };

            stdout.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
            return 0;
        }

        stdout.WriteLine($"Drive '{recorder.UniqueId}' ({recorder.VendorId} {recorder.ProductId}):");
        if (caps.MmcFeatureSummary.Count > 0)
        {
            stdout.WriteLine($"  MMC Features:          {string.Join(", ", caps.MmcFeatureSummary)}");
        }
        stdout.WriteLine($"  Media Present:         {(status.MediaPresent ? "Yes" : "No")}");
        if (status.MediaPresent)
        {
            stdout.WriteLine($"  Media Type:            {status.MediaType.GetDisplayName()}");
            if (!string.IsNullOrWhiteSpace(status.MediaManufacturerId))
            {
                stdout.WriteLine($"  Media ID / ATIP:       {status.MediaManufacturerId}");
            }
            stdout.WriteLine($"  Blank:                 {(status.IsBlank ? "Yes" : "No")}");
            stdout.WriteLine($"  Rewritable:            {(status.IsRewritable ? "Yes" : "No")}");
            stdout.WriteLine($"  Appendable:            {(status.IsAppendable ? "Yes" : "No")}");
            stdout.WriteLine(status.IsOverwriteOnly
                ? "  Finalized:             No (rewritten in place; a burn replaces everything on it)"
                : $"  Finalized:             {(isFinalized ? "Yes" : "No")}");
            stdout.WriteLine($"  Total Capacity:        {FormatBytes(status.TotalCapacityBytes)} ({status.TotalSectors:N0} sectors)");
            stdout.WriteLine(status.HasDataThatBurningReplaces
                ? $"  Free Capacity:         {FormatBytes(status.FreeCapacityBytes)} after replacing the current files (--overwrite)"
                : $"  Free Capacity:         {FormatBytes(status.FreeCapacityBytes)} ({status.FreeSectors:N0} sectors)");
            stdout.WriteLine($"  Used Capacity:         {FormatBytes(usedBytes)} ({status.UsedSectors:N0} sectors)");
            stdout.WriteLine($"  Sessions:              {status.SessionCount}");
            stdout.WriteLine($"  Next Writable LBA:     {status.NextWritableLba:N0}");
        }

        return 0;
    }

    private static async Task<int> HandleBuildIsoAsync(CommandLineArgs cmd, TextWriter stdout)
    {
        string inputFolder = cmd.GetRequiredOption("input", "--input <folder>");
        string outputIso = cmd.GetRequiredOption("output", "--output <file.iso>");
        string? volumeLabel = cmd.GetOption("label");
        string? bootBiosPath = cmd.GetOption("boot-bios");
        string? bootUefiPath = cmd.GetOption("boot-uefi");

        if (!Directory.Exists(inputFolder))
        {
            throw new DirectoryNotFoundException($"Input directory does not exist: {inputFolder}");
        }

        bool hasUdfFlag = cmd.HasFlag("udf");
        bool hasJolietFlag = cmd.HasFlag("joliet");
        bool includeUdf = true;
        bool includeJoliet = true;

        if (hasUdfFlag || hasJolietFlag)
        {
            includeUdf = hasUdfFlag;
            includeJoliet = hasJolietFlag;
        }

        var bootConfig = BuildBootConfiguration(bootBiosPath, bootUefiPath, volumeLabel);

        string effectiveLabel = !string.IsNullOrWhiteSpace(volumeLabel) ? volumeLabel : "CDBURNER_ISO";
        var isoOptions = new IsoBuildOptions(
            VolumeLabel: effectiveLabel,
            IncludeIso9660: true,
            IncludeJoliet: includeJoliet,
            IncludeUdf: includeUdf,
            BootConfiguration: bootConfig);

        var root = CompilationDirectory.CreateRoot();
        PopulateDirectoryFromDisk(root, inputFolder);

        await IsoUdfImageBuilder.BuildToFileAsync(root, outputIso, isoOptions).ConfigureAwait(false);

        var fileInfo = new FileInfo(outputIso);
        int totalSectors = OpticalConstants.BytesToSectors(fileInfo.Length, OpticalConstants.DataSectorSize);

        if (cmd.IsJson)
        {
            var result = new
            {
                success = true,
                outputPath = Path.GetFullPath(outputIso),
                totalBytes = fileInfo.Length,
                totalSectors = totalSectors,
                volumeLabel = effectiveLabel,
                hasIso9660 = true,
                hasJoliet = includeJoliet,
                hasUdf = includeUdf,
                isBootable = bootConfig != null
            };

            stdout.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
            return 0;
        }

        stdout.WriteLine("ISO image built successfully:");
        stdout.WriteLine($"  Output Path:   {Path.GetFullPath(outputIso)}");
        stdout.WriteLine($"  Size:          {FormatBytes(fileInfo.Length)} ({totalSectors:N0} sectors)");
        stdout.WriteLine($"  Volume Label:  {effectiveLabel}");
        stdout.WriteLine($"  Joliet:        {includeJoliet}");
        stdout.WriteLine($"  UDF:           {includeUdf}");
        stdout.WriteLine($"  Bootable:      {bootConfig != null}");

        return 0;
    }

    private static async Task<int> HandleBurnIsoAsync(
        CommandLineArgs cmd,
        TextWriter stdout,
        TextWriter stderr,
        IDiscDeviceService deviceService,
        IBurnEngineBackend burnEngine)
    {
        string driveId = cmd.GetRequiredOption("drive", "--drive <id>");
        string isoPath = cmd.GetRequiredOption("iso", "--iso <file.iso>");
        string? speedStr = cmd.GetOption("speed");
        bool verify = cmd.HasFlag("verify");
        bool closeDisc = !cmd.HasFlag("no-close");
        bool overwrite = cmd.HasFlag("overwrite");

        if (!File.Exists(isoPath))
        {
            throw new FileNotFoundException($"ISO image file not found: {isoPath}", isoPath);
        }

        var recorder = FindRecorder(deviceService, driveId);
        var mediaStatus = recorder.QueryMediaStatus();

        var speedDesc = ParseSpeed(speedStr, mediaStatus.MediaType);

        var burnOptions = new DataBurnOptions
        {
            Speed = speedDesc,
            VerifyAfterBurn = verify,
            CloseDisc = closeDisc,
            OverwriteExistingData = overwrite
        };

        var burnService = new DataBurnService(burnEngine);
        DataBurnResult result;
        if (LegacyDiscImageConverter.IsLegacyImageFile(isoPath))
        {
            // .nrg / .mdf / .mds / .ccd / .img: stream the 2048-byte user data directly (no temp .iso, no 2 GB limit).
            await using var legacyStream = LegacyDiscImageConverter.OpenIsoUserDataStream(isoPath);
            result = await burnService.BurnStreamAsync(recorder, legacyStream, burnOptions).ConfigureAwait(false);
        }
        else
        {
            result = await burnService.BurnIsoFileAsync(recorder, isoPath, burnOptions).ConfigureAwait(false);
        }

        if (cmd.IsJson)
        {
            var jsonResult = new
            {
                success = result.Success,
                driveId = recorder.UniqueId,
                isoPath = Path.GetFullPath(isoPath),
                sectorsWritten = result.SectorsWritten,
                totalTime = result.TotalTime.ToString(),
                totalTimeSeconds = result.TotalTime.TotalSeconds,
                averageSpeedMultiplier = result.AverageSpeedMultiplier,
                verified = result.Verified,
                error = result.ErrorMessage
            };

            stdout.WriteLine(JsonSerializer.Serialize(jsonResult, JsonOptions));
            return result.Success ? 0 : 1;
        }

        if (result.Success)
        {
            stdout.WriteLine("Burn completed successfully:");
            stdout.WriteLine($"  Drive:          {recorder.UniqueId}");
            stdout.WriteLine($"  Sectors Burned: {result.SectorsWritten:N0}");
            stdout.WriteLine($"  Duration:       {result.TotalTime}");
            stdout.WriteLine($"  Average Speed:  {result.AverageSpeedMultiplier:F1}x");
            stdout.WriteLine($"  Verified:       {(result.Verified ? "Passed" : "No")}");
            return 0;
        }

        stderr.WriteLine($"Burn failed: {result.ErrorMessage}");
        return 1;
    }

    private static async Task<int> HandleBurnDataAsync(
        CommandLineArgs cmd,
        TextWriter stdout,
        TextWriter stderr,
        IDiscDeviceService deviceService,
        IBurnEngineBackend burnEngine)
    {
        string driveId = cmd.GetRequiredOption("drive", "--drive <id>");
        string inputFolder = cmd.GetRequiredOption("input", "--input <folder>");
        string? volumeLabel = cmd.GetOption("label");
        string? speedStr = cmd.GetOption("speed");
        string? bootBiosPath = cmd.GetOption("boot-bios");
        string? bootUefiPath = cmd.GetOption("boot-uefi");
        bool verify = cmd.HasFlag("verify");
        bool closeDisc = !cmd.HasFlag("no-close");
        bool overwrite = cmd.HasFlag("overwrite");

        if (!Directory.Exists(inputFolder))
        {
            throw new DirectoryNotFoundException($"Input directory does not exist: {inputFolder}");
        }

        bool hasUdfFlag = cmd.HasFlag("udf");
        bool hasJolietFlag = cmd.HasFlag("joliet");
        bool includeUdf = true;
        bool includeJoliet = true;

        if (hasUdfFlag || hasJolietFlag)
        {
            includeUdf = hasUdfFlag;
            includeJoliet = hasJolietFlag;
        }

        var recorder = FindRecorder(deviceService, driveId);
        var mediaStatus = recorder.QueryMediaStatus();
        var speedDesc = ParseSpeed(speedStr, mediaStatus.MediaType);

        string effectiveLabel = !string.IsNullOrWhiteSpace(volumeLabel) ? volumeLabel : "DATA_DISC";
        var bootConfig = BuildBootConfiguration(bootBiosPath, bootUefiPath, effectiveLabel);

        var root = CompilationDirectory.CreateRoot();
        if (!mediaStatus.IsBlank && mediaStatus.IsAppendable && !mediaStatus.IsOverwriteOnly)
        {
            try
            {
                root = await MultiSessionManager.ImportFromRecorderAsync(recorder, burnEngine).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                stderr.WriteLine($"Warning: Could not import previous session ({ex.Message}); writing new session without merging.");
            }
        }

        PopulateDirectoryFromDisk(root, inputFolder);

        // Leaving a disc open for later sessions requires omitting UDF 1.02 (unless explicitly requested with --udf or
        // required by a >= 4 GiB file), because Windows mounts UDF 1.02 at fixed LBA 256 and hides later Joliet sessions.
        if (!closeDisc && !hasUdfFlag && !root.GetAllFilesRecursive().Any(f => f.SizeBytes > uint.MaxValue))
        {
            includeUdf = false;
        }

        var burnOptions = new DataBurnOptions
        {
            VolumeLabel = effectiveLabel,
            Speed = speedDesc,
            VerifyAfterBurn = verify,
            CloseDisc = closeDisc,
            OverwriteExistingData = overwrite
        };

        var buildOptions = new IsoBuildOptions(
            VolumeLabel: effectiveLabel,
            IncludeIso9660: true,
            IncludeJoliet: includeJoliet,
            IncludeUdf: includeUdf,
            BootConfiguration: bootConfig);

        var phases = new PhaseTimingProgress(cmd.IsJson ? null : stdout);
        var burnService = new DataBurnService(burnEngine);
        var result = await burnService.BurnCompilationAsync(recorder, root, burnOptions, buildOptions, phases).ConfigureAwait(false);
        phases.Complete();

        if (cmd.IsJson)
        {
            var jsonResult = new
            {
                success = result.Success,
                driveId = recorder.UniqueId,
                inputFolder = Path.GetFullPath(inputFolder),
                volumeLabel = effectiveLabel,
                sectorsWritten = result.SectorsWritten,
                totalTime = result.TotalTime.ToString(),
                totalTimeSeconds = result.TotalTime.TotalSeconds,
                averageSpeedMultiplier = result.AverageSpeedMultiplier,
                verified = result.Verified,
                phases = phases.Phases.Select(p => new { phase = p.Name, seconds = Math.Round(p.Duration.TotalSeconds, 1) }),
                error = result.ErrorMessage
            };

            stdout.WriteLine(JsonSerializer.Serialize(jsonResult, JsonOptions));
            return result.Success ? 0 : 1;
        }

        if (result.Success)
        {
            stdout.WriteLine("Data burn completed successfully:");
            stdout.WriteLine($"  Drive:          {recorder.UniqueId}");
            stdout.WriteLine($"  Volume Label:   {effectiveLabel}");
            stdout.WriteLine($"  Sectors Burned: {result.SectorsWritten:N0}");
            stdout.WriteLine($"  Duration:       {result.TotalTime}");
            stdout.WriteLine($"  Verified:       {(result.Verified ? "Passed" : "No")}");
            phases.WriteSummary(stdout);
            return 0;
        }

        stderr.WriteLine($"Data burn failed: {result.ErrorMessage}");
        phases.WriteSummary(stderr);
        return 1;
    }

    /// <summary>
    /// Records how long each burn phase (IMAPI write action or service step) took, printing each phase as it starts.
    /// Implemented synchronously rather than with <see cref="Progress{T}"/>, so reports are never reordered.
    /// </summary>
    private sealed class PhaseTimingProgress : IProgress<BurnProgressSnapshot>
    {
        private readonly TextWriter? _live;
        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        private readonly List<(string Name, TimeSpan Start)> _starts = new();
        private readonly object _lock = new();
        private TimeSpan? _end;

        public PhaseTimingProgress(TextWriter? live) => _live = live;

        public void Report(BurnProgressSnapshot value)
        {
            string name = string.IsNullOrWhiteSpace(value.CurrentActionDescription) ? "Working" : value.CurrentActionDescription;
            lock (_lock)
            {
                if (_end != null || (_starts.Count > 0 && _starts[^1].Name == name)) return;
                _starts.Add((name, _clock.Elapsed));
                _live?.WriteLine($"  [{_clock.Elapsed:mm\\:ss}] {name}");
            }
        }

        public void Complete()
        {
            lock (_lock) { _end ??= _clock.Elapsed; }
        }

        public IReadOnlyList<(string Name, TimeSpan Duration)> Phases
        {
            get
            {
                lock (_lock)
                {
                    var end = _end ?? _clock.Elapsed;
                    return _starts.Select((s, i) => (s.Name, (i + 1 < _starts.Count ? _starts[i + 1].Start : end) - s.Start)).ToList();
                }
            }
        }

        public void WriteSummary(TextWriter writer)
        {
            var phases = Phases;
            if (phases.Count == 0) return;
            writer.WriteLine("  Phase timing:");
            foreach (var (name, duration) in phases)
            {
                writer.WriteLine($"    {name,-28} {duration:mm\\:ss\\.f}");
            }
        }
    }

    private static async Task<int> HandleBurnAudioAsync(
        CommandLineArgs cmd,
        TextWriter stdout,
        TextWriter stderr,
        IDiscDeviceService deviceService,
        IBurnEngineBackend burnEngine)
    {
        string driveId = cmd.GetRequiredOption("drive", "--drive <id>");
        string tracksRaw = cmd.GetRequiredOption("tracks", "--tracks <file1,file2,...>");
        string? title = cmd.GetOption("title");
        string? artist = cmd.GetOption("artist");
        string? speedStr = cmd.GetOption("speed");
        bool dao = cmd.HasFlag("dao");
        bool native = cmd.HasFlag("native");
        bool testWrite = cmd.HasFlag("test-write");
        if (native && !testWrite)
        {
            throw new CommandLineException("--native needs --test-write: this version only does native test writes (laser off).");
        }

        var recorder = FindRecorder(deviceService, driveId);
        if (recorder is VirtualDiscRecorder vRec && !vRec.QueryMediaStatus().MediaPresent)
        {
            vRec.InsertMedia(VirtualDiscMedium.CreateBlankCdR700());
        }
        var mediaStatus = recorder.QueryMediaStatus();
        var speedDesc = ParseSpeed(speedStr, mediaStatus.MediaType, isAudio: true);

        var trackFiles = tracksRaw.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (trackFiles.Length == 0)
        {
            throw new CommandLineException("No track files provided in --tracks argument.");
        }

        foreach (var track in trackFiles)
        {
            if (!File.Exists(track))
            {
                throw new FileNotFoundException($"Audio track file not found: {track}", track);
            }
        }

        using var mfDecoder = OperatingSystem.IsWindows() ? new MediaFoundationAudioDecoder() : null;
        var nativeEngine = native ? new NativeAudioBurnEngine() : null;
        var audioBurnService = new AudioCdBurnService(nativeEngine ?? burnEngine, mfDecoder);

        var compilation = new AudioCdCompilation
        {
            AlbumTitle = title ?? "Audio CD",
            AlbumPerformer = artist ?? "Various Artists",
            RecordingMode = dao ? AudioRecordingMode.DiscAtOnceGapless : AudioRecordingMode.TrackAtOnce,
            IncludeCdText = !string.IsNullOrEmpty(title) || !string.IsNullOrEmpty(artist)
        };

        for (int i = 0; i < trackFiles.Length; i++)
        {
            string trackPath = trackFiles[i];
            if (string.Equals(Path.GetExtension(trackPath), ".cue", StringComparison.OrdinalIgnoreCase))
            {
                compilation.ImportCueSheet(trackPath, audioBurnService.AudioDecoder);
                compilation.IncludeCdText = true;
                continue;
            }

            if (AudioPlaylistParser.IsPlaylistFile(trackPath))
            {
                compilation.ImportPlaylist(trackPath, audioBurnService.AudioDecoder);
                compilation.IncludeCdText = true;
                continue;
            }

            string trackTitle = Path.GetFileNameWithoutExtension(trackPath);
            string trackPerformer = artist ?? compilation.AlbumPerformer;
            string? trackSongwriter = null;
            string? trackComposer = null;
            string? trackIsrc = null;

            try
            {
                var meta = audioBurnService.AudioDecoder.ProbeAudioFile(trackPath);
                if (!string.IsNullOrWhiteSpace(meta.Title))
                {
                    trackTitle = meta.Title;
                    compilation.IncludeCdText = true;
                }
                if (string.IsNullOrEmpty(artist) && !string.IsNullOrWhiteSpace(meta.Artist))
                {
                    trackPerformer = meta.Artist;
                    compilation.IncludeCdText = true;
                }
                if (string.IsNullOrEmpty(title) && compilation.AlbumTitle == "Audio CD" && !string.IsNullOrWhiteSpace(meta.Album))
                {
                    compilation.AlbumTitle = meta.Album;
                    compilation.IncludeCdText = true;
                }
                if (string.IsNullOrEmpty(artist) && compilation.AlbumPerformer == "Various Artists" && !string.IsNullOrWhiteSpace(meta.AlbumArtist ?? meta.Artist))
                {
                    compilation.AlbumPerformer = (meta.AlbumArtist ?? meta.Artist)!;
                }
                trackSongwriter = meta.Songwriter;
                trackComposer = meta.Composer;
                trackIsrc = meta.Isrc;
            }
            catch
            {
                // Fall back to filename metadata if probe fails here (BurnAsync will report detailed decode errors)
            }

            int nextTrackNum = compilation.TrackCount + 1;
            compilation.AddTrack(new AudioCdTrackItem(
                TrackNumber: nextTrackNum,
                SourceFilePath: trackPath,
                Title: trackTitle,
                Performer: trackPerformer,
                Isrc: trackIsrc,
                PreGapFrames: nextTrackNum == 1 ? 150 : 0)
            {
                Songwriter = trackSongwriter,
                Composer = trackComposer
            });
        }

        var result = await audioBurnService.BurnAsync(
            recorder,
            compilation,
            new AudioBurnOptions { Speed = speedDesc, UseDiscAtOnce = dao, WriteCdText = compilation.IncludeCdText, Simulate = testWrite }).ConfigureAwait(false);

        if (cmd.IsJson)
        {
            var jsonResult = new
            {
                success = result.Success,
                testWrite,
                native,
                commandLog = nativeEngine?.LastLogPath,
                discStillBlank = nativeEngine?.LastResult?.DiscStillBlankAfterTestWrite,
                driveId = recorder.UniqueId,
                title = compilation.AlbumTitle,
                artist = compilation.AlbumPerformer,
                trackCount = compilation.TrackCount,
                sectorsWritten = result.SectorsWritten,
                totalTime = result.TotalTime.ToString(),
                totalTimeSeconds = result.TotalTime.TotalSeconds,
                averageSpeedMultiplier = result.AverageSpeedMultiplier,
                error = result.ErrorMessage
            };

            stdout.WriteLine(JsonSerializer.Serialize(jsonResult, JsonOptions));
            return result.Success ? 0 : 1;
        }

        if (result.Success && testWrite)
        {
            stdout.WriteLine("Audio CD test write completed (laser off; the disc was not written):");
            stdout.WriteLine($"  Drive:          {recorder.UniqueId}");
            stdout.WriteLine($"  Tracks:         {compilation.TrackCount}");
            stdout.WriteLine($"  CD-Text:        {(compilation.IncludeCdText ? "yes" : "no")}");
            stdout.WriteLine($"  Sectors sent:   {result.SectorsWritten:N0}");
            stdout.WriteLine($"  Duration:       {result.TotalTime}");
            if (nativeEngine?.LastLogPath != null)
            {
                stdout.WriteLine($"  Command log:    {nativeEngine.LastLogPath}");
            }
            return 0;
        }

        if (result.Success)
        {
            stdout.WriteLine("Audio CD burn completed successfully:");
            stdout.WriteLine($"  Drive:          {recorder.UniqueId}");
            stdout.WriteLine($"  Album Title:    {compilation.AlbumTitle}");
            stdout.WriteLine($"  Artist:         {compilation.AlbumPerformer}");
            stdout.WriteLine($"  Tracks Burned:  {compilation.TrackCount}");
            stdout.WriteLine($"  Total Sectors:  {result.SectorsWritten:N0}");
            stdout.WriteLine($"  Duration:       {result.TotalTime}");
            return 0;
        }

        stderr.WriteLine($"Audio burn failed: {result.ErrorMessage}");
        return 1;
    }

    private static async Task<int> HandleBurnDvdAsync(
        CommandLineArgs cmd,
        TextWriter stdout,
        TextWriter stderr,
        IDiscDeviceService deviceService,
        IBurnEngineBackend burnEngine)
    {
        string driveId = cmd.GetRequiredOption("drive", "--drive <id>");
        string? videoTsInput = cmd.GetOption("video-ts") ?? cmd.GetOption("input");
        if (string.IsNullOrWhiteSpace(videoTsInput))
        {
            throw new CommandLineException("Missing required option: --video-ts <folder-or-movie>");
        }
        string? volumeLabel = cmd.GetOption("label");

        var pathTokens = videoTsInput.Split(new[] { ';', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool allTokensExist = pathTokens.Length > 0 && pathTokens.All(p => File.Exists(p) || Directory.Exists(p));

        if (!File.Exists(videoTsInput) && !Directory.Exists(videoTsInput) && !allTokensExist)
        {
            throw new DirectoryNotFoundException($"VIDEO_TS directory or movie file does not exist: {videoTsInput}");
        }

        var recorder = FindRecorder(deviceService, driveId);
        if (recorder is VirtualDiscRecorder vRec)
        {
            var mediaStatus = vRec.QueryMediaStatus();
            if (!mediaStatus.MediaPresent || (!mediaStatus.IsBlank && !mediaStatus.IsAppendable && !mediaStatus.IsRewritable))
            {
                vRec.InsertMedia(VirtualDiscMedium.CreateBlankDvdR47());
            }
        }

        var analysis = DvdInputNormalizer.AnalyzeInput(videoTsInput);
        if (!analysis.CanAuthorOrBurn)
        {
            throw new InvalidOperationException($"Input cannot be authored or burned to DVD-Video: {string.Join("; ", analysis.NormalizationNotes)}");
        }

        string sourceRootDirectory;
        string? tempAuthorDir = null;
        string effectiveLabel = !string.IsNullOrWhiteSpace(volumeLabel) ? volumeLabel : (analysis.SuggestedVolumeLabel ?? "DVD_VIDEO");
        DataBurnResult result;

        try
        {
            if (analysis.RequiresAuthoring)
            {
                tempAuthorDir = Path.Combine(Path.GetTempPath(), "IgnisDisc_CliAuthor_" + Guid.NewGuid().ToString("N"));
                var authorService = new DvdVideoAuthoringService();
                var authorOptions = new DvdAuthoringOptions
                {
                    VolumeLabel = effectiveLabel,
                    AutoCreatePlayMenu = true
                };

                var authResult = await authorService.AuthorAsync(videoTsInput, tempAuthorDir, authorOptions).ConfigureAwait(false);
                if (!authResult.Success)
                {
                    throw new InvalidOperationException($"DVD-Video authoring failed: {authResult.ErrorMessage}");
                }

                sourceRootDirectory = authResult.OutputRootDirectory;
            }
            else
            {
                sourceRootDirectory = analysis.ExistingVideoTsPath ?? videoTsInput;
            }

            var root = CompilationDirectory.CreateRoot();
            CompilationDirectory videoTsDir;

            string folderName = Path.GetFileName(sourceRootDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.Equals(folderName, "VIDEO_TS", StringComparison.OrdinalIgnoreCase))
            {
                videoTsDir = root.AddDirectory("VIDEO_TS");
                PopulateDirectoryFromDisk(videoTsDir, sourceRootDirectory, skipDvdIgnoredFiles: true);

                string? parent = Path.GetDirectoryName(Path.GetFullPath(sourceRootDirectory));
                if (!string.IsNullOrEmpty(parent))
                {
                    string siblingAudioTs = Path.Combine(parent, "AUDIO_TS");
                    if (Directory.Exists(siblingAudioTs))
                    {
                        var audioTs = root.AddDirectory("AUDIO_TS");
                        PopulateDirectoryFromDisk(audioTs, siblingAudioTs, skipDvdIgnoredFiles: true);
                    }
                }
            }
            else
            {
                string childVideoTs = Path.Combine(sourceRootDirectory, "VIDEO_TS");
                if (Directory.Exists(childVideoTs))
                {
                    videoTsDir = root.AddDirectory("VIDEO_TS");
                    PopulateDirectoryFromDisk(videoTsDir, childVideoTs, skipDvdIgnoredFiles: true);

                    string childAudioTs = Path.Combine(sourceRootDirectory, "AUDIO_TS");
                    if (Directory.Exists(childAudioTs))
                    {
                        var audioTs = root.AddDirectory("AUDIO_TS");
                        PopulateDirectoryFromDisk(audioTs, childAudioTs, skipDvdIgnoredFiles: true);
                    }
                }
                else
                {
                    videoTsDir = root.AddDirectory("VIDEO_TS");
                    PopulateDirectoryFromDisk(videoTsDir, sourceRootDirectory, skipDvdIgnoredFiles: true);
                }
            }

            // Ensure AUDIO_TS is always added to root
            if (root.FindDirectoryByVirtualPath("/AUDIO_TS") == null)
            {
                root.AddDirectory("AUDIO_TS");
            }

            // Validate VIDEO_TS structure
            var validator = new DvdVideoValidator();
            bool hasAudioTs = root.FindDirectoryByVirtualPath("/AUDIO_TS") != null;
            var validation = validator.ValidateDirectory(videoTsDir, hasAudioTs);
            if (!validation.IsValid)
            {
                throw new InvalidOperationException($"DVD-Video structure validation failed: {string.Join("; ", validation.Errors)}");
            }

            // Align IFO and BUP files across 16-sector ECC boundaries
            var planner = new DvdVideoLayoutPlanner();
            planner.ApplyToCompilation(videoTsDir, startLba: 0);

            var buildOptions = new IsoBuildOptions(
                VolumeLabel: effectiveLabel,
                IncludeIso9660: true,
                IncludeUdf: true,
                IncludeJoliet: false,
                UdfRevision: 0x0102);

            var burnService = new DataBurnService(burnEngine);
            result = await burnService.BurnCompilationAsync(recorder, root, new DataBurnOptions { VolumeLabel = effectiveLabel, CloseDisc = true, OverwriteExistingData = cmd.HasFlag("overwrite") }, buildOptions).ConfigureAwait(false);
        }
        finally
        {
            if (tempAuthorDir != null && Directory.Exists(tempAuthorDir))
            {
                try { Directory.Delete(tempAuthorDir, recursive: true); } catch { }
            }
        }

        if (cmd.IsJson)
        {
            var jsonResult = new
            {
                success = result.Success,
                driveId = recorder.UniqueId,
                videoTsFolder = videoTsInput.Contains(';') || videoTsInput.Contains('|') ? videoTsInput : Path.GetFullPath(videoTsInput),
                volumeLabel = effectiveLabel,
                sectorsWritten = result.SectorsWritten,
                totalTime = result.TotalTime.ToString(),
                totalTimeSeconds = result.TotalTime.TotalSeconds,
                averageSpeedMultiplier = result.AverageSpeedMultiplier,
                error = result.ErrorMessage
            };

            stdout.WriteLine(JsonSerializer.Serialize(jsonResult, JsonOptions));
            return result.Success ? 0 : 1;
        }

        if (result.Success)
        {
            stdout.WriteLine("DVD-Video burned successfully:");
            stdout.WriteLine($"  Drive:          {recorder.UniqueId}");
            stdout.WriteLine($"  Volume Label:   {effectiveLabel}");
            stdout.WriteLine($"  Sectors Burned: {result.SectorsWritten:N0}");
            stdout.WriteLine($"  Duration:       {result.TotalTime}");
            return 0;
        }

        stderr.WriteLine($"DVD-Video burn failed: {result.ErrorMessage}");
        return 1;
    }

    private static async Task<int> HandleRipAsync(
        CommandLineArgs cmd,
        TextWriter stdout,
        TextWriter stderr,
        IDiscDeviceService deviceService,
        IBurnEngineBackend burnEngine)
    {
        string driveId = cmd.GetRequiredOption("drive", "--drive <id>");
        string outputPath = cmd.GetRequiredOption("output", "--output <file.iso|file.bin>");
        bool isRaw = cmd.HasFlag("raw") || outputPath.EndsWith(".bin", StringComparison.OrdinalIgnoreCase);

        var recorder = FindRecorder(deviceService, driveId);
        var ripper = new DiscRipperService();

        RipOperationResult result;
        string? companionCue = null;

        if (isRaw)
        {
            companionCue = Path.ChangeExtension(outputPath, ".cue");
            result = await ripper.RipToBinCueAsync(recorder, burnEngine, outputPath, companionCue).ConfigureAwait(false);
        }
        else
        {
            result = await ripper.RipToIsoAsync(recorder, burnEngine, outputPath).ConfigureAwait(false);
        }

        if (cmd.IsJson)
        {
            var jsonResult = new
            {
                success = result.Success,
                driveId = recorder.UniqueId,
                outputPath = Path.GetFullPath(outputPath),
                cuePath = companionCue != null ? Path.GetFullPath(companionCue) : null,
                rawMode = isRaw,
                sectorsRead = result.SectorsRead,
                badSectorsCount = result.BadSectorsCount,
                totalTime = result.TotalTime.ToString(),
                totalTimeSeconds = result.TotalTime.TotalSeconds,
                error = result.ErrorMessage
            };

            stdout.WriteLine(JsonSerializer.Serialize(jsonResult, JsonOptions));
            return result.Success ? 0 : 1;
        }

        if (result.Success)
        {
            stdout.WriteLine("Rip completed successfully:");
            stdout.WriteLine($"  Drive:          {recorder.UniqueId}");
            stdout.WriteLine($"  Output Path:    {Path.GetFullPath(outputPath)}");
            if (companionCue != null)
            {
                stdout.WriteLine($"  CUE Sheet:      {Path.GetFullPath(companionCue)}");
            }
            stdout.WriteLine($"  Mode:           {(isRaw ? "Raw 2352-byte BIN" : "Mode 1 2048-byte ISO")}");
            stdout.WriteLine($"  Sectors Read:   {result.SectorsRead:N0}");
            stdout.WriteLine($"  Bad Sectors:    {result.BadSectorsCount}");
            stdout.WriteLine($"  Duration:       {result.TotalTime}");
            return 0;
        }

        stderr.WriteLine($"Rip failed: {result.ErrorMessage}");
        return 1;
    }

    private static async Task<int> HandleRipAudioAsync(
        CommandLineArgs cmd,
        TextWriter stdout,
        TextWriter stderr,
        IDiscDeviceService deviceService,
        IBurnEngineBackend burnEngine)
    {
        string driveId = cmd.GetRequiredOption("drive", "--drive <id>");
        string outputFolder = cmd.GetRequiredOption("output", "--output <folder>");

        string formatStr = cmd.GetOption("format") ?? "wav";
        if (!string.Equals(formatStr, "wav", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(formatStr, "flac", StringComparison.OrdinalIgnoreCase))
        {
            throw new CommandLineException($"Invalid audio format '{formatStr}'. Supported formats are wav, flac.");
        }
        bool isFlac = string.Equals(formatStr, "flac", StringComparison.OrdinalIgnoreCase);

        string? ffmpegPath = null;
        if (isFlac)
        {
            ffmpegPath = FfmpegLocator.FindFfmpegExecutable();
            if (ffmpegPath == null)
            {
                throw new CommandLineException("FLAC output requires ffmpeg. Install ffmpeg or use --format wav.");
            }
        }

        List<int>? requestedTracks = null;
        string? tracksOpt = cmd.GetOption("tracks");
        if (!string.IsNullOrWhiteSpace(tracksOpt))
        {
            requestedTracks = new List<int>();
            var tokens = tracksOpt.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (tokens.Length == 0)
            {
                throw new CommandLineException("Invalid track list. Expected comma-separated positive integers (e.g. --tracks 1,3,5).");
            }
            foreach (var token in tokens)
            {
                if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int trackNum) || trackNum <= 0)
                {
                    throw new CommandLineException($"Invalid track number '{token}'. Track numbers must be positive integers.");
                }
                requestedTracks.Add(trackNum);
            }
        }

        int readOffset = 0;
        string? readOffsetOpt = cmd.GetOption("read-offset");
        if (readOffsetOpt != null &&
            (!int.TryParse(readOffsetOpt, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out readOffset) ||
             Math.Abs(readOffset) > 10_000))
        {
            throw new CommandLineException($"Invalid read offset '{readOffsetOpt}'. Expected an integer number of samples between -10000 and 10000 (e.g. --read-offset 6).");
        }

        var recorder = FindRecorder(deviceService, driveId);

        var ids = DiscIdCalculator.CalculateForRecorder(recorder);

        string? warning = null;
        CdTextAlbumMetadata? metadataOverride = null;

        if (cmd.HasFlag("lookup") && ids != null)
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                var mbClient = new MusicBrainzClient(http, MusicBrainzClient.DefaultUserAgent);
                var releases = await mbClient.LookupDiscIdAsync(ids.MusicBrainzId).ConfigureAwait(false);
                if (releases != null && releases.Count > 0)
                {
                    metadataOverride = MusicBrainzClient.ToAlbumMetadata(releases[0], ids.MusicBrainzId);
                }
            }
            catch (HttpRequestException hre)
            {
                warning = $"MusicBrainz lookup failed: {hre.Message}";
            }
            catch (TaskCanceledException tce)
            {
                warning = $"MusicBrainz lookup timed out: {tce.Message}";
            }
            catch (Exception ex)
            {
                warning = $"MusicBrainz lookup failed: {ex.Message}";
            }
        }

        string? coverPath = cmd.GetOption("cover") ?? cmd.GetOption("cover-art");
        byte[]? coverBytes = null;
        string? coverMime = null;
        if (!string.IsNullOrWhiteSpace(coverPath))
        {
            if (!File.Exists(coverPath))
            {
                // Fail before ripping rather than silently producing FLACs without the requested cover.
                throw new FileNotFoundException($"Cover image not found: '{coverPath}'.", coverPath);
            }

            coverBytes = await File.ReadAllBytesAsync(coverPath).ConfigureAwait(false);
            coverMime = FlacEncoder.DetectImageMimeType(coverBytes)
                ?? throw new InvalidDataException($"'{coverPath}' is not a JPEG, PNG, GIF or WebP image.");
        }

        var options = new AudioRipOptions
        {
            ContinueOnUnreadableSector = cmd.HasFlag("continue-on-error"),
            TrackNumbersToRip = requestedTracks,
            MetadataOverride = metadataOverride,
            ReadOffsetSamples = readOffset,
            SecureMode = cmd.HasFlag("secure"),
            CalculateReplayGain = !cmd.HasFlag("no-replaygain"),
            CoverArtBytes = coverBytes,
            CoverArtMimeType = coverMime
        };

        var ripper = new AudioCdRipperService();
        var result = await ripper.RipAudioCdAsync(recorder, burnEngine, outputFolder, options).ConfigureAwait(false);

        string metadataSource = metadataOverride != null
            ? "musicbrainz"
            : (result.CdText != null ? "cd-text" : "none");

        if (!result.Success)
        {
            if (cmd.IsJson)
            {
                var errObj = new
                {
                    success = false,
                    driveId = recorder.UniqueId,
                    outputFolder = Path.GetFullPath(outputFolder),
                    format = isFlac ? "FLAC" : "WAV",
                    tracksRipped = 0,
                    sectorsRead = 0,
                    totalTimeSeconds = 0.0,
                    musicBrainzId = ids?.MusicBrainzId,
                    freeDbId = ids?.FreeDbIdHex,
                    accurateRipPath = ids?.AccurateRipDatabasePath,
                    metadataSource = metadataSource,
                    warning = warning,
                    files = Array.Empty<string>(),
                    cueSheet = (string?)null,
                    error = result.ErrorMessage
                };
                stdout.WriteLine(JsonSerializer.Serialize(errObj, JsonOptions));
                return 1;
            }

            stderr.WriteLine($"Rip failed: {result.ErrorMessage}");
            return 1;
        }

        // AccurateRip: checksums were computed on the PCM during the rip, so this is independent of the output format.
        string? accurateRipStatus = null;
        var accurateRipTracks = new List<(AudioTrackRipInfo Info, AccurateRipTrackVerdict? Verdict)>();
        if (cmd.HasFlag("verify-accuraterip"))
        {
            if (ids == null)
            {
                accurateRipStatus = "unavailable";
            }
            else
            {
                try
                {
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                    var pressings = await AccurateRipDatabase.FetchAsync(http, ids).ConfigureAwait(false);
                    accurateRipStatus = pressings.Count == 0 ? "not-in-database" : "checked";
                    foreach (var info in result.TrackInfos)
                    {
                        accurateRipTracks.Add((info, pressings.Count == 0
                            ? null
                            : AccurateRipDatabase.Match(pressings, info.AudioTrackIndex, info.AccurateRipV1, info.AccurateRipV2)));
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException)
                {
                    accurateRipStatus = "failed";
                    warning = (warning == null ? "" : warning + " ") + $"AccurateRip lookup failed: {ex.Message}";
                }
            }

            if (readOffset == 0 && accurateRipStatus == "checked" && accurateRipTracks.Any(t => t.Verdict is { IsAccurate: false }))
            {
                warning = (warning == null ? "" : warning + " ") +
                    "No --read-offset was given; AccurateRip only matches when the drive's read offset is corrected (see the AccurateRip drive offset list).";
            }
        }

        var finalOutputFiles = new List<string>();
        if (isFlac)
        {
            int trackTotal = result.RippedFilePaths.Count;
            string albumTitle = !string.IsNullOrWhiteSpace(result.CdText?.AlbumTitle) ? result.CdText.AlbumTitle : "Unknown Album";
            string albumPerformer = !string.IsNullOrWhiteSpace(result.CdText?.AlbumPerformer) ? result.CdText.AlbumPerformer : "Unknown Artist";

            foreach (var info in result.TrackInfos)
            {
                string wavPath = info.FilePath;
                int trackNumber = info.TrackNumber;

                // The ripper already merged CD-Text, the MusicBrainz override and generic fallbacks per track.
                string trackTitle = !string.IsNullOrWhiteSpace(info.Title) ? info.Title : $"Track {trackNumber:D2}";
                string trackArtist = !string.IsNullOrWhiteSpace(info.Performer) && info.Performer != "Unknown Artist"
                    ? info.Performer
                    : albumPerformer;
                string? isrc = info.Isrc;

                string? trackGain = info.ReplayGainTrackGain.HasValue ? ReplayGainCalculator.FormatGainDb(info.ReplayGainTrackGain.Value) : null;
                string? trackPeak = info.ReplayGainTrackPeak.HasValue ? ReplayGainCalculator.FormatPeak(info.ReplayGainTrackPeak.Value) : null;
                string? albumGain = result.ReplayGainAlbumGain.HasValue ? ReplayGainCalculator.FormatGainDb(result.ReplayGainAlbumGain.Value) : null;
                string? albumPeak = result.ReplayGainAlbumPeak.HasValue ? ReplayGainCalculator.FormatPeak(result.ReplayGainAlbumPeak.Value) : null;

                var tags = new AudioTrackTags(
                    Title: trackTitle,
                    Artist: trackArtist,
                    Album: albumTitle,
                    TrackNumber: trackNumber,
                    TrackTotal: trackTotal,
                    Isrc: isrc,
                    Date: null,
                    ReplayGainTrackGain: trackGain,
                    ReplayGainTrackPeak: trackPeak,
                    ReplayGainAlbumGain: albumGain,
                    ReplayGainAlbumPeak: albumPeak,
                    CoverArtBytes: options.CoverArtBytes,
                    CoverArtMimeType: options.CoverArtMimeType);

                string flacPath = Path.ChangeExtension(wavPath, ".flac");
                await FlacEncoder.EncodeAsync(ffmpegPath!, wavPath, flacPath, tags).ConfigureAwait(false);

                if (options.CoverArtBytes is { Length: > 0 })
                {
                    FlacEncoder.EmbedCoverArtAndReplayGain(flacPath, tags);
                }

                if (!cmd.HasFlag("keep-wav"))
                {
                    try
                    {
                        File.Delete(wavPath);
                    }
                    catch
                    {
                    }
                }

                finalOutputFiles.Add(flacPath);
            }

            if (result.CueSheetPath != null && File.Exists(result.CueSheetPath))
            {
                string cueContent = await File.ReadAllTextAsync(result.CueSheetPath).ConfigureAwait(false);
                var cueLines = cueContent.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
                var updatedLines = new List<string>(cueLines.Length);
                var cueFileRegex = new Regex(@"^FILE\s+""(?<name>.+?)\.wav""\s+WAVE$", RegexOptions.IgnoreCase);

                foreach (var line in cueLines)
                {
                    var match = cueFileRegex.Match(line.TrimStart());
                    if (match.Success)
                    {
                        int leadingSpaces = line.Length - line.TrimStart().Length;
                        string prefix = line.Substring(0, leadingSpaces);
                        updatedLines.Add($"{prefix}FILE \"{match.Groups["name"].Value}.flac\" WAVE");
                    }
                    else
                    {
                        updatedLines.Add(line);
                    }
                }

                string separator = cueContent.Contains("\r\n") ? "\r\n" : "\n";
                await File.WriteAllTextAsync(result.CueSheetPath, string.Join(separator, updatedLines)).ConfigureAwait(false);
            }
        }
        else
        {
            finalOutputFiles.AddRange(result.RippedFilePaths);
        }

        if (cmd.IsJson)
        {
            var jsonResult = new
            {
                success = true,
                driveId = recorder.UniqueId,
                outputFolder = Path.GetFullPath(outputFolder),
                format = isFlac ? "FLAC" : "WAV",
                tracksRipped = result.TracksRipped,
                sectorsRead = result.TotalSectorsRead,
                totalTimeSeconds = result.TotalTime.TotalSeconds,
                musicBrainzId = ids?.MusicBrainzId,
                freeDbId = ids?.FreeDbIdHex,
                accurateRipPath = ids?.AccurateRipDatabasePath,
                metadataSource = metadataSource,
                warning = warning,
                files = finalOutputFiles.Select(Path.GetFullPath).ToList(),
                cueSheet = result.CueSheetPath != null && File.Exists(result.CueSheetPath) ? Path.GetFullPath(result.CueSheetPath) : null,
                readOffset,
                badSectors = result.BadSectorLbas,
                trackChecksums = result.TrackInfos.Select(t => new
                {
                    trackNumber = t.TrackNumber,
                    crc32 = t.Crc32.ToString("X8"),
                    accurateRipV1 = t.AccurateRipV1.ToString("x8"),
                    accurateRipV2 = t.AccurateRipV2.ToString("x8"),
                    unreadableSectors = t.UnreadableSectors,
                    recoveredSectors = t.RecoveredSectors,
                    suspectSectors = t.SuspectSectors
                }).ToList(),
                secureMode = !cmd.HasFlag("secure") ? "off" : result.SecureModeUsed ? "c2" : "unsupported",
                accurateRip = accurateRipStatus == null ? null : new
                {
                    status = accurateRipStatus,
                    tracks = accurateRipTracks.Select(t => new
                    {
                        trackNumber = t.Info.TrackNumber,
                        accurate = t.Verdict?.IsAccurate,
                        confidence = t.Verdict?.Confidence,
                        totalSubmissions = t.Verdict?.TotalSubmissions,
                        matchedV2 = t.Verdict?.MatchedV2
                    }).ToList()
                },
                error = (string?)null
            };

            stdout.WriteLine(JsonSerializer.Serialize(jsonResult, JsonOptions));
            return 0;
        }

        stdout.WriteLine("Audio rip completed successfully:");
        stdout.WriteLine($"  Drive:            {recorder.UniqueId}");
        stdout.WriteLine($"  Output Folder:    {Path.GetFullPath(outputFolder)}");
        stdout.WriteLine($"  Format:           {(isFlac ? "FLAC" : "WAV")}");
        stdout.WriteLine($"  Tracks Ripped:    {result.TracksRipped}");
        stdout.WriteLine($"  Sectors Read:     {result.TotalSectorsRead:N0}");
        stdout.WriteLine($"  Duration:         {result.TotalTime}");
        stdout.WriteLine($"  MusicBrainz ID:   {ids?.MusicBrainzId ?? "n/a"}");
        stdout.WriteLine($"  freedb ID:        {ids?.FreeDbIdHex ?? "n/a"}");
        stdout.WriteLine($"  AccurateRip:      {ids?.AccurateRipDatabasePath ?? "n/a"}");
        stdout.WriteLine($"  Metadata Source:  {metadataSource}");
        stdout.WriteLine($"  Read Offset:      {readOffset:+0;-0;0} samples");
        if (cmd.HasFlag("secure"))
        {
            stdout.WriteLine(result.SecureModeUsed
                ? $"  Secure Mode:      C2 error pointers ({result.TrackInfos.Sum(t => t.RecoveredSectors)} sectors recovered, {result.TrackInfos.Sum(t => t.SuspectSectors)} still suspect)"
                : "  Secure Mode:      requested, but the drive does not report C2 error pointers (plain read used)");
            foreach (var t in result.TrackInfos.Where(t => t.SuspectSectors > 0))
            {
                stdout.WriteLine($"    Track {t.TrackNumber:D2}: {t.SuspectSectors} suspect sector(s) - check AccurateRip or re-rip");
            }
        }
        if (result.BadSectorLbas.Count > 0)
        {
            stdout.WriteLine($"  Bad Sectors:      {result.BadSectorLbas.Count} (zero-filled)");
        }
        if (accurateRipStatus != null)
        {
            stdout.WriteLine($"  AccurateRip:      {accurateRipStatus}");
            foreach (var (info, verdict) in accurateRipTracks)
            {
                string line = verdict == null
                    ? "not in database"
                    : verdict.IsAccurate
                        ? $"accurate (confidence {verdict.Confidence}/{verdict.TotalSubmissions}, {(verdict.MatchedV2 ? "v2" : "v1")})"
                        : $"NOT accurate (0/{verdict.TotalSubmissions} submissions match)";
                stdout.WriteLine($"    Track {info.TrackNumber:D2}: {line}  [v1 {info.AccurateRipV1:x8} v2 {info.AccurateRipV2:x8}]");
            }
        }
        if (!string.IsNullOrEmpty(warning))
        {
            stdout.WriteLine($"  Warning:          {warning}");
        }
        stdout.WriteLine("  Files:");
        foreach (var file in finalOutputFiles)
        {
            stdout.WriteLine($"    {Path.GetFullPath(file)}");
        }
        if (!string.IsNullOrEmpty(result.CueSheetPath) && File.Exists(result.CueSheetPath))
        {
            stdout.WriteLine($"  CUE Sheet:        {Path.GetFullPath(result.CueSheetPath)}");
        }

        return 0;
    }

    private static async Task<int> HandleDetectOffsetAsync(
        CommandLineArgs cmd,
        TextWriter stdout,
        TextWriter stderr,
        IDiscDeviceService deviceService,
        IBurnEngineBackend burnEngine)
    {
        string driveId = cmd.GetRequiredOption("drive", "--drive <id>");
        var recorder = FindRecorder(deviceService, driveId);

        var tracks = AudioCdRipperService.QueryTracksAndCdText(recorder).Tracks;
        var ids = DiscIdCalculator.CalculateForRecorder(recorder);
        if (ids == null || tracks.Count == 0)
        {
            throw new CommandLineException("No audio tracks found. Insert an audio CD to detect the drive's read offset.");
        }

        IReadOnlyList<AccurateRipPressing> pressings;
        using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) })
        {
            pressings = await AccurateRipDatabase.FetchAsync(http, ids).ConfigureAwait(false);
        }

        if (pressings.Count == 0)
        {
            throw new CommandLineException(
                "This disc is not in the AccurateRip database, so it cannot be used to detect the offset. Try a well-known commercial CD.");
        }

        var detection = await ReadOffsetDetector.DetectAsync(burnEngine, recorder, tracks, pressings).ConfigureAwait(false);
        var best = detection.Best;

        if (cmd.IsJson)
        {
            stdout.WriteLine(JsonSerializer.Serialize(new
            {
                success = best != null,
                driveId = recorder.UniqueId,
                readOffset = best?.OffsetSamples,
                confidence = best?.Confidence,
                probedTracks = detection.ProbedTracks,
                candidates = detection.Candidates.Select(c => new { offset = c.OffsetSamples, c.Confidence, c.TrackNumber }),
                accurateRipPath = ids.AccurateRipDatabasePath,
                error = best == null ? (detection.ErrorMessage ?? "No offset within +/-3000 samples matched the AccurateRip database.") : null
            }, JsonOptions));
            return best != null ? 0 : 1;
        }

        if (best == null)
        {
            stderr.WriteLine($"Offset detection failed: {detection.ErrorMessage ?? "No offset within +/-3000 samples matched the AccurateRip database."}");
            return 1;
        }

        stdout.WriteLine("Read offset detected:");
        stdout.WriteLine($"  Drive:          {recorder.UniqueId}");
        stdout.WriteLine($"  Read Offset:    {best.OffsetSamples:+0;-0;0} samples");
        stdout.WriteLine($"  Confidence:     {best.Confidence} matching submissions");
        stdout.WriteLine($"  Tracks Checked: {string.Join(", ", detection.ProbedTracks)}");
        stdout.WriteLine($"  Use it with:    rip-audio --drive {driveId} --read-offset {best.OffsetSamples:+0;-0;0} --verify-accuraterip ...");
        return 0;
    }

    private static async Task<int> HandleArchiveStageAsync(CommandLineArgs cmd, TextWriter stdout)
    {
        string input = cmd.GetRequiredOption("input", "--input <folder>");
        string? staging = cmd.GetOption("staging");
        bool planOnly = cmd.HasFlag("plan-only");
        if (!planOnly && string.IsNullOrWhiteSpace(staging))
        {
            throw new CommandLineException("Missing required option --staging <folder> (or use --plan-only).");
        }

        DiscMediaType media = (cmd.GetOption("media") ?? "dvd").ToLowerInvariant() switch
        {
            "cd" => DiscMediaType.CdR,
            "dvd" => DiscMediaType.DvdPlusR,                // the smaller single-layer capacity, safe for -R and +R
            "dvd-dl" => DiscMediaType.DvdMinusRDualLayer,   // the smaller dual-layer capacity
            var other => throw new CommandLineException($"Invalid media '{other}'. Supported: cd, dvd, dvd-dl.")
        };

        string redundancyOpt = cmd.GetOption("redundancy") ?? "10";
        if (!int.TryParse(redundancyOpt, NumberStyles.Integer, CultureInfo.InvariantCulture, out int redundancy) || redundancy is < 0 or > 100)
        {
            throw new CommandLineException($"Invalid redundancy '{redundancyOpt}'. Expected a percentage between 0 and 100.");
        }

        var plan = ArchiveSpanPlanner.Plan(input, media, redundancy);

        string? par2Path = null;
        if (!planOnly && redundancy > 0)
        {
            par2Path = Par2Runner.FindPar2Executable()
                ?? throw new CommandLineException(
                    "PAR2 recovery data needs par2cmdline (par2.exe on PATH). Install it, or pass --redundancy 0 for manifests only.");
        }

        IReadOnlyList<string> stagedDiscs = Array.Empty<string>();
        if (!planOnly)
        {
            var progress = cmd.IsJson ? null : new Progress<string>(message => stdout.WriteLine($"  {message}"));
            stagedDiscs = await ArchiveStager.StageAsync(plan, staging!, par2Path, progress).ConfigureAwait(false);
        }

        if (cmd.IsJson)
        {
            stdout.WriteLine(JsonSerializer.Serialize(new
            {
                success = true,
                setId = plan.SetId,
                media = media.ToString(),
                redundancyPercent = plan.RedundancyPercent,
                usableSectorsPerDisc = plan.UsableSectorsPerDisc,
                totalBytes = plan.TotalBytes,
                discs = plan.Discs.Select(d => new
                {
                    discNumber = d.DiscNumber,
                    files = d.Files.Count,
                    dataBytes = d.DataBytes,
                    path = stagedDiscs.Count >= d.DiscNumber ? stagedDiscs[d.DiscNumber - 1] : null
                }),
                planOnly
            }, JsonOptions));
            return 0;
        }

        stdout.WriteLine(planOnly ? "Archive plan:" : "Archive staged successfully:");
        stdout.WriteLine($"  Set ID:           {plan.SetId}");
        // --media plans for the smaller of the -R/+R capacities, so the plan fits either kind of disc.
        string mediaName = media switch
        {
            DiscMediaType.CdR => "CD-R (700 MB)",
            DiscMediaType.DvdPlusR => "DVD±R (4.7 GB)",
            DiscMediaType.DvdMinusRDualLayer => "DVD±R DL (8.5 GB)",
            _ => media.GetDisplayName()
        };
        stdout.WriteLine($"  Media:            {mediaName}");
        stdout.WriteLine($"  Redundancy:       {plan.RedundancyPercent}% PAR2");
        stdout.WriteLine($"  Total Data:       {plan.TotalBytes / 1048576.0:N1} MB in {plan.Discs.Sum(d => d.Files.Count):N0} files");
        stdout.WriteLine($"  Discs:            {plan.Discs.Count}");
        foreach (var d in plan.Discs)
        {
            string where = stagedDiscs.Count >= d.DiscNumber ? $"  -> {stagedDiscs[d.DiscNumber - 1]}" : string.Empty;
            stdout.WriteLine($"    Disc {d.DiscNumber:D2}: {d.Files.Count:N0} files, {d.DataBytes / 1048576.0:N1} MB{where}");
        }
        if (!planOnly && stagedDiscs.Count > 0)
        {
            stdout.WriteLine($"  Burn each disc with: burn-data --drive <id> --input \"{stagedDiscs[0]}\" --udf --verify");
            stdout.WriteLine("  Check a burned disc later with: archive-verify --path <drive letter>");
        }
        return 0;
    }

    private static async Task<int> HandleArchiveVerifyAsync(CommandLineArgs cmd, TextWriter stdout)
    {
        string path = cmd.GetRequiredOption("path", "--path <folder|drive>");
        if (path.Length == 2 && path[1] == ':')
        {
            path += "\\";
        }

        var progress = cmd.IsJson ? null : new Progress<string>(_ => { });
        var result = await ArchiveManifest.VerifyAsync(path, progress).ConfigureAwait(false);

        if (cmd.IsJson)
        {
            stdout.WriteLine(JsonSerializer.Serialize(new
            {
                success = result.IsIntact,
                setId = result.SetId,
                discNumber = result.DiscNumber,
                discCount = result.DiscCount,
                filesChecked = result.FilesChecked,
                missing = result.Missing,
                corrupt = result.Corrupt,
                hasRecoveryData = result.HasRecoveryData
            }, JsonOptions));
            return result.IsIntact ? 0 : 1;
        }

        stdout.WriteLine(result.IsIntact ? "Archive disc is intact:" : "Archive disc has PROBLEMS:");
        if (result.SetId != null)
        {
            stdout.WriteLine($"  Set ID:           {result.SetId} (disc {result.DiscNumber} of {result.DiscCount})");
        }
        stdout.WriteLine($"  Files Checked:    {result.FilesChecked:N0}");
        stdout.WriteLine($"  Missing:          {result.Missing.Count}");
        stdout.WriteLine($"  Corrupt:          {result.Corrupt.Count}");
        stdout.WriteLine($"  Recovery Data:    {(result.HasRecoveryData ? "present (PAR2)" : "none")}");
        foreach (var m in result.Missing) stdout.WriteLine($"    MISSING  {m}");
        foreach (var c in result.Corrupt) stdout.WriteLine($"    CORRUPT  {c}");
        if (!result.IsIntact && result.HasRecoveryData)
        {
            // -B. is required: the recovery set stores paths relative to the disc root, not to the RECOVERY folder.
            stdout.WriteLine("  To repair: copy the disc to a folder, open a terminal in that folder and run:");
            stdout.WriteLine("    par2 repair -B. RECOVERY\\recovery.par2");
            stdout.WriteLine("  (par2 keeps each damaged original as <name>.1; delete those once verified.)");
        }
        return result.IsIntact ? 0 : 1;
    }

    private static async Task<int> HandleVerifyAsync(
        CommandLineArgs cmd,
        TextWriter stdout,
        TextWriter stderr,
        IDiscDeviceService deviceService,
        IBurnEngineBackend burnEngine)
    {
        string driveId = cmd.GetRequiredOption("drive", "--drive <id>");
        string isoPath = cmd.GetRequiredOption("iso", "--iso <file.iso>");

        if (!File.Exists(isoPath))
        {
            throw new FileNotFoundException($"ISO image file not found: {isoPath}", isoPath);
        }

        var recorder = FindRecorder(deviceService, driveId);
        var verifier = new DiscVerificationService();

        VerificationResult result;
        if (LegacyDiscImageConverter.IsLegacyImageFile(isoPath))
        {
            await using var legacyStream = LegacyDiscImageConverter.OpenIsoUserDataStream(isoPath);
            result = await verifier.VerifyDiscAgainstStreamAsync(recorder, burnEngine, legacyStream, raw2352Mode: false).ConfigureAwait(false);
        }
        else
        {
            result = await verifier.VerifyDiscAgainstIsoFileAsync(recorder, burnEngine, isoPath).ConfigureAwait(false);
        }

        if (cmd.IsJson)
        {
            var jsonResult = new
            {
                success = result.IsMatch,
                isMatch = result.IsMatch,
                driveId = recorder.UniqueId,
                isoPath = Path.GetFullPath(isoPath),
                totalSectorsVerified = result.TotalSectorsVerified,
                mismatchedLbaCount = result.MismatchedLbas.Count,
                mismatchedLbas = result.MismatchedLbas,
                corruptedFiles = result.CorruptedFiles,
                expectedSha256 = result.ExpectedSha256,
                actualSha256 = result.ActualSha256,
                expectedCrc32 = result.ExpectedCrc32,
                actualCrc32 = result.ActualCrc32,
                totalTime = result.TotalTime.ToString(),
                error = result.ErrorMessage
            };

            stdout.WriteLine(JsonSerializer.Serialize(jsonResult, JsonOptions));
            return result.IsMatch ? 0 : 1;
        }

        if (result.IsMatch)
        {
            stdout.WriteLine("Disc verification PASSED:");
            stdout.WriteLine($"  Drive:                   {recorder.UniqueId}");
            stdout.WriteLine($"  ISO File:                {Path.GetFullPath(isoPath)}");
            stdout.WriteLine($"  Total Sectors Verified:  {result.TotalSectorsVerified:N0}");
            stdout.WriteLine($"  Expected SHA-256:        {result.ExpectedSha256}");
            stdout.WriteLine($"  Actual SHA-256:          {result.ActualSha256}");
            stdout.WriteLine($"  Duration:                {result.TotalTime}");
            return 0;
        }

        stderr.WriteLine("Disc verification FAILED:");
        stderr.WriteLine($"  Mismatches:              {result.MismatchedLbas.Count} sector(s)");
        if (result.CorruptedFiles.Count > 0)
        {
            stderr.WriteLine($"  Corrupted Files:         {string.Join(", ", result.CorruptedFiles)}");
        }
        stderr.WriteLine($"  Expected SHA-256:        {result.ExpectedSha256}");
        stderr.WriteLine($"  Actual SHA-256:          {result.ActualSha256}");
        return 1;
    }

    private static async Task<int> HandleEraseAsync(
        CommandLineArgs cmd,
        TextWriter stdout,
        TextWriter stderr,
        IDiscDeviceService deviceService,
        IBurnEngineBackend burnEngine)
    {
        string driveId = cmd.GetRequiredOption("drive", "--drive <id>");
        bool fullErase = cmd.HasFlag("full");

        var recorder = FindRecorder(deviceService, driveId);
        var eraseService = new DiscEraseService(burnEngine);

        var result = await eraseService.EraseAsync(recorder, burnEngine, fullErase).ConfigureAwait(false);

        if (cmd.IsJson)
        {
            var jsonResult = new
            {
                success = result.Success,
                driveId = recorder.UniqueId,
                fullErase = fullErase,
                totalTime = result.TotalTime.ToString(),
                totalTimeSeconds = result.TotalTime.TotalSeconds,
                error = result.ErrorMessage
            };

            stdout.WriteLine(JsonSerializer.Serialize(jsonResult, JsonOptions));
            return result.Success ? 0 : 1;
        }

        if (result.Success)
        {
            stdout.WriteLine("Disc erased successfully:");
            stdout.WriteLine($"  Drive:      {recorder.UniqueId}");
            stdout.WriteLine($"  Mode:       {(fullErase ? "Full erase" : "Quick erase")}");
            stdout.WriteLine($"  Duration:   {result.TotalTime}");
            return 0;
        }

        stderr.WriteLine($"Erase failed: {result.ErrorMessage}");
        return 1;
    }

    private static async Task<int> HandleScanAsync(
        CommandLineArgs cmd,
        TextWriter stdout,
        TextWriter stderr,
        IDiscDeviceService deviceService,
        IBurnEngineBackend burnEngine)
    {
        string driveId = cmd.GetRequiredOption("drive", "--drive <id>");
        string? blockSizeStr = cmd.GetOption("block-size");
        string? csvOutputPath = cmd.GetOption("csv") ?? cmd.GetOption("output");

        int blockSize = 256;
        if (!string.IsNullOrWhiteSpace(blockSizeStr) && int.TryParse(blockSizeStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int bs) && bs > 0)
        {
            blockSize = bs;
        }

        var recorder = FindRecorder(deviceService, driveId);
        var scanService = new SurfaceScanService(burnEngine);
        var scanOptions = new SurfaceScanOptions { BlockSizeSectors = blockSize };

        var report = await scanService.ScanAsync(recorder, burnEngine, scanOptions).ConfigureAwait(false);

        string? fullCsvPath = null;
        if (!string.IsNullOrWhiteSpace(csvOutputPath))
        {
            fullCsvPath = Path.GetFullPath(csvOutputPath);
            await report.ExportCsvAsync(fullCsvPath).ConfigureAwait(false);
        }

        if (cmd.IsJson)
        {
            var jsonResult = new
            {
                success = true,
                driveId = recorder.UniqueId,
                totalSectorsScanned = report.TotalSectorsScanned,
                goodBlocks = report.GoodBlocks,
                slowBlocks = report.SlowBlocks,
                damagedBlocks = report.DamagedBlocks,
                minSpeedKbPerSec = report.MinSpeedKbPerSec,
                maxSpeedKbPerSec = report.MaxSpeedKbPerSec,
                averageSpeedKbPerSec = report.AverageSpeedKbPerSec,
                csvPath = fullCsvPath
            };

            stdout.WriteLine(JsonSerializer.Serialize(jsonResult, JsonOptions));
            return report.DamagedBlocks == 0 ? 0 : 1;
        }

        stdout.WriteLine($"Surface scan report for '{recorder.UniqueId}':");
        stdout.WriteLine($"  Total Sectors:      {report.TotalSectorsScanned:N0}");
        stdout.WriteLine($"  Good Blocks:        {report.GoodBlocks:N0}");
        stdout.WriteLine($"  Slow Blocks:        {report.SlowBlocks:N0}");
        stdout.WriteLine($"  Damaged Blocks:     {report.DamagedBlocks:N0}");
        stdout.WriteLine($"  Min Read Rate:      {report.MinSpeedKbPerSec:F1} KiB/s");
        stdout.WriteLine($"  Max Read Rate:      {report.MaxSpeedKbPerSec:F1} KiB/s");
        stdout.WriteLine($"  Average Read Rate:  {report.AverageSpeedKbPerSec:F1} KiB/s");
        if (fullCsvPath != null)
        {
            stdout.WriteLine($"  CSV Export:         {fullCsvPath}");
        }

        return report.DamagedBlocks == 0 ? 0 : 1;
    }

    #endregion

    #region Helper Methods

    private static IDiscRecorderBackend FindRecorder(IDiscDeviceService deviceService, string driveId)
    {
        var recorder = deviceService.GetRecorderById(driveId)
            ?? deviceService.GetRecorders(includeVirtual: true).FirstOrDefault(r =>
                string.Equals(r.UniqueId, driveId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(r.VolumePath?.TrimEnd('\\'), driveId.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));

        if (recorder == null)
        {
            throw new CommandLineException($"Drive '{driveId}' not found. Use 'drives' command to list available drives.");
        }

        return recorder;
    }

    private static WriteSpeedDescriptor? ParseSpeed(string? speedStr, DiscMediaType mediaType, bool isAudio = false)
    {
        if (string.IsNullOrWhiteSpace(speedStr)) return null;
        string cleaned = speedStr.TrimEnd('x', 'X').Trim();
        if (double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out double multiplier) && multiplier > 0)
        {
            return WriteSpeedDescriptor.FromMultiplier(multiplier, mediaType, isAudio);
        }
        return null;
    }

    private static ElToritoBootConfiguration? BuildBootConfiguration(string? bootBiosPath, string? bootUefiPath, string? developerId)
    {
        BootCatalogEntrySpec? biosEntry = null;
        if (!string.IsNullOrWhiteSpace(bootBiosPath))
        {
            if (!File.Exists(bootBiosPath))
            {
                throw new FileNotFoundException($"BIOS boot image file not found: {bootBiosPath}", bootBiosPath);
            }
            var fi = new FileInfo(bootBiosPath);
            ushort sectors = (ushort)Math.Max(1, (fi.Length + 511) / 512);
            biosEntry = new BootCatalogEntrySpec(
                Platform: BootPlatformId.X86Bios,
                Emulation: BootEmulationType.NoEmulation,
                LoadSegment: 0,
                SectorCount: sectors,
                BootImageStreamFactory: () => new FileStream(bootBiosPath, FileMode.Open, FileAccess.Read, FileShare.Read),
                Name: Path.GetFileName(bootBiosPath));
        }

        BootCatalogEntrySpec? uefiEntry = null;
        if (!string.IsNullOrWhiteSpace(bootUefiPath))
        {
            if (!File.Exists(bootUefiPath))
            {
                throw new FileNotFoundException($"UEFI boot image file not found: {bootUefiPath}", bootUefiPath);
            }
            var fi = new FileInfo(bootUefiPath);
            ushort sectors = (ushort)Math.Max(1, (fi.Length + 511) / 512);
            uefiEntry = new BootCatalogEntrySpec(
                Platform: BootPlatformId.Efi,
                Emulation: BootEmulationType.NoEmulation,
                LoadSegment: 0,
                SectorCount: sectors,
                BootImageStreamFactory: () => new FileStream(bootUefiPath, FileMode.Open, FileAccess.Read, FileShare.Read),
                Name: Path.GetFileName(bootUefiPath));
        }

        string devId = !string.IsNullOrWhiteSpace(developerId) ? developerId : "CDBURNER";

        if (biosEntry != null && uefiEntry != null)
        {
            return new ElToritoBootConfiguration(
                DefaultEntry: biosEntry,
                AdditionalSectionEntries: new[] { uefiEntry },
                DeveloperId: devId);
        }

        if (biosEntry != null)
        {
            return new ElToritoBootConfiguration(
                DefaultEntry: biosEntry,
                DeveloperId: devId);
        }

        if (uefiEntry != null)
        {
            return new ElToritoBootConfiguration(
                DefaultEntry: uefiEntry,
                DeveloperId: devId);
        }

        return null;
    }

    private static void PopulateDirectoryFromDisk(CompilationDirectory target, string sourceDiskDir, bool skipDvdIgnoredFiles = false)
    {
        CompilationFolderImporter.Import(target, sourceDiskDir, strict: false,
            skipDvdIgnoredFiles ? DvdInputNormalizer.IsIgnoredUselessFile : null);
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
        {
            return $"{(double)bytes / (1024 * 1024 * 1024):F2} GB";
        }
        if (bytes >= 1024 * 1024)
        {
            return $"{(double)bytes / (1024 * 1024):F1} MB";
        }
        if (bytes >= 1024)
        {
            return $"{(double)bytes / 1024:F1} KB";
        }
        return $"{bytes} B";
    }

    private static void PrintHelp(TextWriter stdout)
    {
        stdout.WriteLine("IgnisDisc Optical CLI (ignisdisc-cli.exe / cdburner-cli.exe) — ignisdisc.tetherworks.dev — Free & Open Source (GPLv3)");
        stdout.WriteLine("Usage: ignisdisc-cli <command> [options]");
        stdout.WriteLine();
        stdout.WriteLine("Supported Commands:");
        stdout.WriteLine("  drives [--json]");
        stdout.WriteLine("      Lists physical and virtual optical drives and capabilities.");
        stdout.WriteLine("  drive-report --drive <id> [--json]");
        stdout.WriteLine("      Generates a Markdown compatibility report matching GitHub issue template.");
        stdout.WriteLine("  media-info --drive <id> [--json]");
        stdout.WriteLine("      Shows inserted disc status, capacity, sessions, speeds.");
        stdout.WriteLine("  build-iso --input <folder> --output <file.iso> [--label <vol>] [--udf] [--joliet] [--boot-bios <img>] [--boot-uefi <img>] [--json]");
        stdout.WriteLine("      Compiles a folder into an ISO 9660 / Joliet / UDF filesystem image.");
        stdout.WriteLine("  burn-iso --drive <id> --iso <file.iso|.nrg|.mdf|.mds|.ccd|.img> [--speed <X>] [--verify] [--no-close] [--overwrite] [--json]");
        stdout.WriteLine("      Burns an ISO image directly to the target optical drive.");
        stdout.WriteLine("  burn-data --drive <id> --input <folder> [--label <vol>] [--speed <X>] [--udf] [--joliet] [--boot-bios <img>] [--boot-uefi <img>] [--verify] [--no-close] [--overwrite] [--json]");
        stdout.WriteLine("      Compiles a folder and burns it to optical media.");
        stdout.WriteLine("  burn-audio --drive <id> --tracks <file1,file2,...> [--title <album>] [--artist <name>] [--speed <X>] [--dao] [--native --test-write] [--json]");
        stdout.WriteLine("      Burns Red Book Audio CD from WAV, FLAC, MP3, AAC/M4A, or WMA tracks.");
        stdout.WriteLine("  burn-dvd --drive <id> --video-ts <folder|movie> [--label <vol>] [--overwrite] [--json]");
        stdout.WriteLine("      Validates, normalizes/authors, aligns, and burns a DVD-Video compliant disc from VIDEO_TS or movie files.");
        stdout.WriteLine("  rip --drive <id> --output <file.iso|file.bin> [--raw] [--json]");
        stdout.WriteLine("      Rips optical disc to an ISO or raw BIN+CUE image.");
        stdout.WriteLine("  rip-audio --drive <id> --output <folder> [--format wav|flac] [--tracks 1,3,5] [--lookup] [--keep-wav] [--continue-on-error] [--read-offset <samples>] [--verify-accuraterip] [--secure] [--json]");
        stdout.WriteLine("      Rips Red Book Audio CD to WAV or FLAC with CD-Text and optional MusicBrainz lookup.");
        stdout.WriteLine("  detect-offset --drive <id> [--json]");
        stdout.WriteLine("      Finds the drive's audio read offset using an inserted CD that is in the AccurateRip database.");
        stdout.WriteLine("  archive-stage --input <folder> (--staging <folder> | --plan-only) [--media cd|dvd|dvd-dl] [--redundancy <0-100>] [--json]");
        stdout.WriteLine("      Splits a folder across discs and stages disc01..discNN with SHA-256 manifests and PAR2 recovery data.");
        stdout.WriteLine("  archive-verify --path <folder|drive> [--json]");
        stdout.WriteLine("      Checks an archive disc against its SHA-256 manifest.");
        stdout.WriteLine("  verify --drive <id> --iso <file.iso|.nrg|.mdf|.mds|.ccd|.img> [--json]");
        stdout.WriteLine("      Verifies disc sectors against an ISO image with SHA-256 and defect mapping.");
        stdout.WriteLine("  erase --drive <id> [--full] [--json]");
        stdout.WriteLine("      Erases rewritable optical media (CD-RW, DVD-RW, DVD+RW, DVD-RAM).");
        stdout.WriteLine("  scan --drive <id> [--block-size <sectors>] [--csv <file.csv>] [--json]");
        stdout.WriteLine("      Scans optical disc surface and benchmarks transfer rate curve.");
        stdout.WriteLine();
        stdout.WriteLine("General Options:");
        stdout.WriteLine("  --help, -h       Show help information.");
        stdout.WriteLine("  --version, -v    Show version information.");
        stdout.WriteLine("  --json           Format output as machine-readable JSON.");
    }

    private static void PrintSubcommandHelp(string? subcommand, TextWriter stdout)
    {
        if (string.Equals(subcommand, "drive-report", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(subcommand, "drivereport", StringComparison.OrdinalIgnoreCase))
        {
            stdout.WriteLine("  drive-report --drive <id> [--json]");
            stdout.WriteLine("      Generates a Markdown compatibility report matching GitHub issue template.");
            return;
        }
        if (string.Equals(subcommand, "rip-audio", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(subcommand, "ripaudio", StringComparison.OrdinalIgnoreCase))
        {
            stdout.WriteLine("  rip-audio --drive <id> --output <folder> [--format wav|flac] [--tracks 1,3,5] [--lookup] [--keep-wav] [--continue-on-error] [--read-offset <samples>] [--verify-accuraterip] [--secure] [--cover <image>] [--no-replaygain] [--json]");
            stdout.WriteLine("      Rips Red Book Audio CD to WAV or FLAC with CD-Text and optional MusicBrainz lookup.");
            stdout.WriteLine("      FLAC files get ReplayGain 2.0 tags (unless --no-replaygain) and, with --cover, embedded front cover art.");
            return;
        }

        PrintHelp(stdout);
    }

    #endregion

    #region Internal Command-Line Argument Parser

    internal sealed class CommandLineArgs
    {
        public string? Subcommand { get; set; }
        public Dictionary<string, string> Options { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Flags { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Positionals { get; } = new();

        public bool IsJson => HasFlag("json");

        // A switch missing from KnownFlags is parsed as a value-less option; still count it as set so a new switch is
        // never silently ignored (this happened to --overwrite).
        public bool HasFlag(string name) =>
            Flags.Contains(name) || (Options.TryGetValue(name, out var val) && val.Length == 0);

        public string? GetOption(string name) => Options.TryGetValue(name, out var val) ? val : null;

        public string GetRequiredOption(string name, string optionSyntax)
        {
            if (!Options.TryGetValue(name, out var val) || string.IsNullOrWhiteSpace(val))
            {
                throw new CommandLineException($"Missing required option: {optionSyntax}");
            }
            return val;
        }

        /// <summary>
        /// True if the argument is a value rather than another option. Negative numbers (e.g. a read offset of -6)
        /// start with '-' but are values.
        /// </summary>
        private static bool IsOptionValue(string arg) =>
            !arg.StartsWith('-') ||
            double.TryParse(arg, NumberStyles.Float, CultureInfo.InvariantCulture, out _);

        public static CommandLineArgs Parse(string[] args)
        {
            var parsed = new CommandLineArgs();

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i].Trim();
                if (string.IsNullOrEmpty(arg)) continue;

                if (arg.StartsWith("--") || arg.StartsWith('-'))
                {
                    string trimmed = arg.TrimStart('-');
                    string key;
                    string? inlineVal = null;

                    int eqIdx = trimmed.IndexOf('=');
                    if (eqIdx >= 0)
                    {
                        key = trimmed.Substring(0, eqIdx);
                        inlineVal = trimmed.Substring(eqIdx + 1);
                    }
                    else
                    {
                        key = trimmed;
                    }

                    if (inlineVal != null)
                    {
                        parsed.Options[key] = inlineVal;
                    }
                    else if (KnownFlags.Contains(key))
                    {
                        parsed.Flags.Add(key);
                    }
                    else if (i + 1 < args.Length && IsOptionValue(args[i + 1]))
                    {
                        parsed.Options[key] = args[++i].Trim();
                    }
                    else
                    {
                        // Option without value
                        parsed.Options[key] = string.Empty;
                    }
                }
                else
                {
                    if (parsed.Subcommand == null)
                    {
                        parsed.Subcommand = arg;
                    }
                    else
                    {
                        parsed.Positionals.Add(arg);
                    }
                }
            }

            return parsed;
        }
    }

    #endregion
}
