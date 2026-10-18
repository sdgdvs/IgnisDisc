using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Audio;
using CdBurner.Core.Burning;
using CdBurner.Core.Compilation;
using CdBurner.Core.DvdVideo;
using CdBurner.Core.Images;
using CdBurner.Core.Iso;
using CdBurner.Core.Primitives;
using CdBurner.Core.Utilities;
using CdBurner.Hardware.Burning;
using CdBurner.Hardware.Devices;
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
        "json", "udf", "joliet", "verify", "close", "no-close", "raw", "full", "dao", "help", "h", "version", "v"
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
                stdout.WriteLine("IgnisDisc (CdBurner CLI) v1.01 (.NET 10.0 Windows)");
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
            "verify" or "verify-disc" => await HandleVerifyAsync(cmd, stdout, stderr, deviceService, burnEngine).ConfigureAwait(false),
            "erase" or "erase-disc" => await HandleEraseAsync(cmd, stdout, stderr, deviceService, burnEngine).ConfigureAwait(false),
            "scan" or "scan-disc" => await HandleScanAsync(cmd, stdout, stderr, deviceService, burnEngine).ConfigureAwait(false),
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

    private static int HandleMediaInfo(CommandLineArgs cmd, TextWriter stdout, IDiscDeviceService deviceService)
    {
        string driveId = cmd.GetRequiredOption("drive", "--drive <id>");
        var recorder = FindRecorder(deviceService, driveId);

        var status = recorder.QueryMediaStatus();
        var caps = recorder.GetCapabilities();
        bool isFinalized = status.MediaPresent && !status.IsBlank && !status.IsAppendable;
        long usedBytes = Math.Max(0L, status.TotalCapacityBytes - status.FreeCapacityBytes);

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
                isBlank = status.IsBlank,
                isRewritable = status.IsRewritable,
                isAppendable = status.IsAppendable,
                isFinalized = isFinalized,
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
        stdout.WriteLine($"  Media Present:         {(status.MediaPresent ? "Yes" : "No")}");
        if (status.MediaPresent)
        {
            stdout.WriteLine($"  Media Type:            {status.MediaType.GetDisplayName()}");
            stdout.WriteLine($"  Blank:                 {(status.IsBlank ? "Yes" : "No")}");
            stdout.WriteLine($"  Rewritable:            {(status.IsRewritable ? "Yes" : "No")}");
            stdout.WriteLine($"  Appendable:            {(status.IsAppendable ? "Yes" : "No")}");
            stdout.WriteLine($"  Finalized:             {(isFinalized ? "Yes" : "No")}");
            stdout.WriteLine($"  Total Capacity:        {FormatBytes(status.TotalCapacityBytes)} ({status.TotalSectors:N0} sectors)");
            stdout.WriteLine($"  Free Capacity:         {FormatBytes(status.FreeCapacityBytes)} ({status.FreeSectors:N0} sectors)");
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
            CloseDisc = closeDisc
        };

        var burnService = new DataBurnService(burnEngine);
        var result = await burnService.BurnIsoFileAsync(recorder, isoPath, burnOptions).ConfigureAwait(false);

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
        bool verify = cmd.HasFlag("verify");

        if (!Directory.Exists(inputFolder))
        {
            throw new DirectoryNotFoundException($"Input directory does not exist: {inputFolder}");
        }

        var recorder = FindRecorder(deviceService, driveId);

        string effectiveLabel = !string.IsNullOrWhiteSpace(volumeLabel) ? volumeLabel : "DATA_DISC";
        var root = CompilationDirectory.CreateRoot();
        PopulateDirectoryFromDisk(root, inputFolder);

        var burnOptions = new DataBurnOptions
        {
            VolumeLabel = effectiveLabel,
            VerifyAfterBurn = verify,
            CloseDisc = true
        };

        var buildOptions = new IsoBuildOptions(VolumeLabel: effectiveLabel);

        var burnService = new DataBurnService(burnEngine);
        var result = await burnService.BurnCompilationAsync(recorder, root, burnOptions, buildOptions).ConfigureAwait(false);

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
            return 0;
        }

        stderr.WriteLine($"Data burn failed: {result.ErrorMessage}");
        return 1;
    }

    private static async Task<int> HandleBurnAudioAsync(
        CommandLineArgs cmd,
        TextWriter stdout,
        TextWriter stderr,
        IDiscDeviceService deviceService,
        IBurnEngineBackend burnEngine)
    {
        string driveId = cmd.GetRequiredOption("drive", "--drive <id>");
        string tracksRaw = cmd.GetRequiredOption("tracks", "--tracks <wav1,wav2,...>");
        string? title = cmd.GetOption("title");
        string? artist = cmd.GetOption("artist");
        bool dao = cmd.HasFlag("dao");

        var recorder = FindRecorder(deviceService, driveId);

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
            string trackTitle = Path.GetFileNameWithoutExtension(trackPath);
            compilation.AddTrack(new AudioCdTrackItem(
                TrackNumber: i + 1,
                SourceFilePath: trackPath,
                Title: trackTitle,
                Performer: artist ?? "Various Artists",
                Isrc: null,
                PreGapFrames: i == 0 ? 150 : 0));
        }

        var audioBurnService = new AudioCdBurnService(burnEngine);
        var result = await audioBurnService.BurnAsync(
            recorder,
            compilation,
            new AudioBurnOptions { UseDiscAtOnce = dao, WriteCdText = compilation.IncludeCdText }).ConfigureAwait(false);

        if (cmd.IsJson)
        {
            var jsonResult = new
            {
                success = result.Success,
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
        string videoTsInput = cmd.GetRequiredOption("video-ts", "--video-ts <folder>");
        string? volumeLabel = cmd.GetOption("label");

        if (!Directory.Exists(videoTsInput))
        {
            throw new DirectoryNotFoundException($"VIDEO_TS directory does not exist: {videoTsInput}");
        }

        var recorder = FindRecorder(deviceService, driveId);

        var root = CompilationDirectory.CreateRoot();
        CompilationDirectory videoTsDir;

        string folderName = Path.GetFileName(videoTsInput.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.Equals(folderName, "VIDEO_TS", StringComparison.OrdinalIgnoreCase))
        {
            videoTsDir = root.AddDirectory("VIDEO_TS");
            PopulateDirectoryFromDisk(videoTsDir, videoTsInput);

            string? parent = Path.GetDirectoryName(Path.GetFullPath(videoTsInput));
            if (!string.IsNullOrEmpty(parent))
            {
                string siblingAudioTs = Path.Combine(parent, "AUDIO_TS");
                if (Directory.Exists(siblingAudioTs))
                {
                    var audioTs = root.AddDirectory("AUDIO_TS");
                    PopulateDirectoryFromDisk(audioTs, siblingAudioTs);
                }
            }
        }
        else
        {
            string childVideoTs = Path.Combine(videoTsInput, "VIDEO_TS");
            if (Directory.Exists(childVideoTs))
            {
                videoTsDir = root.AddDirectory("VIDEO_TS");
                PopulateDirectoryFromDisk(videoTsDir, childVideoTs);

                string childAudioTs = Path.Combine(videoTsInput, "AUDIO_TS");
                if (Directory.Exists(childAudioTs))
                {
                    var audioTs = root.AddDirectory("AUDIO_TS");
                    PopulateDirectoryFromDisk(audioTs, childAudioTs);
                }
            }
            else
            {
                videoTsDir = root.AddDirectory("VIDEO_TS");
                PopulateDirectoryFromDisk(videoTsDir, videoTsInput);
            }
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

        string effectiveLabel = !string.IsNullOrWhiteSpace(volumeLabel) ? volumeLabel : "DVD_VIDEO";
        var buildOptions = new IsoBuildOptions(
            VolumeLabel: effectiveLabel,
            IncludeIso9660: true,
            IncludeUdf: true,
            IncludeJoliet: false,
            UdfRevision: 0x0102);

        var burnService = new DataBurnService(burnEngine);
        var result = await burnService.BurnCompilationAsync(recorder, root, new DataBurnOptions { VolumeLabel = effectiveLabel, CloseDisc = true }, buildOptions).ConfigureAwait(false);

        if (cmd.IsJson)
        {
            var jsonResult = new
            {
                success = result.Success,
                driveId = recorder.UniqueId,
                videoTsFolder = Path.GetFullPath(videoTsInput),
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

        var result = await verifier.VerifyDiscAgainstIsoFileAsync(recorder, burnEngine, isoPath).ConfigureAwait(false);

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

        int blockSize = 256;
        if (!string.IsNullOrWhiteSpace(blockSizeStr) && int.TryParse(blockSizeStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int bs) && bs > 0)
        {
            blockSize = bs;
        }

        var recorder = FindRecorder(deviceService, driveId);
        var scanService = new SurfaceScanService(burnEngine);
        var scanOptions = new SurfaceScanOptions { BlockSizeSectors = blockSize };

        var report = await scanService.ScanAsync(recorder, burnEngine, scanOptions).ConfigureAwait(false);

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
                averageSpeedKbPerSec = report.AverageSpeedKbPerSec
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

    private static WriteSpeedDescriptor? ParseSpeed(string? speedStr, DiscMediaType mediaType)
    {
        if (string.IsNullOrWhiteSpace(speedStr)) return null;
        string cleaned = speedStr.TrimEnd('x', 'X').Trim();
        if (double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out double multiplier) && multiplier > 0)
        {
            return WriteSpeedDescriptor.FromMultiplier(multiplier, mediaType);
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

    private static void PopulateDirectoryFromDisk(CompilationDirectory target, string sourceDiskDir)
    {
        var dirInfo = new DirectoryInfo(sourceDiskDir);
        if (!dirInfo.Exists) return;

        foreach (var file in dirInfo.GetFiles())
        {
            try
            {
                target.AddFile(new CompilationFile(file.FullName, file.Length));
            }
            catch
            {
            }
        }

        foreach (var subDir in dirInfo.GetDirectories())
        {
            try
            {
                var childDir = target.AddDirectory(subDir.Name);
                PopulateDirectoryFromDisk(childDir, subDir.FullName);
            }
            catch
            {
            }
        }
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
        stdout.WriteLine("IgnisDisc Optical CLI (ignisdisc-cli.exe / cdburner-cli.exe)");
        stdout.WriteLine("Usage: ignisdisc-cli <command> [options]");
        stdout.WriteLine();
        stdout.WriteLine("Supported Commands:");
        stdout.WriteLine("  drives [--json]");
        stdout.WriteLine("      Lists physical and virtual optical drives and capabilities.");
        stdout.WriteLine("  media-info --drive <id> [--json]");
        stdout.WriteLine("      Shows inserted disc status, capacity, sessions, speeds.");
        stdout.WriteLine("  build-iso --input <folder> --output <file.iso> [--label <vol>] [--udf] [--joliet] [--boot-bios <img>] [--boot-uefi <img>] [--json]");
        stdout.WriteLine("      Compiles a folder into an ISO 9660 / Joliet / UDF filesystem image.");
        stdout.WriteLine("  burn-iso --drive <id> --iso <file.iso> [--speed <X>] [--verify] [--close] [--json]");
        stdout.WriteLine("      Burns an ISO image directly to the target optical drive.");
        stdout.WriteLine("  burn-data --drive <id> --input <folder> [--label <vol>] [--verify] [--json]");
        stdout.WriteLine("      Compiles a folder and burns it to optical media.");
        stdout.WriteLine("  burn-audio --drive <id> --tracks <wav1,wav2,...> [--title <album>] [--artist <name>] [--dao] [--json]");
        stdout.WriteLine("      Burns Red Book Audio CD from WAV tracks.");
        stdout.WriteLine("  burn-dvd --drive <id> --video-ts <folder> [--label <vol>] [--json]");
        stdout.WriteLine("      Validates, aligns, and burns a DVD-Video compliant disc from VIDEO_TS.");
        stdout.WriteLine("  rip --drive <id> --output <file.iso|file.bin> [--raw] [--json]");
        stdout.WriteLine("      Rips optical disc to an ISO or raw BIN+CUE image.");
        stdout.WriteLine("  verify --drive <id> --iso <file.iso> [--json]");
        stdout.WriteLine("      Verifies disc sectors against an ISO image with SHA-256 and defect mapping.");
        stdout.WriteLine("  erase --drive <id> [--full] [--json]");
        stdout.WriteLine("      Erases rewritable optical media (CD-RW, DVD-RW, DVD+RW, DVD-RAM).");
        stdout.WriteLine("  scan --drive <id> [--block-size <sectors>] [--json]");
        stdout.WriteLine("      Scans optical disc surface and benchmarks transfer rate curve.");
        stdout.WriteLine();
        stdout.WriteLine("General Options:");
        stdout.WriteLine("  --help, -h       Show help information.");
        stdout.WriteLine("  --version, -v    Show version information.");
        stdout.WriteLine("  --json           Format output as machine-readable JSON.");
    }

    private static void PrintSubcommandHelp(string? subcommand, TextWriter stdout)
    {
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

        public bool HasFlag(string name) => Flags.Contains(name);

        public string? GetOption(string name) => Options.TryGetValue(name, out var val) ? val : null;

        public string GetRequiredOption(string name, string optionSyntax)
        {
            if (!Options.TryGetValue(name, out var val) || string.IsNullOrWhiteSpace(val))
            {
                throw new CommandLineException($"Missing required option: {optionSyntax}");
            }
            return val;
        }

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
                    else if (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
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
