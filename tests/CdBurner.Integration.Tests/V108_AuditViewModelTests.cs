using System.IO;
using System.Text;
using CdBurner.App.ViewModels;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Audio;
using CdBurner.Core.Compilation;
using CdBurner.Core.Images;
using CdBurner.Core.Iso;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Devices;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Integration.Tests;

/// <summary>
/// v1.08 audit regressions at the view-model / service level: playlist import reporting and recursion, preview shutdown,
/// audio CUE verify-after-burn, disc-copy drive detection, media sizing and single-drive safety, and streaming legacy burns.
/// </summary>
public sealed class V108_AuditViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "IgnisDisc_V108_VM_" + Guid.NewGuid().ToString("N"));
    private readonly DiscDeviceManager _devices = new(enablePhysicalEnumeration: false);
    private readonly VirtualBurnEngine _engine = new();

    public V108_AuditViewModelTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _devices.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    #region Playlists & preview

    [Fact]
    public void AddPlaylist_WithOneMissingFile_ReportsAddedAndSkippedOnce_AndReindexes()
    {
        for (int i = 1; i <= 3; i++)
        {
            FlacFileCodec.WriteFlacFile(Path.Combine(_dir, $"t{i}.flac"), new byte[20 * OpticalConstants.RawSectorSize]);
        }

        string m3u = Path.Combine(_dir, "mix.m3u8");
        File.WriteAllText(m3u, "#EXTM3U\nt1.flac\nmissing.flac\nt2.flac\nt3.flac\n");

        var vm = new AudioBurnViewModel(_devices, _engine);
        vm.AddAudioFiles(new[] { m3u });

        Assert.Equal(3, vm.Tracks.Count);
        Assert.Equal(new[] { 1, 2, 3 }, vm.Tracks.Select(t => t.TrackNumber));
        Assert.Contains("Added 3 audio track(s)", vm.StatusMessage);
        Assert.Contains("Skipped 1", vm.StatusMessage);
    }

    [Fact]
    public void AddPlaylist_ThatIncludesItself_DoesNotRecurseForever()
    {
        FlacFileCodec.WriteFlacFile(Path.Combine(_dir, "loop.flac"), new byte[10 * OpticalConstants.RawSectorSize]);
        string m3u = Path.Combine(_dir, "self.m3u");
        File.WriteAllText(m3u, "self.m3u\nloop.flac\n");

        var vm = new AudioBurnViewModel(_devices, _engine);
        vm.AddAudioFiles(new[] { m3u });

        Assert.Single(vm.Tracks);
        Assert.Contains("Added 1", vm.StatusMessage);
    }

    [Fact]
    public void Dispose_WhilePreviewIsPlaying_StopsPreview()
    {
        string flac = Path.Combine(_dir, "preview.flac");
        FlacFileCodec.WriteFlacFile(flac, new byte[30 * OpticalConstants.RawSectorSize]);

        var vm = new AudioBurnViewModel(_devices, _engine);
        vm.AddAudioFiles(new[] { flac });
        vm.PreviewTrack();
        Assert.True(vm.IsPreviewPlaying);

        vm.Dispose();

        Assert.False(vm.IsPreviewPlaying);
        vm.Dispose(); // idempotent
    }

    #endregion

    #region Image Studio: audio CUE verify, legacy streaming

    [Fact]
    public async Task BurnAudioFlacCue_WithVerifyAfterBurn_SkipsVerificationInsteadOfFailing()
    {
        FlacFileCodec.WriteFlacFile(Path.Combine(_dir, "a.flac"), new byte[30 * OpticalConstants.RawSectorSize]);
        string cue = Path.Combine(_dir, "album.cue");
        File.WriteAllText(cue, "FILE \"a.flac\" WAVE\n  TRACK 01 AUDIO\n    INDEX 01 00:00:00\n");

        var vm = new ImageStudioViewModel(_devices, _engine) { VerifyAfterBurn = true };
        vm.SourceImagePath = cue;

        var burn = await vm.BurnImageToDiscAsync("VDRV-01");

        Assert.True(burn.Success, burn.ErrorMessage);
        Assert.NotNull(vm.LastVerificationResult);
        Assert.True(vm.LastVerificationResult!.IsSkipped);
        Assert.Contains("skipped", vm.VerificationStatusText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("FAILED", vm.StatusMessage);
    }

    [Fact]
    public async Task BurnAndVerifyMode2RawImg_StreamsUserDataAndMatches()
    {
        byte[] iso = await BuildIsoAsync(48_000);
        string img = Path.Combine(_dir, "xa.img");
        File.WriteAllBytes(img, WrapMode2(iso));

        var vm = new ImageStudioViewModel(_devices, _engine) { VerifyAfterBurn = true };
        vm.SourceImagePath = img;
        Assert.Equal(iso.Length / 2048, vm.ImageSectorCount);

        var burn = await vm.BurnImageToDiscAsync("VDRV-01");

        Assert.True(burn.Success, burn.ErrorMessage);
        Assert.True(vm.IsChecksumMatch, vm.VerificationStatusText);
    }

    #endregion

    #region Disc copy

    [Theory]
    [InlineData(DiscMediaType.CdR, 300_000, DiscMediaType.CdR)]
    [InlineData(DiscMediaType.CdRom, OpticalConstants.Cd700MbSectors + 1, DiscMediaType.DvdMinusR)]
    [InlineData(DiscMediaType.DvdMinusR, 2_100_000, DiscMediaType.DvdMinusR)]  // ~4.3 GB DVD
    [InlineData(DiscMediaType.DvdRom, 1_000, DiscMediaType.DvdMinusR)]          // small DVD is still a DVD
    [InlineData(DiscMediaType.DvdPlusRDualLayer, 3_500_000, DiscMediaType.DvdPlusRDualLayer)]
    public void BlankVirtualMedium_IsSizedForTheSourceDisc(DiscMediaType source, int usedSectors, DiscMediaType expected)
    {
        var medium = ImageStudioViewModel.CreateBlankVirtualMediumFor(new MediaStatusDto { MediaType = source, UsedSectors = usedSectors });
        Assert.Equal(expected, medium.MediaType);
        Assert.True(medium.TotalCapacitySectors >= usedSectors);
    }

    [Fact]
    public async Task CopyDvdOnSingleVirtualDrive_MountsBlankDvdNotCd()
    {
        var rec = (VirtualDiscRecorder)_devices.GetRecorders().First(r => r.UniqueId == "VDRV-01");
        byte[] iso = await BuildIsoAsync(64_000);
        int sectors = iso.Length / 2048;
        var dvd = VirtualDiscMedium.CreateBlankDvdR47();
        for (int i = 0; i < sectors; i++)
        {
            dvd.WriteSector(i, iso.AsSpan(i * 2048, 2048).ToArray(), 2048);
        }
        dvd.UsedSectors = sectors;
        dvd.IsFinalized = true;
        rec.InsertMedia(dvd);

        var vm = new ImageStudioViewModel(_devices, _engine)
        {
            CopySourceRecorder = rec,
            CopyTargetRecorder = rec,
            CopyCount = 1,
            CopyVerifyEachDisc = true
        };

        var result = await vm.CopyDiscAsync();

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(DiscMediaType.DvdMinusR, rec.CurrentMedium!.MediaType);
    }

    [Fact]
    public void IsSourceAudioCd_UsesPhysicalTableOfContents()
    {
        using var audio = new TocRecorder("PHYS-AUDIO", new[] { new DiscTocTrack(1, 0, 15000, IsAudio: true), new DiscTocTrack(2, 15000, 9000, IsAudio: true) });
        using var data = new TocRecorder("PHYS-DATA", new[] { new DiscTocTrack(1, 0, 300000, IsAudio: false) });

        Assert.True(DiscCopyService.IsSourceAudioCd(audio));
        Assert.False(DiscCopyService.IsSourceAudioCd(data));
    }

    [Fact]
    public async Task CopyService_SingleDriveOrMultiCopyWithoutSwapPrompt_FailsBeforeTouchingTheSource()
    {
        var rec = (VirtualDiscRecorder)_devices.GetRecorders().First(r => r.UniqueId == "VDRV-01");
        byte[] iso = await BuildIsoAsync(8_000);
        var cd = VirtualDiscMedium.CreateBlankCdR700();
        for (int i = 0; i < iso.Length / 2048; i++) cd.WriteSector(i, iso.AsSpan(i * 2048, 2048).ToArray(), 2048);
        cd.UsedSectors = iso.Length / 2048;
        cd.IsFinalized = true;
        rec.InsertMedia(cd);

        var service = new DiscCopyService(_engine);

        var single = await service.CopyDiscAsync(rec, rec, new DiscCopyOptions { CopyCount = 1 });
        Assert.False(single.Success);
        Assert.Contains("Single-drive", single.ErrorMessage);
        Assert.Same(cd, rec.CurrentMedium); // the source disc was not overwritten

        using var second = new VirtualDiscRecorder("VDRV-COPY-TARGET");
        second.InsertMedia(VirtualDiscMedium.CreateBlankCdR700());
        var multi = await service.CopyDiscAsync(rec, second, new DiscCopyOptions { CopyCount = 2 });
        Assert.False(multi.Success);
        Assert.Contains("Multi-copy", multi.ErrorMessage);
    }

    private sealed class TocRecorder(string id, IReadOnlyList<DiscTocTrack> toc) : VirtualDiscRecorder(id), IDiscTocProvider
    {
        public IReadOnlyList<DiscTocTrack> ReadTableOfContents() => toc;
    }

    #endregion

    #region Helpers

    private static async Task<byte[]> BuildIsoAsync(int payloadBytes)
    {
        var root = CompilationDirectory.CreateRoot();
        root.AddFile("DATA.BIN", Encoding.ASCII.GetBytes(new string('z', payloadBytes)));
        using var ms = new MemoryStream();
        await IsoUdfImageBuilder.BuildAsync(root, ms, new IsoBuildOptions(VolumeLabel: "V108"));
        return ms.ToArray();
    }

    private static byte[] WrapMode2(byte[] iso)
    {
        int sectors = iso.Length / 2048;
        var raw = new byte[sectors * 2352];
        for (int s = 0; s < sectors; s++)
        {
            var sec = raw.AsSpan(s * 2352, 2352);
            sec.Slice(1, 10).Fill(0xFF);
            sec[15] = 2;
            sec[18] = 0x08;
            sec[22] = 0x08;
            iso.AsSpan(s * 2048, 2048).CopyTo(sec.Slice(24));
        }
        return raw;
    }

    #endregion
}
