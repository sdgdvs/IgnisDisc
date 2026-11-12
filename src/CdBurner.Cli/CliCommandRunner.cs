using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CdBurner.Core;
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
using CdBurner.Core.Verification;
using CdBurner.Hardware.Audio;
using CdBurner.Hardware.Burning;
using CdBurner.Hardware.Devices;
using CdBurner.Hardware.NativeWriting;
using CdBurner.Hardware.Scsi;
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
        "overwrite", "strict-verify", "no-verify", "no-secure", "allow-damaged"
    };

    private static void WriteJsonResult(TextWriter stdout, object payload)
    {
        var node = JsonSerializer.SerializeToNode(payload, JsonOptions);
        if (node is JsonObject obj)
        {
            var withEngine = new JsonObject
            {
                ["engine"] = LegalBanner.JsonEngine
            };
            foreach (var kvp in obj)
            {
                if (kvp.Key != "engine")
                {
                    withEngine[kvp.Key] = kvp.Value?.DeepClone();
                }
            }
            stdout.WriteLine(JsonSerializer.Serialize(withEngine, JsonOptions));
            return;
        }

        stdout.WriteLine(JsonSerializer.Serialize(payload, JsonOptions));
    }

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

                if (parsed.IsJson)
                {
                    WriteJsonResult(stdout, new
                    {
                        success = true,
                        version = version ?? "unknown",
                        notice = LegalBanner.VersionNotice
                    });
                    return 0;
                }

                stdout.WriteLine(LegalBanner.VersionNotice);
                stdout.WriteLine($"IgnisDisc (CdBurner CLI) v{version ?? "dev"} (.NET {Environment.Version.ToString(2)} Windows)");
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
            WriteJsonResult(stdout, errObj);
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
            "check-disc" or "checkdisc" => HandleCheckDisc(cmd, stdout, deviceService),
            "copy-disc" or "copydisc" or "copy" => await HandleCopyDiscAsync(cmd, stdout, stderr, deviceService, burnEngine).ConfigureAwait(false),
            "archive-stage" or "archivestage" => await HandleArchiveStageAsync(cmd, stdout).ConfigureAwait(false),
            "archive-verify" or "archiveverify" => await HandleArchiveVerifyAsync(cmd, stdout).ConfigureAwait(false),
            "recover" or "archive-recover" => await HandleRecoverAsync(cmd, stdout, deviceService, burnEngine).ConfigureAwait(false),
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
            WriteJsonResult(stdout, result);
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
            WriteJsonResult(stdout, result);
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
        string? recommendedSpeed = status.MediaPresent
            ? WriteSpeedPolicy.ExplainRecommended(WriteSpeedPolicy.ForDisc(caps.SupportedWriteSpeeds, status.MediaType))
            : null;
        // Straight from the drive (read-only): formatted size and the write speeds it offers for this disc.
        var driveReport = status.MediaPresent && recorder is IDriveMediaReportProvider reportProvider
            ? reportProvider.ReadDriveMediaReport()
            : DriveMediaReport.Empty;
        // Only where formatting decides the usable size; on a CD-RW the drive just echoes the recorded data.
        var formatted = status.MediaType.IsFormattable() ? driveReport.FormattedCapacity : null;
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
                }).ToList(),
                formattedCapacity = formatted == null ? null : new
                {
                    state = formatted.StateName,
                    blocks = formatted.Blocks,
                    blockLength = formatted.BlockLength,
                    bytes = formatted.Bytes,
                    formats = formatted.Formats.Select(f => new
                    {
                        formatType = $"0x{f.FormatType:X2}",
                        blocks = f.Blocks,
                        typeParameter = f.TypeParameter
                    }).ToList()
                },
                recommendedWriteSpeed = recommendedSpeed,
                writeSpeedsFromDrive = driveReport.WriteSpeeds.Select(s => new
                {
                    multiplier = s.Multiplier(status.MediaType),
                    kilobytesPerSecond = s.KilobytesPerSecond,
                    bytesPerSecond = s.BytesPerSecond,
                    endLba = s.EndLba,
                    exact = s.Exact,
                    rotation = s.Rotation.ToString().ToUpperInvariant()
                }).ToList()
            };

            WriteJsonResult(stdout, result);
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
            if (formatted != null)
            {
                stdout.WriteLine(formatted.State == FormattedCapacityState.Formatted
                    ? $"  Formatted Capacity:    {FormatBytes(formatted.Bytes)} ({formatted.Blocks:N0} blocks of {formatted.BlockLength})"
                    : $"  Formattable Up To:     {FormatBytes(formatted.Bytes)} ({formatted.Blocks:N0} blocks, not formatted)");
            }
            if (driveReport.WriteSpeeds.Count > 0)
            {
                stdout.WriteLine($"  Drive's Write Speeds:  {string.Join(", ", driveReport.WriteSpeeds.Select(s => $"{s.Multiplier(status.MediaType):0.#}x{(s.Rotation == WriteRotation.Unknown ? "" : " " + s.Rotation.ToString().ToUpperInvariant())}"))}");
            }
            if (recommendedSpeed != null)
            {
                stdout.WriteLine($"  Default Write Speed:   {recommendedSpeed}");
            }
        }

        return 0;
    }

    /// <summary>
    /// Reads <c>--fixed-time</c> (Unix seconds or an ISO 8601 date/time; a time without an offset is taken as UTC),
    /// falling back to the <c>SOURCE_DATE_EPOCH</c> environment variable used by reproducible-build tooling.
    /// Returns null when neither is given, which keeps the normal build-time stamps.
    /// </summary>
    internal static DateTime? ResolveFixedTimestamp(CommandLineArgs cmd)
    {
        string? flagValue = cmd.GetOption("fixed-time");
        string? value = flagValue ?? Environment.GetEnvironmentVariable("SOURCE_DATE_EPOCH");
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        return ParseFixedTimestamp(value.Trim(), flagValue != null ? "--fixed-time" : "SOURCE_DATE_EPOCH");
    }

    /// <summary>Parses Unix seconds or an ISO 8601 date/time into a UTC build time an image can record.</summary>
    public static DateTime ParseFixedTimestamp(string value, string source = "--fixed-time")
    {
        var earliest = new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var latest = new DateTime(2155, 12, 31, 23, 59, 59, DateTimeKind.Utc);
        DateTime result;
        if (long.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long seconds))
        {
            if (seconds > 253402300799L)
            {
                throw new ArgumentException($"{source}: {value} is not a valid Unix time in seconds.");
            }
            result = DateTime.UnixEpoch.AddSeconds(seconds);
        }
        else if (DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
                     System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed))
        {
            result = parsed.UtcDateTime;
        }
        else
        {
            throw new ArgumentException($"{source}: '{value}' is not Unix seconds or an ISO 8601 date/time (e.g. 2026-01-01T00:00:00Z).");
        }

        // ISO 9660 dates cover 1900-2155; 1980 is the floor the builder already uses for file times.
        if (result < earliest || result > latest)
        {
            throw new ArgumentException($"{source}: {result:yyyy-MM-dd} is outside the range a disc image can record (1980-2155).");
        }
        return DateTime.SpecifyKind(result, DateTimeKind.Utc);
    }

    /// <summary>The UDF revision from <c>--udf-rev</c> (e.g. "2.50"), or 1.02 when it is not given.</summary>
    internal static ushort ResolveUdfRevision(CommandLineArgs cmd) =>
        cmd.GetOption("udf-rev") is { } value ? ParseUdfRevision(value) : (ushort)0x0102;

    /// <summary>Parses "2.50", "250" or "0x0250" into the BCD revision the builder takes.</summary>
    public static ushort ParseUdfRevision(string value)
    {
        string digits = value.Trim().Replace(".", "", StringComparison.Ordinal);
        if (digits.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            digits = digits[2..];
        }
        if (digits.Length is >= 3 and <= 4 && ushort.TryParse(digits, System.Globalization.NumberStyles.HexNumber, null, out ushort bcd)
            && IsoUdfImageBuilder.SupportedUdfRevisions.Contains(bcd))
        {
            return bcd;
        }
        throw new ArgumentException($"--udf-rev: '{value}' is not a supported UDF revision. Use 1.02, 1.50, 2.00, 2.01, 2.50 or 2.60.");
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

        bool hasUdfFlag = cmd.HasFlag("udf") || cmd.GetOption("udf-rev") != null;
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
            UdfRevision: ResolveUdfRevision(cmd),
            BootConfiguration: bootConfig,
            FixedTimestampUtc: ResolveFixedTimestamp(cmd),
            WriteSha256Manifest: cmd.HasFlag("sha256sums"),
            WriteBlake3Manifest: cmd.HasFlag("blake3sums"));

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

            WriteJsonResult(stdout, result);
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
        var (nativeEngine, testWrite) = ResolveNativeDataEngine(cmd, burnEngine);
        bool strictVerify = cmd.HasFlag("strict-verify"); // every read forced from the disc; implies --verify
        // Read back by default (--no-verify skips it; --verify is still accepted). A test write records nothing to read,
        // and a direct burn is always read back.
        bool verify = strictVerify || (!testWrite && (!cmd.HasFlag("no-verify") || nativeEngine != null));
        bool closeDisc = !cmd.HasFlag("no-close");
        bool overwrite = cmd.HasFlag("overwrite");

        if (!File.Exists(isoPath))
        {
            throw new FileNotFoundException($"ISO image file not found: {isoPath}", isoPath);
        }

        var recorder = FindRecorder(deviceService, driveId);
        var mediaStatus = recorder.QueryMediaStatus();

        var speedDesc = ParseSpeed(speedStr, recorder, mediaStatus.MediaType);

        var burnOptions = new DataBurnOptions
        {
            Speed = speedDesc,
            Simulate = testWrite,
            VerifyAfterBurn = verify,
            StrictVerify = strictVerify,
            CloseDisc = closeDisc,
            OverwriteExistingData = overwrite
        };

        var burnService = new DataBurnService(nativeEngine ?? burnEngine);
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
                verificationNote = result.VerificationNote,
                directWrite = NativeDataJson(nativeEngine),
                error = result.ErrorMessage
            };

            WriteJsonResult(stdout, jsonResult);
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
            if (result.VerificationNote != null) stdout.WriteLine($"  Verify Note:    {result.VerificationNote}");
            WriteNativeDataSummary(stdout, nativeEngine);
            return 0;
        }

        stderr.WriteLine($"Burn failed: {result.ErrorMessage}");
        WriteNativeDataSummary(stderr, nativeEngine);
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
        var (nativeEngine, testWrite) = ResolveNativeDataEngine(cmd, burnEngine);
        bool strictVerify = cmd.HasFlag("strict-verify"); // every read forced from the disc; implies --verify
        // Read back by default (--no-verify skips it; --verify is still accepted). A test write records nothing to read,
        // and a direct burn is always read back.
        bool verify = strictVerify || (!testWrite && (!cmd.HasFlag("no-verify") || nativeEngine != null));
        bool closeDisc = !cmd.HasFlag("no-close");
        bool overwrite = cmd.HasFlag("overwrite");

        if (!Directory.Exists(inputFolder))
        {
            throw new DirectoryNotFoundException($"Input directory does not exist: {inputFolder}");
        }

        bool hasUdfFlag = cmd.HasFlag("udf") || cmd.GetOption("udf-rev") != null;
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
        var speedDesc = ParseSpeed(speedStr, recorder, mediaStatus.MediaType);

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
            Simulate = testWrite,
            VerifyAfterBurn = verify,
            StrictVerify = strictVerify,
            CloseDisc = closeDisc,
            OverwriteExistingData = overwrite
        };

        var buildOptions = new IsoBuildOptions(
            VolumeLabel: effectiveLabel,
            IncludeIso9660: true,
            IncludeJoliet: includeJoliet,
            IncludeUdf: includeUdf,
            UdfRevision: ResolveUdfRevision(cmd),
            BootConfiguration: bootConfig,
            FixedTimestampUtc: ResolveFixedTimestamp(cmd),
            WriteSha256Manifest: cmd.HasFlag("sha256sums"),
            WriteBlake3Manifest: cmd.HasFlag("blake3sums"));

        var phases = new PhaseTimingProgress(cmd.IsJson ? null : stdout);
        var burnService = new DataBurnService(nativeEngine ?? burnEngine);
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
                verificationNote = result.VerificationNote,
                warning = result.Warning,
                phases = phases.Phases.Select(p => new { phase = p.Name, seconds = Math.Round(p.Duration.TotalSeconds, 1) }),
                directWrite = NativeDataJson(nativeEngine),
                error = result.ErrorMessage
            };

            WriteJsonResult(stdout, jsonResult);
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
            if (result.VerificationNote != null) stdout.WriteLine($"  Verify Note:    {result.VerificationNote}");
            if (result.Warning != null) stdout.WriteLine($"  Warning:        {result.Warning}");
            WriteNativeDataSummary(stdout, nativeEngine);
            phases.WriteSummary(stdout);
            return 0;
        }

        stderr.WriteLine($"Data burn failed: {result.ErrorMessage}");
        WriteNativeDataSummary(stderr, nativeEngine);
        phases.WriteSummary(stderr);
        return 1;
    }

    /// <summary>
    /// <c>--native [--test-write | --test-first] [--log-dir]</c> for data burns: the direct writer, with the given
    /// engine kept for discs it does not cover yet, reading and erasing. Null without <c>--native</c>.
    /// </summary>
    private static (NativeDataBurnEngine? Engine, bool TestWrite) ResolveNativeDataEngine(CommandLineArgs cmd, IBurnEngineBackend fallback)
    {
        bool native = cmd.HasFlag("native");
        bool testWrite = cmd.HasFlag("test-write");
        bool testFirst = cmd.HasFlag("test-first");
        if ((testWrite || testFirst || cmd.GetOption("log-dir") != null) && !native)
        {
            throw new CommandLineException("--test-write, --test-first and --log-dir need --native.");
        }
        if (testFirst && testWrite)
        {
            throw new CommandLineException("--test-first only applies to a real --native burn (without --test-write).");
        }
        return native
            ? (new NativeDataBurnEngine(fallback, cmd.GetOption("log-dir")) { TestWriteFirst = testFirst }, testWrite)
            : (null, false);
    }

    private static object? NativeDataJson(NativeDataBurnEngine? engine)
    {
        if (engine == null)
        {
            return null;
        }
        var r = engine.LastResult;
        return new
        {
            writer = engine.LastFallbackReason != null ? "windows" : "direct",
            fallbackReason = engine.LastFallbackReason,
            testWritePassedFirst = engine.LastTestWritePassed,
            writeMode = r?.WriteMode,
            speedSetKiloBytesPerSecond = r?.SpeedSetKiloBytesPerSecond,
            driveSpeedsKiloBytesPerSecond = r?.DriveSpeedsKiloBytesPerSecond,
            dataSectors = r?.DataSectorsWritten,
            layer0Sectors = r?.Layer0Sectors,
            paddingSectors = r?.PaddingSectors,
            writingSeconds = r == null ? (double?)null : Math.Round(r.WritingTime.TotalSeconds, 1),
            writerSeconds = r == null ? (double?)null : Math.Round(r.Elapsed.TotalSeconds, 1),
            writerMessage = r?.Message,
            commandLog = engine.LastLogPath
        };
    }

    private static void WriteNativeDataSummary(TextWriter output, NativeDataBurnEngine? engine)
    {
        if (engine == null)
        {
            return;
        }
        if (engine.LastFallbackReason != null)
        {
            output.WriteLine($"  Writer:         {engine.LastFallbackReason}");
            return;
        }
        var r = engine.LastResult;
        output.WriteLine($"  Writer:         direct drive commands ({r?.WriteMode ?? "not started"})");
        if (engine.TestWriteFirst) output.WriteLine($"  Test write:     {(engine.LastTestWritePassed ? "passed first" : "not passed")}");
        if (r != null)
        {
            double oneX = r.OneXKiloBytesPerSecond;
            string speed = r.SpeedSetKiloBytesPerSecond is int kb ? $"{kb} kB/s ({kb / oneX:0.#}x)" : "drive's own";
            output.WriteLine($"  Speed set:      {speed}; drive offers {(r.DriveSpeedsKiloBytesPerSecond.Count > 0 ? string.Join(", ", r.DriveSpeedsKiloBytesPerSecond.Select(k => $"{k / oneX:0.#}x")) : "no list")}");
            output.WriteLine($"  Writing time:   {r.WritingTime.TotalSeconds:F0} s for the data ({r.AverageWriteMultiplier:F1}x average), {r.Elapsed.TotalSeconds:F0} s with closing");
            output.WriteLine($"  Drive reports:  {r.Message}");
        }
        if (engine.LastLogPath != null) output.WriteLine($"  Command log:    {engine.LastLogPath}");
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
        bool testFirst = cmd.HasFlag("test-first");
        if (testFirst && (!native || testWrite))
        {
            throw new CommandLineException("--test-first only applies to a real --native burn (without --test-write).");
        }

        var recorder = FindRecorder(deviceService, driveId);
        if (recorder is VirtualDiscRecorder vRec && !vRec.QueryMediaStatus().MediaPresent)
        {
            vRec.InsertMedia(VirtualDiscMedium.CreateBlankCdR700());
        }
        var mediaStatus = recorder.QueryMediaStatus();
        var speedDesc = ParseSpeed(speedStr, recorder, mediaStatus.MediaType, isAudio: true);

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
        var nativeEngine = native ? new NativeAudioBurnEngine(cmd.GetOption("log-dir")) { TestWriteFirst = testFirst } : null;
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
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // The burn itself reports decode errors in detail; here only the titles are lost, so say so.
                stderr.WriteLine($"Warning: could not read tags from '{Path.GetFileName(trackPath)}' ({ex.Message}); its CD-Text title will be the file name.");
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

        if (!compilation.IncludeCdText)
        {
            stderr.WriteLine("Note: no titles or artists were found in the tracks' tags and none were given (--title, --artist), so the disc gets no CD-Text.");
        }

        var result = await audioBurnService.BurnAsync(
            recorder,
            compilation,
            new AudioBurnOptions
            {
                Speed = speedDesc,
                UseDiscAtOnce = dao,
                WriteCdText = compilation.IncludeCdText,
                Simulate = testWrite,
                KeepIndexMarks = native, // only the direct writer can place INDEX 00 marks and hidden audio
                AllowDamagedAudio = cmd.HasFlag("allow-damaged"),
                WriteOffsetSamples = ParseWriteOffset(cmd.GetOption("write-offset"), native)
            }).ConfigureAwait(false);

        var verification = nativeEngine?.LastResult?.Verification;

        if (cmd.IsJson)
        {
            var jsonResult = new
            {
                success = result.Success,
                testWrite,
                native,
                commandLog = nativeEngine?.LastLogPath,
                discStillBlank = nativeEngine?.LastResult?.DiscStillBlankAfterTestWrite,
                testWritePassedFirst = nativeEngine?.LastTestWritePassed,
                discRecorded = nativeEngine?.LastResult?.DiscRecorded,
                verified = verification?.Passed,
                verifyMessage = verification?.Message,
                readBackOffsetSamples = verification?.OffsetSamples,
                samplesCompared = verification?.SamplesCompared,
                hardToReadSectors = verification?.HardToReadSectors,
                writtenWrongSectors = verification?.WrittenWrongSectors,
                unreadableSectors = verification?.UnreadableSectors,
                verifyWarning = verification?.Warning,
                damagedAudioFiles = result.DamagedAudioFiles,
                writeOffset = nativeEngine?.LastWriteOffsetPlan is { } plan
                    ? new
                    {
                        readOffsetSamples = plan.ReadOffset,
                        readOffsetSource = plan.ReadOffsetSource,
                        combinedOffsetSamples = plan.CombinedOffset,
                        writeOffsetSamples = plan.WriteOffset,
                        shiftSamples = plan.ShiftSamples,
                        note = plan.Explanation
                    }
                    : null,
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

            WriteJsonResult(stdout, jsonResult);
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
            if (nativeEngine != null)
            {
                stdout.WriteLine("  Writer:         direct drive commands (Session-at-Once)");
                stdout.WriteLine($"  Test write:     {(nativeEngine.LastTestWritePassed ? "passed first" : "not run")}");
                stdout.WriteLine($"  Read-back:      {verification?.Message ?? "not run"}");
                if (verification?.Warning is { } readWarning)
                {
                    stderr.WriteLine($"Warning: {readWarning}");
                }
                if (nativeEngine.LastLogPath != null)
                {
                    stdout.WriteLine($"  Command log:    {nativeEngine.LastLogPath}");
                }
            }
            return 0;
        }

        stderr.WriteLine($"Audio burn failed: {result.ErrorMessage}");
        if (result.DamagedAudioFiles is { Count: > 0 })
        {
            stderr.WriteLine("To burn anyway, run the same command with --allow-damaged.");
        }
        return 1;
    }

    /// <summary>
    /// check-disc: reads every sector of the CD in the drive once with C2 error pointers and says where it is weak (read-only).
    /// </summary>
    /// <summary>
    /// <c>copy-disc</c>: the app's disc copy (<see cref="DiscCopyService"/>). Messages for the person at the drive go to
    /// stderr, so <c>--json</c> output stays one object on stdout.
    /// </summary>
    private static async Task<int> HandleCopyDiscAsync(CommandLineArgs cmd, TextWriter stdout, TextWriter stderr,
        IDiscDeviceService deviceService, IBurnEngineBackend burnEngine)
    {
        var source = FindRecorder(deviceService, cmd.GetRequiredOption("source", "--source <id>"));
        var target = cmd.GetOption("target") is { } targetId ? FindRecorder(deviceService, targetId) : source;
        bool singleDrive = string.Equals(source.UniqueId, target.UniqueId, StringComparison.OrdinalIgnoreCase);
        int copies = int.TryParse(cmd.GetOption("copies") ?? "1", out int n) && n is >= 1 and <= 99
            ? n : throw new CommandLineException("--copies takes a number from 1 to 99.");
        int waitMinutes = int.TryParse(cmd.GetOption("wait-minutes") ?? "30", out int w) && w is >= 1 and <= 1440
            ? w : throw new CommandLineException("--wait-minutes takes a number from 1 to 1440.");
        bool native = cmd.HasFlag("native");
        if (cmd.GetOption("log-dir") != null && !native)
        {
            throw new CommandLineException("--log-dir needs --native.");
        }
        string? keep = cmd.GetOption("keep-image");

        var sourceStatus = source.QueryMediaStatus();
        if (!sourceStatus.MediaPresent || sourceStatus.IsBlank)
        {
            throw new CommandLineException($"There is no recorded disc to copy in {source.VolumePath ?? source.UniqueId}.");
        }
        bool isAudio = DiscCopyService.IsSourceAudioCd(source);
        var speed = ParseSpeed(cmd.GetOption("speed"), target, sourceStatus.MediaType, isAudio);

        async Task<bool> PrepareTarget(int copyIndex, CancellationToken ct)
        {
            if (target is VirtualDiscRecorder virtualTarget)
            {
                var status = virtualTarget.QueryMediaStatus();
                if (singleDrive || copyIndex > 1 || !status.MediaPresent || !status.IsBlank)
                {
                    virtualTarget.InsertMedia(VirtualDiscMedium.CreateBlankFor(sourceStatus));
                }
                return true;
            }
            if (!singleDrive && copyIndex == 1)
            {
                return true; // the blank disc already in the second drive
            }

            target.EjectMedia();
            stderr.WriteLine($"Copy {copyIndex} of {copies}: put a blank disc in {target.VolumePath ?? target.UniqueId} and close the tray"
                + $" (waiting up to {waitMinutes} min).");
            var deadline = DateTime.UtcNow.AddMinutes(waitMinutes);
            await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var status = target.QueryMediaStatus();
                    if (status.MediaPresent && status.IsBlank)
                    {
                        stderr.WriteLine($"Blank {status.MediaType} found; copying.");
                        return true;
                    }
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
                {
                    // The drive is still loading the disc; ask again.
                }
                await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
            }
            stderr.WriteLine($"No blank disc within {waitMinutes} min.");
            return false;
        }

        var nativeEngine = native ? new NativeAudioBurnEngine(cmd.GetOption("log-dir")) : null;
        using var decoder = OperatingSystem.IsWindows() ? new MediaFoundationAudioDecoder() : null;
        string? stagingFolder = null;
        if (keep != null)
        {
            Directory.CreateDirectory(keep);
            stagingFolder = keep;
        }

        var result = await new DiscCopyService(burnEngine, decoder).CopyDiscAsync(source, target, new DiscCopyOptions
        {
            CopyCount = copies,
            RawCloneMode = cmd.HasFlag("raw"),
            WriteSpeed = speed,
            KeepStagedImage = keep != null,
            CustomStagingImagePath = stagingFolder != null ? Path.Combine(stagingFolder, isAudio ? "album.cue" : cmd.HasFlag("raw") ? "staged_disc.bin" : "staged_disc.iso") : null,
            MixedModeWriter = nativeEngine,
            AllowSuspectAudio = cmd.HasFlag("allow-suspect"),
            OnPrepareTargetDiscAsync = PrepareTarget
        }).ConfigureAwait(false);

        if (cmd.IsJson)
        {
            WriteJsonResult(stdout, new
            {
                success = result.Success,
                error = result.ErrorMessage,
                warning = result.WarningMessage,
                copiesRequested = result.CopiesRequested,
                copiesCompleted = result.CopiesCompleted,
                audioCd = result.WasAudioCd,
                mixedMode = result.IsMixedMode,
                copiedDataTrack = result.CopiedDataTrack,
                sectorsPerDisc = result.SectorsPerDisc,
                totalSectorsWritten = result.TotalSectorsWritten,
                totalTimeSeconds = result.TotalTime.TotalSeconds,
                stagedImage = result.StagedImagePath,
                verified = nativeEngine?.LastResult?.Verification?.Passed,
                verifyMessage = nativeEngine?.LastResult?.Verification?.Message,
                commandLog = nativeEngine?.LastLogPath
            });
        }
        else if (result.Success)
        {
            stdout.WriteLine($"Copied: {result.CopiesCompleted} of {result.CopiesRequested} disc(s), {result.SectorsPerDisc:N0} sectors each, in {result.TotalTime.TotalSeconds:N0} s.");
            if (result.CopiedDataTrack) stdout.WriteLine("  The mixed-mode CD's data track was copied with the audio.");
            if (nativeEngine?.LastResult?.Verification is { } v) stdout.WriteLine($"  {v.Message}");
            if (result.WarningMessage != null) stdout.WriteLine($"  Warning: {result.WarningMessage}");
        }
        else
        {
            stderr.WriteLine($"Copy failed: {result.ErrorMessage}");
        }
        return result.Success ? 0 : 1;
    }

    private static int HandleCheckDisc(CommandLineArgs cmd, TextWriter stdout, IDiscDeviceService deviceService)
    {
        string driveId = cmd.GetRequiredOption("drive", "--drive <id>");
        var recorder = FindRecorder(deviceService, driveId);
        if (recorder.IsVirtual || string.IsNullOrWhiteSpace(recorder.VolumePath))
        {
            throw new CommandLineException("check-disc reads a real disc: choose a physical drive.");
        }

        DiscHealthReport report;
        using (var transport = Win32ScsiTransport.Open(recorder.VolumePath, FileAccess.ReadWrite))
        {
            int lastPercent = -1;
            report = new DiscHealthScanner(transport).Scan((done, total) =>
            {
                int percent = (int)(100L * done / Math.Max(1, total));
                if (!cmd.IsJson && percent / 10 != lastPercent / 10)
                {
                    stdout.WriteLine($"  Reading: {percent}%");
                }
                lastPercent = percent;
            });
        }

        if (cmd.IsJson)
        {
            WriteJsonResult(stdout, new
            {
                success = true,
                driveId = recorder.UniqueId,
                verdict = report.Verdict.ToString(),
                summary = report.Describe(),
                sectorsRead = report.TotalSectors,
                flaggedSectors = report.FlaggedSectors,
                unreadableSectors = report.UnreadableSectors,
                c2Available = report.C2Available,
                problemsPerMinute = report.ProblemsPerMinute,
                outerFifthShare = report.FlaggedShareInOuterFifth,
                seconds = report.Elapsed.TotalSeconds,
                tracks = report.Tracks.Select(t => new { number = t.TrackNumber, audio = t.IsAudio, startLba = t.StartLba, sectors = t.Sectors, flagged = t.FlaggedSectors, unreadable = t.UnreadableSectors })
            });
            return 0;
        }

        stdout.WriteLine();
        stdout.WriteLine(report.Describe());
        foreach (var t in report.Tracks.Where(t => t.FlaggedSectors + t.UnreadableSectors > 0))
        {
            stdout.WriteLine($"  Track {t.TrackNumber:D2}: {t.FlaggedSectors} with uncorrectable errors, {t.UnreadableSectors} unreadable (of {t.Sectors:N0})");
        }
        if (report.FlaggedSectors + report.UnreadableSectors > 0)
        {
            stdout.WriteLine("  By minute of the disc: " + string.Join(" ", report.ProblemsPerMinute.Select((n, m) => n == 0 ? "." : $"{m}:{n}")));
        }
        stdout.WriteLine($"  Took {report.Elapsed.TotalSeconds:F0} s.");
        return 0;
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
        var (nativeEngine, testWrite) = ResolveNativeDataEngine(cmd, burnEngine);

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

            var burnService = new DataBurnService(nativeEngine ?? burnEngine);
            var dvdOptions = new DataBurnOptions
            {
                VolumeLabel = effectiveLabel,
                CloseDisc = true,
                OverwriteExistingData = cmd.HasFlag("overwrite"),
                Speed = ParseSpeed(cmd.GetOption("speed"), recorder, recorder.QueryMediaStatus().MediaType),
                Simulate = testWrite,
                // Read back by default; a test write records nothing; a direct burn is always read back.
                VerifyAfterBurn = !testWrite && (!cmd.HasFlag("no-verify") || nativeEngine != null)
            };
            result = await burnService.BurnCompilationAsync(recorder, root, dvdOptions, buildOptions).ConfigureAwait(false);
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
                testWrite,
                verified = result.Verified,
                verificationNote = result.VerificationNote,
                directWrite = NativeDataJson(nativeEngine),
                error = result.ErrorMessage
            };

            WriteJsonResult(stdout, jsonResult);
            return result.Success ? 0 : 1;
        }

        if (result.Success)
        {
            stdout.WriteLine(testWrite ? "DVD-Video test write completed (laser off; the disc was not written):" : "DVD-Video burned successfully:");
            stdout.WriteLine($"  Drive:          {recorder.UniqueId}");
            stdout.WriteLine($"  Volume Label:   {effectiveLabel}");
            stdout.WriteLine($"  Sectors Burned: {result.SectorsWritten:N0}");
            stdout.WriteLine($"  Duration:       {result.TotalTime}");
            stdout.WriteLine($"  Verified:       {(result.Verified ? "yes" : "no")}{(string.IsNullOrEmpty(result.VerificationNote) ? "" : " - " + result.VerificationNote)}");
            if (nativeEngine?.LastLogPath != null)
            {
                stdout.WriteLine($"  Command log:    {nativeEngine.LastLogPath}");
            }
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

            WriteJsonResult(stdout, jsonResult);
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
            SecureMode = !cmd.HasFlag("no-secure"), // on by default; a drive without C2 pointers falls back to a plain read
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
                WriteJsonResult(stdout, errObj);
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

        // A rip with sectors that may be wrong is finished but not clean: exit code 2 (as `recover` uses for lost files),
        // so a script does not take it for a perfect copy.
        int suspectTotal = result.TrackInfos.Sum(t => t.SuspectSectors);
        int unreadableTotal = Math.Max(result.TrackInfos.Sum(t => t.UnreadableSectors), result.BadSectorLbas.Count);
        bool complete = suspectTotal + unreadableTotal == 0;
        int exitCode = complete ? 0 : 2;

        if (cmd.IsJson)
        {
            var jsonResult = new
            {
                success = true,
                complete,
                suspectSectors = suspectTotal,
                unreadableSectors = unreadableTotal,
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
                secureMode = cmd.HasFlag("no-secure") ? "off" : result.SecureModeUsed ? "c2" : "unsupported",
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

            WriteJsonResult(stdout, jsonResult);
            return exitCode;
        }

        stdout.WriteLine(complete
            ? "Audio rip completed successfully:"
            : $"Audio rip finished, but {suspectTotal + unreadableTotal:N0} sector(s) may be wrong ({suspectTotal:N0} still suspect after re-reads, {unreadableTotal:N0} unreadable):");
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
        if (!cmd.HasFlag("no-secure"))
        {
            stdout.WriteLine(result.SecureModeUsed
                ? $"  Secure Mode:      C2 error pointers ({result.TrackInfos.Sum(t => t.RecoveredSectors)} sectors recovered, {result.TrackInfos.Sum(t => t.SuspectSectors)} still suspect)"
                : "  Secure Mode:      the drive does not report C2 error pointers, so a plain read was used");
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

        return exitCode;
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
            WriteJsonResult(stdout, new
            {
                success = best != null,
                driveId = recorder.UniqueId,
                readOffset = best?.OffsetSamples,
                confidence = best?.Confidence,
                probedTracks = detection.ProbedTracks,
                candidates = detection.Candidates.Select(c => new { offset = c.OffsetSamples, c.Confidence, c.TrackNumber }),
                accurateRipPath = ids.AccurateRipDatabasePath,
                error = best == null ? (detection.ErrorMessage ?? "No offset within +/-3000 samples matched the AccurateRip database.") : null
            });
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
            "bd" => DiscMediaType.BdRe,                     // BD-RE's spare area makes it the smaller of BD-R/RE
            "bd-dl" => DiscMediaType.BdReDualLayer,
            "bdxl" => DiscMediaType.BdxlRTripleLayer,
            "bdxl-ql" => DiscMediaType.BdxlRQuadLayer,
            var other => throw new CommandLineException($"Invalid media '{other}'. Supported: cd, dvd, dvd-dl, bd, bd-dl, bdxl, bdxl-ql.")
        };

        string redundancyOpt = cmd.GetOption("redundancy") ?? "10";
        if (!int.TryParse(redundancyOpt, NumberStyles.Integer, CultureInfo.InvariantCulture, out int redundancy) || redundancy is < 0 or > 100)
        {
            throw new CommandLineException($"Invalid redundancy '{redundancyOpt}'. Expected a percentage between 0 and 100.");
        }

        // Recovery data fills each disc unless --no-fill; --redundancy is then the minimum.
        var plan = ArchiveSpanPlanner.Plan(input, media, redundancy, fillFreeSpace: !cmd.HasFlag("no-fill"));

        string? par2Path = null;
        if (!planOnly && redundancy > 0)
        {
            par2Path = Par2Runner.FindPar2Executable()
                ?? throw new CommandLineException(
                    "PAR2 recovery data needs par2cmdline (par2.exe on PATH). Install it, or pass --redundancy 0 for manifests only.");
        }

        IReadOnlyList<ArchiveStagedDisc> staged = Array.Empty<ArchiveStagedDisc>();
        if (!planOnly)
        {
            var progress = cmd.IsJson ? null : new Progress<string>(message => stdout.WriteLine($"  {message}"));
            staged = await ArchiveStager.StageDiscsAsync(plan, staging!, par2Path, progress).ConfigureAwait(false);
        }
        IReadOnlyList<string> stagedDiscs = staged.Select(s => s.DiscRoot).ToList();

        if (cmd.IsJson)
        {
            WriteJsonResult(stdout, new
            {
                success = true,
                setId = plan.SetId,
                media = media.ToString(),
                redundancyPercent = plan.RedundancyPercent,
                usableSectorsPerDisc = plan.UsableSectorsPerDisc,
                ceilingSectorsPerDisc = plan.EffectiveCeilingSectors,
                fillFreeSpace = plan.FillFreeSpace,
                totalBytes = plan.TotalBytes,
                discs = plan.Discs.Select(d =>
                {
                    var s = staged.Count >= d.DiscNumber ? staged[d.DiscNumber - 1] : null;
                    return new
                    {
                        discNumber = d.DiscNumber,
                        files = d.Files.Count,
                        dataBytes = d.DataBytes,
                        path = s?.DiscRoot,
                        totalSectors = s?.TotalSectors,
                        recoverySectors = s?.RecoverySectors,
                        recoveryBlocks = s?.Recovery?.RecoveryBlocks,
                        recoveryBlockSize = s?.Recovery?.BlockSize,
                        recoveryPercent = s?.Recovery == null ? (double?)null : Math.Round(s.Recovery.RedundancyPercent, 1)
                    };
                }),
                planOnly
            });
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
            DiscMediaType.BdRe => "BD-R/RE (25 GB)",
            DiscMediaType.BdReDualLayer => "BD-R/RE DL (50 GB)",
            DiscMediaType.BdxlRTripleLayer => "BD-R XL (100 GB)",
            DiscMediaType.BdxlRQuadLayer => "BD-R XL (128 GB)",
            _ => media.GetDisplayName()
        };
        stdout.WriteLine($"  Media:            {mediaName}");
        stdout.WriteLine($"  Redundancy:       {plan.RedundancyPercent}% PAR2");
        stdout.WriteLine($"  Total Data:       {plan.TotalBytes / 1048576.0:N1} MB in {plan.Discs.Sum(d => d.Files.Count):N0} files");
        stdout.WriteLine($"  Discs:            {plan.Discs.Count}");
        foreach (var d in plan.Discs)
        {
            var s = staged.Count >= d.DiscNumber ? staged[d.DiscNumber - 1] : null;
            string where = s != null ? $"  -> {s.DiscRoot}" : string.Empty;
            string recovery = s?.Recovery is { } r
                ? $", recovery {r.RecoverySectors * 2048.0 / 1048576:N1} MB ({r.RedundancyPercent:N0} %, survives {r.RecoveryBlocks:N0} damaged blocks of {r.BlockSize / 1024:N0} KB)"
                : string.Empty;
            stdout.WriteLine($"    Disc {d.DiscNumber:D2}: {d.Files.Count:N0} files, {d.DataBytes / 1048576.0:N1} MB{recovery}{where}");
        }
        if (!planOnly && stagedDiscs.Count > 0)
        {
            stdout.WriteLine($"  Burn each disc with: burn-data --drive <id> --input \"{stagedDiscs[0]}\" --udf --verify");
            stdout.WriteLine("  Check a burned disc later with: archive-verify --path <drive letter>");
        }
        return 0;
    }

    /// <summary>
    /// recover (--drive &lt;id&gt; | --image &lt;file.iso&gt;) --out &lt;folder&gt; [--retries N] [--keep-image] [--json]:
    /// reads an archive disc with retries (unreadable sectors zero-filled), copies its files out, rebuilds damaged
    /// ones from the disc's PAR2 recovery data and checks them against its manifest. Exit code 0 only when every
    /// file checks out, 2 when some could not be recovered.
    /// </summary>
    private static async Task<int> HandleRecoverAsync(
        CommandLineArgs cmd, TextWriter stdout, IDiscDeviceService deviceService, IBurnEngineBackend burnEngine)
    {
        string output = Path.GetFullPath(cmd.GetRequiredOption("out", "--out <folder>"));
        string? image = cmd.GetOption("image");
        string? driveId = cmd.GetOption("drive");
        if ((image == null) == (driveId == null))
        {
            throw new CommandLineException("Give either --drive <id> to read a disc, or --image <file.iso> for an image of one.");
        }

        string retriesOption = cmd.GetOption("retries") ?? "5";
        if (!int.TryParse(retriesOption, NumberStyles.Integer, CultureInfo.InvariantCulture, out int retries) || retries is < 0 or > 100)
        {
            throw new CommandLineException($"Invalid --retries '{retriesOption}'. Expected 0 to 100.");
        }

        var progress = cmd.IsJson ? null : new Progress<string>(message => stdout.WriteLine($"  {message}"));
        IReadOnlyList<int> unreadable = Array.Empty<int>();
        string? ripped = null;
        if (driveId != null)
        {
            var recorder = FindRecorder(deviceService, driveId);
            ripped = output.TrimEnd(Path.DirectorySeparatorChar) + ".read.iso";
            if (!cmd.IsJson)
            {
                stdout.WriteLine($"Reading the disc in {driveId} (up to {retries} retries per bad sector)...");
            }
            var rip = await new DiscRipperService().RipToIsoAsync(recorder, burnEngine, ripped,
                new RipOptions { MaxRetriesPerSector = retries, ContinueOnUnreadableSector = true }).ConfigureAwait(false);
            if (!File.Exists(ripped))
            {
                throw new CommandLineException("The disc could not be read: " + (rip.ErrorMessage ?? "unknown error"));
            }
            unreadable = rip.BadSectorLbas;
            image = ripped;
        }
        else if (!File.Exists(image))
        {
            throw new CommandLineException($"Image not found: '{image}'.");
        }

        var report = await ArchiveRecoveryService.RecoverFromImageAsync(
            image!, unreadable, output, Par2Runner.FindPar2Executable(), progress).ConfigureAwait(false);

        string reportPath = output.TrimEnd(Path.DirectorySeparatorChar) + "-recovery-report.txt";
        await File.WriteAllTextAsync(reportPath, report.ToText()).ConfigureAwait(false);
        if (ripped != null && !cmd.HasFlag("keep-image"))
        {
            try { File.Delete(ripped); } catch (IOException) { }
        }

        if (cmd.IsJson)
        {
            WriteJsonResult(stdout, new
            {
                success = report.FullyRecovered,
                output = report.OutputFolder,
                report = reportPath,
                image = ripped != null && cmd.HasFlag("keep-image") ? ripped : image,
                unreadableSectors = report.UnreadableSectors.Count,
                damagedFiles = report.DamagedFiles,
                repairedFiles = report.RepairedFiles,
                stillMissing = report.StillMissing,
                stillCorrupt = report.StillCorrupt,
                filesChecked = report.FilesChecked,
                hadRecoveryData = report.HadRecoveryData,
                problem = report.Problem
            });
        }
        else
        {
            stdout.Write(report.ToText());
            stdout.WriteLine($"Report saved to: {reportPath}");
        }
        return report.FullyRecovered ? 0 : 2;
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
            WriteJsonResult(stdout, new
            {
                success = result.IsIntact,
                setId = result.SetId,
                discNumber = result.DiscNumber,
                discCount = result.DiscCount,
                filesChecked = result.FilesChecked,
                missing = result.Missing,
                corrupt = result.Corrupt,
                hasRecoveryData = result.HasRecoveryData
            });
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

    /// <summary>
    /// <c>verify --cue</c>: checks a recorded audio or mixed-mode CD against the <c>.cue</c> it was burned from, with the
    /// read-back check a direct burn ends with, and writes nothing. The tracks are prepared as <c>burn-audio --native</c>
    /// prepares them, so a disc burned that way on this drive is compared sample for sample.
    /// </summary>
    private static async Task<int> HandleVerifyCueAsync(CommandLineArgs cmd, string cuePath, IDiscRecorderBackend recorder,
        TextWriter stdout, TextWriter stderr)
    {
        if (!File.Exists(cuePath))
        {
            throw new FileNotFoundException($"CUE sheet not found: {cuePath}", cuePath);
        }

        using var decoder = OperatingSystem.IsWindows() ? new MediaFoundationAudioDecoder() : null;
        var engine = new NativeAudioBurnEngine(cmd.GetOption("log-dir"));
        var service = new AudioCdBurnService(engine, decoder);
        // The album text starts as burn-audio starts it (--title / --artist, else its defaults), so the CD-Text compared
        // is the CD-Text that command wrote.
        var compilation = new AudioCdCompilation
        {
            RecordingMode = AudioRecordingMode.DiscAtOnceGapless,
            AlbumTitle = cmd.GetOption("title") ?? "Audio CD",
            AlbumPerformer = cmd.GetOption("artist") ?? "Various Artists"
        };
        compilation.ImportCueSheet(cuePath, service.AudioDecoder);
        compilation.IncludeCdText = !cmd.HasFlag("no-cd-text");

        var result = await service.VerifyAsync(recorder, compilation, new AudioBurnOptions
        {
            UseDiscAtOnce = true,
            WriteCdText = compilation.IncludeCdText,
            KeepIndexMarks = true,
            WriteOffsetSamples = ParseWriteOffset(cmd.GetOption("write-offset"), native: true)
        }).ConfigureAwait(false);

        var v = engine.LastVerification;
        if (cmd.IsJson)
        {
            WriteJsonResult(stdout, new
            {
                success = result.Success,
                driveId = recorder.UniqueId,
                cueSheet = Path.GetFullPath(cuePath),
                message = v?.Message ?? result.ErrorMessage,
                offsetSamples = v?.OffsetSamples,
                samplesCompared = v?.SamplesCompared,
                hardToReadSectors = v?.HardToReadSectors ?? 0,
                writtenWrongSectors = v?.WrittenWrongSectors ?? 0,
                unreadableSectors = v?.UnreadableSectors ?? 0,
                warning = v?.Warning,
                writeOffset = engine.LastWriteOffsetPlan?.Explanation,
                commandLog = engine.LastLogPath,
                error = result.Success ? null : result.ErrorMessage
            });
            return result.Success ? 0 : 1;
        }

        (result.Success ? stdout : stderr).WriteLine(result.Success
            ? $"Disc check PASSED: {v?.Message}"
            : $"Disc check FAILED: {v?.Message ?? result.ErrorMessage}");
        if (v?.Warning is { } warning)
        {
            stdout.WriteLine($"  Warning: {warning}");
        }
        if (engine.LastLogPath != null)
        {
            stdout.WriteLine($"  Command log: {engine.LastLogPath}");
        }
        return result.Success ? 0 : 1;
    }

    private static async Task<int> HandleVerifyAsync(
        CommandLineArgs cmd,
        TextWriter stdout,
        TextWriter stderr,
        IDiscDeviceService deviceService,
        IBurnEngineBackend burnEngine)
    {
        string driveId = cmd.GetRequiredOption("drive", "--drive <id>");
        if (cmd.GetOption("cue") is { } cuePath)
        {
            return await HandleVerifyCueAsync(cmd, cuePath, FindRecorder(deviceService, driveId), stdout, stderr).ConfigureAwait(false);
        }
        string isoPath = cmd.GetRequiredOption("iso", "--iso <file.iso> or --cue <file.cue>");

        if (!File.Exists(isoPath))
        {
            throw new FileNotFoundException($"ISO image file not found: {isoPath}", isoPath);
        }

        var recorder = FindRecorder(deviceService, driveId);
        var verifier = new DiscVerificationService();

        // --strict-verify forces every read from the disc; without it the saved setting decides.
        bool? strict = cmd.HasFlag("strict-verify") ? true : null;
        VerificationResult result;
        if (LegacyDiscImageConverter.IsLegacyImageFile(isoPath))
        {
            await using var legacyStream = LegacyDiscImageConverter.OpenIsoUserDataStream(isoPath);
            result = await verifier.VerifyDiscAgainstStreamAsync(recorder, burnEngine, legacyStream, raw2352Mode: false, strict: strict).ConfigureAwait(false);
        }
        else
        {
            result = await verifier.VerifyDiscAgainstIsoFileAsync(recorder, burnEngine, isoPath, strict: strict).ConfigureAwait(false);
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

            WriteJsonResult(stdout, jsonResult);
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

            WriteJsonResult(stdout, jsonResult);
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

            WriteJsonResult(stdout, jsonResult);
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

    /// <summary>
    /// The write speed for a burn. No <c>--speed</c> (or <c>recommended</c>): <see cref="WriteSpeedPolicy.Recommended"/>,
    /// the fastest speed the drive writes at a constant linear velocity for this disc, up to 16x on CD and 8x on DVD.
    /// <c>slowest</c> and <c>max</c> take the ends of the drive's list. A number such as <c>8</c> or <c>8x</c> is sent
    /// as asked; the drive rounds it to a speed it supports. Returns null when the drive should keep its own speed
    /// (it reports no speeds).
    /// </summary>
    private static WriteSpeedDescriptor? ParseSpeed(string? speedStr, IDiscRecorderBackend recorder, DiscMediaType mediaType, bool isAudio = false)
    {
        string text = speedStr?.Trim() ?? string.Empty;
        int? preference = text.ToLowerInvariant() switch
        {
            "" or "recommended" => WriteSpeedPolicy.Recommended,
            "slowest" or "min" => WriteSpeedPolicy.Slowest,
            "max" or "maximum" => WriteSpeedPolicy.Maximum,
            _ => null
        };
        if (preference is { } p)
        {
            return WriteSpeedPolicy.Choose(WriteSpeedPolicy.ForDisc(DriveSpeeds(recorder), mediaType), p);
        }

        string cleaned = text.EndsWith('x') || text.EndsWith('X') ? text[..^1].Trim() : text; // one optional x: "8xx" is a typo
        if (double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out double multiplier) && multiplier > 0 && multiplier <= 100)
        {
            return WriteSpeedDescriptor.FromMultiplier(multiplier, mediaType, isAudio);
        }

        // Before, an unreadable value silently meant "fastest"; a typo must not change how a disc is burned.
        throw new CommandLineException($"--speed '{speedStr}' is not a speed. Use a number such as 8 or 8x, 'recommended' (the default), 'slowest' or 'max'.");
    }

    /// <summary>
    /// <c>--write-offset</c>: absent or <c>auto</c> works the drive's write offset out (read offset from AccurateRip's
    /// list, combined offset from earlier burns); a number of samples is used as given; 0 writes the audio unshifted.
    /// </summary>
    private static int? ParseWriteOffset(string? text, bool native)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        if (!native)
        {
            throw new CommandLineException("--write-offset needs --native: only the direct writer can correct the write offset.");
        }
        if (int.TryParse(text.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int samples) && Math.Abs(samples) <= 5_000)
        {
            return samples;
        }
        throw new CommandLineException($"--write-offset '{text}' is not a number of samples between -5000 and +5000, or 'auto'.");
    }

    /// <summary>The drive's write speeds; none when this kind of drive cannot list them (it then keeps its own speed).</summary>
    private static IReadOnlyList<WriteSpeedDescriptor> DriveSpeeds(IDiscRecorderBackend recorder)
    {
        try
        {
            return recorder.GetCapabilities().SupportedWriteSpeeds ?? [];
        }
        catch (NotSupportedException)
        {
            return [];
        }
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
        stdout.WriteLine(LegalBanner.VersionNotice);
        stdout.WriteLine();
        stdout.WriteLine("Usage: ignisdisc-cli <command> [options]");
        stdout.WriteLine();
        stdout.WriteLine("Supported Commands:");
        stdout.WriteLine("  drives [--json]");
        stdout.WriteLine("      Lists physical and virtual optical drives and capabilities.");
        stdout.WriteLine("  drive-report --drive <id> [--json]");
        stdout.WriteLine("      Generates a Markdown compatibility report matching GitHub issue template.");
        stdout.WriteLine("  media-info --drive <id> [--json]");
        stdout.WriteLine("      Shows inserted disc status, capacity, sessions, speeds.");
        stdout.WriteLine("  build-iso --input <folder> --output <file.iso> [--label <vol>] [--udf] [--udf-rev <1.02|1.50|2.00|2.01|2.50|2.60>] [--joliet] [--boot-bios <img>] [--boot-uefi <img>] [--fixed-time <unix|iso8601>] [--sha256sums] [--blake3sums] [--json]");
        stdout.WriteLine("      Compiles a folder into an ISO 9660 / Joliet / UDF filesystem image.");
        stdout.WriteLine("  burn-iso --drive <id> --iso <file.iso|.nrg|.mdf|.mds|.ccd|.img> [--speed <X|max>] [--no-verify | --strict-verify] [--no-close] [--overwrite] [--native [--test-write | --test-first] [--log-dir <folder>]] [--json]");
        stdout.WriteLine("      Burns an ISO image directly to the target optical drive.");
        stdout.WriteLine("  burn-data --drive <id> --input <folder> [--label <vol>] [--speed <X|max>] [--udf] [--udf-rev <1.02|1.50|2.00|2.01|2.50|2.60>] [--joliet] [--boot-bios <img>] [--boot-uefi <img>] [--no-verify | --strict-verify] [--no-close] [--overwrite] [--fixed-time <unix|iso8601>] [--sha256sums] [--blake3sums] [--native [--test-write | --test-first] [--log-dir <folder>]] [--json]");
        stdout.WriteLine("      Compiles a folder and burns it to optical media.");
        stdout.WriteLine("  burn-audio --drive <id> --tracks <file1,file2,...> [--title <album>] [--artist <name>] [--speed <X|max>] [--dao] [--allow-damaged] [--native [--test-write | --test-first] [--write-offset <samples|auto>] [--log-dir <folder>]] [--json]");
        stdout.WriteLine("      Burns Red Book Audio CD from WAV, FLAC, MP3, AAC/M4A, or WMA tracks.");
        stdout.WriteLine("  burn-dvd --drive <id> --video-ts <folder|movie> [--label <vol>] [--speed <X|max>] [--no-verify] [--overwrite] [--native [--test-write | --test-first] [--log-dir <folder>]] [--json]");
        stdout.WriteLine("      Validates, normalizes/authors, aligns, and burns a DVD-Video compliant disc from VIDEO_TS or movie files.");
        stdout.WriteLine("  rip --drive <id> --output <file.iso|file.bin> [--raw] [--json]");
        stdout.WriteLine("      Rips optical disc to an ISO or raw BIN+CUE image.");
        stdout.WriteLine("  rip-audio --drive <id> --output <folder> [--format wav|flac] [--tracks 1,3,5] [--lookup] [--keep-wav] [--continue-on-error] [--read-offset <samples>] [--verify-accuraterip] [--no-secure] [--json]");
        stdout.WriteLine("      Rips Red Book Audio CD to WAV or FLAC with CD-Text and optional MusicBrainz lookup. Exit code 2 when sectors may be wrong (still suspect or unreadable).");
        stdout.WriteLine("  copy-disc --source <id> [--target <id>] [--copies <n>] [--speed <X|max>] [--raw] [--native [--log-dir <folder>]] [--allow-suspect] [--keep-image <folder>] [--wait-minutes <n>] [--json]");
        stdout.WriteLine("      Copies a disc, with one drive or two. With one drive (or for each further copy) the tray opens and the copy waits for a blank disc.");
        stdout.WriteLine("      Audio is read with error checks; a copy stops before writing if sectors still read wrong, unless --allow-suspect. --native copies a mixed-mode CD's data track too.");
        stdout.WriteLine("  check-disc --drive <id> [--json]");
        stdout.WriteLine("      Reads a CD once with C2 error pointers and says where it is weak (where a player may skip). Read-only.");
        stdout.WriteLine("  detect-offset --drive <id> [--json]");
        stdout.WriteLine("      Finds the drive's audio read offset using an inserted CD that is in the AccurateRip database.");
        stdout.WriteLine("  archive-stage --input <folder> (--staging <folder> | --plan-only) [--media cd|dvd|dvd-dl|bd|bd-dl|bdxl|bdxl-ql] [--redundancy <min %>] [--no-fill] [--json]");
        stdout.WriteLine("      Splits a folder across discs and stages disc01..discNN with SHA-256 manifests and PAR2 recovery data.");
        stdout.WriteLine("  archive-verify --path <folder|drive> [--json]");
        stdout.WriteLine("  recover (--drive <id> | --image <file.iso>) --out <folder> [--retries N] [--keep-image] [--json]");
        stdout.WriteLine("      Checks an archive disc against its SHA-256 manifest.");
        stdout.WriteLine("  verify --drive <id> --iso <file.iso|.nrg|.mdf|.mds|.ccd|.img> [--strict-verify] [--json]");
        stdout.WriteLine("      Verifies disc sectors against an ISO image with SHA-256 and defect mapping.");
        stdout.WriteLine("  verify --drive <id> --cue <file.cue> [--title <album>] [--artist <name>] [--no-cd-text] [--write-offset <samples|auto>] [--log-dir <folder>] [--json]");
        stdout.WriteLine("      Checks a recorded audio or mixed-mode CD against the .cue it was burned from (track table, CD-Text, codes, every sample), without writing.");
        stdout.WriteLine("  erase --drive <id> [--full] [--json]");
        stdout.WriteLine("      Erases rewritable optical media (CD-RW, DVD-RW, DVD+RW, DVD-RAM).");
        stdout.WriteLine("  scan --drive <id> [--block-size <sectors>] [--csv <file.csv>] [--json]");
        stdout.WriteLine("      Scans optical disc surface and benchmarks transfer rate curve.");
        stdout.WriteLine();
        stdout.WriteLine("General Options:");
        stdout.WriteLine("  --help, -h       Show help information.");
        stdout.WriteLine("  --version, -v    Show version information.");
        stdout.WriteLine("  --json           Format output as machine-readable JSON.");
        stdout.WriteLine();
        stdout.WriteLine("Safe defaults (each can be turned off):");
        stdout.WriteLine("  Burns use the fastest speed the drive writes at a constant linear velocity for the disc, up to 16x on CD");
        stdout.WriteLine("  and 8x on DVD; --speed <X>, --speed slowest or --speed max to change it.");
        stdout.WriteLine("  Data and DVD-Video burns are read back and compared afterwards; --no-verify skips that.");
        stdout.WriteLine("  rip-audio reads with C2 error pointers and re-reads doubtful sectors; --no-secure reads once.");
        stdout.WriteLine("  burn-audio checks FLAC files first and stops if one is damaged; --allow-damaged burns anyway.");
    }

    private static void PrintSubcommandHelp(string? subcommand, TextWriter stdout)
    {
        if (string.Equals(subcommand, "drive-report", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(subcommand, "drivereport", StringComparison.OrdinalIgnoreCase))
        {
            stdout.WriteLine(LegalBanner.VersionNotice);
            stdout.WriteLine();
            stdout.WriteLine("  drive-report --drive <id> [--json]");
            stdout.WriteLine("      Generates a Markdown compatibility report matching GitHub issue template.");
            return;
        }
        if (string.Equals(subcommand, "rip-audio", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(subcommand, "ripaudio", StringComparison.OrdinalIgnoreCase))
        {
            stdout.WriteLine(LegalBanner.VersionNotice);
            stdout.WriteLine();
            stdout.WriteLine("  rip-audio --drive <id> --output <folder> [--format wav|flac] [--tracks 1,3,5] [--lookup] [--keep-wav] [--continue-on-error] [--read-offset <samples>] [--verify-accuraterip] [--no-secure] [--cover <image>] [--no-replaygain] [--json]");
            stdout.WriteLine("      Rips Red Book Audio CD to WAV or FLAC with CD-Text and optional MusicBrainz lookup. Exit code 2 when sectors may be wrong (still suspect or unreadable).");
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
