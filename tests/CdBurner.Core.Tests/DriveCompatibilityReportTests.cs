using System.Text.Json;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Primitives;
using CdBurner.Core.Utilities;
using Xunit;

namespace CdBurner.Core.Tests;

public class DriveCompatibilityReportTests
{
    private sealed class MockRecorder : IDiscRecorderBackend
    {
        public string UniqueId { get; set; } = "DRV-REAL-01";
        public string VendorId { get; set; } = "HL-DT-ST";
        public string ProductId { get; set; } = "BD-RE WH16NS40";
        public string ProductRevision { get; set; } = "1.05";
        public string VolumePath { get; set; } = @"E:\";
        public bool IsVirtual => false;

        public DriveCapabilitiesDto Caps { get; set; } = new()
        {
            CanWriteCdR = true,
            CanWriteCdRw = true,
            CanWriteDvdR = true,
            CanWriteDvdRw = true,
            CanWriteDvdPlusR = true,
            CanWriteDvdPlusRw = true,
            CanWriteDvdPlusRDL = true,
            CanWriteDvdMinusRDL = true,
            CanWriteDvdRam = true,
            SupportedMediaTypes = new[]
            {
                DiscMediaType.CdR,
                DiscMediaType.CdRw,
                DiscMediaType.DvdMinusR,
                DiscMediaType.DvdMinusRw,
                DiscMediaType.DvdPlusR,
                DiscMediaType.DvdPlusRw,
                DiscMediaType.DvdPlusRDualLayer
            },
            SupportedWriteSpeeds = new[]
            {
                new WriteSpeedDescriptor(2457600, DiscMediaType.CdR),
                new WriteSpeedDescriptor(22160000, DiscMediaType.DvdPlusR)
            }
        };

        public MediaStatusDto Media { get; set; } = new()
        {
            MediaPresent = true,
            IsBlank = true,
            IsRewritable = false,
            IsAppendable = false,
            IsOverwriteOnly = false,
            TotalCapacityBytes = 4700000000,
            FreeCapacityBytes = 4700000000,
            TotalSectors = 2295104,
            FreeSectors = 2295104,
            UsedSectors = 0,
            SessionCount = 0,
            LastRecordedLba = -1,
            NextWritableLba = 0,
            MediaType = DiscMediaType.DvdMinusR
        };

        public bool AcquireExclusiveAccess(string clientName) => true;
        public void CloseTray() { }
        public void EjectMedia() { }
        public DriveCapabilitiesDto GetCapabilities() => Caps;
        public MediaStatusDto QueryMediaStatus() => Media;
        public MediaStatusDto ReadFullToc() => Media;
        public void ReleaseExclusiveAccess() { }
        public void Dispose() { }
    }

