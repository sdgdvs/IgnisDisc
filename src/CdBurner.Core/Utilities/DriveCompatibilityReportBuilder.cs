using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Primitives;

namespace CdBurner.Core.Utilities;

/// <summary>
/// Builds a GitHub-ready markdown compatibility report for optical drives and media,
/// matching the issue template in .github/ISSUE_TEMPLATE/drive_report.yml.
/// </summary>
public static class DriveCompatibilityReportBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Builds a formatted Markdown drive compatibility report ready to copy into GitHub issue reports.
    /// </summary>
    /// <param name="recorder">The optical recorder backend to inspect.</param>
    /// <param name="capabilities">Optional pre-queried drive capabilities.</param>
    /// <param name="media">Optional pre-queried media status.</param>
    /// <param name="appVersion">Optional application version string (e.g. "1.10").</param>
    public static string BuildMarkdownReport(
        IDiscRecorderBackend recorder,
        DriveCapabilitiesDto? capabilities = null,
        MediaStatusDto? media = null,
        string? appVersion = null)
    {
        ArgumentNullException.ThrowIfNull(recorder);

        capabilities ??= recorder.GetCapabilities();
        media ??= recorder.WithManufacturerId(recorder.QueryMediaStatus());

        if (string.IsNullOrWhiteSpace(appVersion))
        {
            appVersion = typeof(DriveCompatibilityReportBuilder).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
                ?? "unknown";
        }

        bool isFinalized = media.MediaPresent && !media.IsBlank && !media.IsAppendable && !media.IsOverwriteOnly;
        long usedBytes = media.UsedSectors > 0
            ? (long)media.UsedSectors * OpticalConstants.DataSectorSize
            : Math.Max(0L, media.TotalCapacityBytes - media.FreeCapacityBytes);

        bool canWriteCd = capabilities.CanWriteCdR || capabilities.CanWriteCdRw;
        bool canWriteDvd = capabilities.CanWriteDvdR || capabilities.CanWriteDvdRw || capabilities.CanWriteDvdPlusR || capabilities.CanWriteDvdPlusRw || capabilities.CanWriteDvdRam;
        bool canWriteDl = capabilities.CanWriteDvdPlusRDL || capabilities.CanWriteDvdMinusRDL;
        bool canReadCd = capabilities.SupportedMediaTypes.Any(m => m.IsCd());
        bool canReadDvd = capabilities.SupportedMediaTypes.Any(m => m.IsDvd());

        string speedsStr = capabilities.SupportedWriteSpeeds.Count > 0
            ? string.Join(", ", capabilities.SupportedWriteSpeeds.Select(s => $"{s.Multiplier:0.##}x"))
            : "Default / Auto";

        string mediaStatusText = !media.MediaPresent
            ? "No media inserted"
            : media.IsBlank
                ? "Blank"
                : media.IsRewritable
                    ? "Rewritable"
                    : media.IsAppendable
                        ? "Appendable"
                        : "Finalized / Recorded";

        // Generate JSON snippets matching CLI outputs
        var drivesObj = new
        {
            success = true,
            drives = new[]
            {
                new
                {
                    vendorId = recorder.VendorId,
                    productId = recorder.ProductId,
                    productRevision = recorder.ProductRevision,
                    volumePath = recorder.VolumePath,
                    isVirtual = recorder.IsVirtual,
                    capabilities = new
                    {
                        canWriteCd = canWriteCd,
                        canWriteDvd = canWriteDvd,
                        canWriteDualLayer = canWriteDl,
                        canReadCd = canReadCd,
                        canReadDvd = canReadDvd,
                        supportedMediaTypes = capabilities.SupportedMediaTypes.Select(m => m.ToString()).ToList(),
                        mmcFeatures = capabilities.MmcFeatureSummary,
                        supportsNativeTao = capabilities.SupportsNativeTao,
                        supportsNativeSao = capabilities.SupportsNativeSao,
                        supportsNativeRawDao = capabilities.SupportsNativeRawDao,
                        supportsNativeCdText = capabilities.SupportsNativeCdText,
                        supportsTestWrite = capabilities.SupportsTestWrite,
                        supportsBufferUnderrunFree = capabilities.SupportsBufferUnderrunFree,
                        supportsLayerJump = capabilities.SupportsLayerJump,
                        supportedWriteSpeeds = capabilities.SupportedWriteSpeeds.Select(s => new
                        {
                            multiplier = s.Multiplier,
                            bytesPerSecond = s.BytesPerSecond,
                            mediaType = s.MediaType.ToString()
                        }).ToList()
                    }
                }
            }
        };

        var mediaInfoObj = new
        {
            success = true,
            vendorId = recorder.VendorId,
            productId = recorder.ProductId,
            productRevision = recorder.ProductRevision,
            volumePath = recorder.VolumePath,
            isVirtual = recorder.IsVirtual,
            mediaPresent = media.MediaPresent,
            mediaType = media.MediaType.ToString(),
            mediaTypeName = media.MediaType.GetDisplayName(),
            mediaManufacturerId = media.MediaManufacturerId,
            mmcFeatures = capabilities.MmcFeatureSummary,
            isBlank = media.IsBlank,
            isRewritable = media.IsRewritable,
            isAppendable = media.IsAppendable,
            isFinalized = isFinalized,
            isOverwriteOnly = media.IsOverwriteOnly,
            totalCapacityBytes = media.TotalCapacityBytes,
            freeCapacityBytes = media.FreeCapacityBytes,
            usedCapacityBytes = usedBytes,
            totalSectors = media.TotalSectors,
            freeSectors = media.FreeSectors,
            usedSectors = media.UsedSectors,
            sessionCount = media.SessionCount,
            lastRecordedLba = media.LastRecordedLba,
            nextWritableLba = media.NextWritableLba,
            supportedWriteSpeeds = capabilities.SupportedWriteSpeeds.Select(s => new
            {
                multiplier = s.Multiplier,
                bytesPerSecond = s.BytesPerSecond,
                mediaType = s.MediaType.ToString()
            }).ToList()
        };

        string drivesJson = JsonSerializer.Serialize(drivesObj, JsonOptions);
        string mediaInfoJson = JsonSerializer.Serialize(mediaInfoObj, JsonOptions);

        return $"""
# IgnisDisc Drive Compatibility Report

### IgnisDisc version
{appVersion}

### Drive (vendor, model, firmware) and connection
- **Vendor:** {recorder.VendorId}
- **Model / Product:** {recorder.ProductId}
- **Firmware Revision:** {recorder.ProductRevision}
- **Volume Path:** {recorder.VolumePath}
- **Device Type:** {(recorder.IsVirtual ? "Virtual Simulator" : "Physical Optical Hardware")}

### Disc type and brand
- **Media Present:** {(media.MediaPresent ? "Yes" : "No")}
- **Media Type:** {media.MediaType.GetDisplayName()} ({media.MediaType})
- **Media ID / ATIP:** {(string.IsNullOrWhiteSpace(media.MediaManufacturerId) ? "N/A" : media.MediaManufacturerId)}
- **Status:** {mediaStatusText}
- **Total Capacity:** {media.TotalCapacityBytes:N0} bytes ({media.TotalSectors:N0} sectors)
- **Free Capacity:** {media.FreeCapacityBytes:N0} bytes ({media.FreeSectors:N0} sectors)
- **Used Capacity:** {usedBytes:N0} bytes ({media.UsedSectors:N0} sectors)
- **Sessions:** {media.SessionCount}

### Capabilities summary
- **CD Writing:** {(canWriteCd ? "Supported" : "Unsupported")} (CD-R: {capabilities.CanWriteCdR}, CD-RW: {capabilities.CanWriteCdRw})
- **DVD Writing:** {(canWriteDvd ? "Supported" : "Unsupported")} (DVD-R: {capabilities.CanWriteDvdR}, DVD+R: {capabilities.CanWriteDvdPlusR})
- **Dual Layer Writing:** {(canWriteDl ? "Supported" : "Unsupported")}
- **Supported Write Speeds:** {speedsStr}

### What did you do?
- [ ] Burn data disc / ISO
- [ ] Burn audio CD
- [ ] Rip audio CD
- [ ] Rip data disc
- [ ] Disc copy
- [ ] Burn legacy image (.nrg / .mdf / .ccd)
- [ ] Erase
- [ ] Surface scan
- [ ] Archive (multi-disc)
- [ ] DVD-Video
- [ ] Other

### Result
- [ ] Worked
- [ ] Worked with problems
- [ ] Failed

### Details
<!-- What happened? Include any error message shown. -->

### Drive and media information

#### `ignisdisc-cli drives --json`
```json
{drivesJson}
```

#### `ignisdisc-cli media-info --json`
```json
{mediaInfoJson}
```
""";
    }
}