    [Fact]
    public void BuildMarkdownReport_GeneratesReport_WithAllRequiredSectionsMatchingIssueTemplate()
    {
        var recorder = new MockRecorder();

        string markdown = DriveCompatibilityReportBuilder.BuildMarkdownReport(
            recorder,
            appVersion: "1.10");

        Assert.NotNull(markdown);
        Assert.Contains("# IgnisDisc Drive Compatibility Report", markdown);
        Assert.Contains("### IgnisDisc version", markdown);
        Assert.Contains("1.10", markdown);

        // Drive info
        Assert.Contains("HL-DT-ST", markdown);
        Assert.Contains("BD-RE WH16NS40", markdown);
        Assert.Contains("1.05", markdown);
        Assert.Contains(@"E:\", markdown);
        Assert.DoesNotContain("`DRV-REAL-01`", markdown);
        Assert.Contains("Physical Optical Hardware", markdown);

        // Media info
        Assert.Contains("DVD-R", markdown);
        Assert.Contains("Blank", markdown);
        Assert.Contains("4,700,000,000 bytes", markdown);

        // Capabilities summary
        Assert.Contains("CD Writing:", markdown);
        Assert.Contains("DVD Writing:", markdown);
        Assert.Contains("Dual Layer Writing:", markdown);
        Assert.Contains("Supported Write Speeds:", markdown);

        // Checklist options
        Assert.Contains("### What did you do?", markdown);
        Assert.Contains("- [ ] Burn data disc / ISO", markdown);
        Assert.Contains("- [ ] Burn audio CD", markdown);
        Assert.Contains("- [ ] Rip audio CD", markdown);
        Assert.Contains("- [ ] Disc copy", markdown);
        Assert.Contains("### Result", markdown);
        Assert.Contains("- [ ] Worked", markdown);
        Assert.Contains("### Details", markdown);

        // CLI diagnostic JSON snippets
        Assert.Contains("#### `ignisdisc-cli drives --json`", markdown);
        Assert.Contains("#### `ignisdisc-cli media-info --json`", markdown);
        // The report is pasted into public issues: no device instance path (it can carry a USB serial number).
        Assert.DoesNotContain("DRV-REAL-01", markdown);

        // Verify JSON snippet parses validly
        int startDrives = markdown.IndexOf("```json");
        Assert.True(startDrives > 0);
        int endDrives = markdown.IndexOf("```", startDrives + 7);
        string drivesJson = markdown.Substring(startDrives + 7, endDrives - (startDrives + 7)).Trim();

        using var drivesDoc = JsonDocument.Parse(drivesJson);
        Assert.True(drivesDoc.RootElement.GetProperty("success").GetBoolean());
        var drivesArr = drivesDoc.RootElement.GetProperty("drives");
        Assert.Equal(1, drivesArr.GetArrayLength());
        Assert.False(drivesArr[0].TryGetProperty("uniqueId", out _));

        int startMedia = markdown.IndexOf("```json", endDrives + 3);
        Assert.True(startMedia > 0);
        int endMedia = markdown.IndexOf("```", startMedia + 7);
        string mediaJson = markdown.Substring(startMedia + 7, endMedia - (startMedia + 7)).Trim();

        using var mediaDoc = JsonDocument.Parse(mediaJson);
        Assert.True(mediaDoc.RootElement.GetProperty("success").GetBoolean());
        Assert.False(mediaDoc.RootElement.TryGetProperty("driveId", out _));
        Assert.True(mediaDoc.RootElement.GetProperty("mediaPresent").GetBoolean());
    }

    [Fact]
    public void BuildMarkdownReport_WithNoMedia_FormatsNoMediaStateGracefully()
    {
        var recorder = new MockRecorder();
        recorder.Media = new MediaStatusDto
        {
            MediaPresent = false,
            MediaType = DiscMediaType.NoMedia
        };

        string markdown = DriveCompatibilityReportBuilder.BuildMarkdownReport(recorder);

        Assert.Contains("- **Media Present:** No", markdown);
        Assert.Contains("No media inserted", markdown);
    }

    [Fact]
    public void BuildMarkdownReport_DrivesJson_IncludesNativeWriteCapabilities()
    {
        var recorder = new MockRecorder();
        recorder.Caps = new DriveCapabilitiesDto
        {
            CanWriteCdR = true,
            SupportsNativeTao = true,
            SupportsNativeSao = true,
            SupportsNativeCdText = false,
            SupportsTestWrite = true,
            SupportsBufferUnderrunFree = true,
            MmcFeatureSummary = new[] { "0x002D TAO", "0x002E SAO/Raw" }
        };

        string markdown = DriveCompatibilityReportBuilder.BuildMarkdownReport(recorder, appVersion: "1.11");

        int start = markdown.IndexOf("```json");
        int end = markdown.IndexOf("```", start + 7);
        using var doc = JsonDocument.Parse(markdown.Substring(start + 7, end - (start + 7)).Trim());
        var caps = doc.RootElement.GetProperty("drives")[0].GetProperty("capabilities");

        Assert.True(caps.GetProperty("supportsNativeTao").GetBoolean());
        Assert.True(caps.GetProperty("supportsNativeSao").GetBoolean());
        Assert.False(caps.GetProperty("supportsNativeCdText").GetBoolean());
        Assert.False(caps.GetProperty("supportsNativeRawDao").GetBoolean());
        Assert.True(caps.GetProperty("supportsTestWrite").GetBoolean());
        Assert.True(caps.GetProperty("supportsBufferUnderrunFree").GetBoolean());
        Assert.Equal(2, caps.GetProperty("mmcFeatures").GetArrayLength());
    }
}
