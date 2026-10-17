using CdBurner.Core.Abstractions;
using CdBurner.Core.Primitives;
using Xunit;

namespace CdBurner.Core.Tests;

public class Phase01_DomainPrimitivesTests
{
    [Fact]
    public void MsfAddress_FromLba_Zero_Equals_TwoSeconds()
    {
        var msf = MsfAddress.FromLba(0);

        Assert.Equal(0, msf.Minutes);
        Assert.Equal(2, msf.Seconds);
        Assert.Equal(0, msf.Frames);
        Assert.Equal("00:02:00", msf.ToString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(150)]
    [InlineData(359999)]
    [InlineData(-150)]
    [InlineData(1)]
    [InlineData(74)]
    [InlineData(75)]
    [InlineData(405000)]
    public void MsfAddress_ToLba_RoundTrips(int lba)
    {
        var msf = MsfAddress.FromLba(lba);
        int roundTripLba = msf.ToLba();

        Assert.Equal(lba, roundTripLba);
    }

    [Fact]
    public void MsfAddress_TryParse_ValidString_Succeeds()
    {
        bool success = MsfAddress.TryParse("79:59:74", out var msf);

        Assert.True(success);
        Assert.Equal(79, msf.Minutes);
        Assert.Equal(59, msf.Seconds);
        Assert.Equal(74, msf.Frames);
        Assert.Equal("79:59:74", msf.ToString());
    }

    [Theory]
    [InlineData("00:60:00")] // Invalid seconds (>= 60)
    [InlineData("00:00:75")] // Invalid frames (>= 75)
    [InlineData("00:-1:00")] // Negative seconds
    [InlineData("-1:00:00")] // Negative minutes
    [InlineData("00:00:-1")] // Negative frames
    [InlineData("invalid")]  // Non-numeric
    [InlineData("10:20")]    // Missing part
    [InlineData("10:20:30:40")] // Too many parts
    [InlineData("")]         // Empty
    [InlineData(null)]       // Null
    public void MsfAddress_TryParse_InvalidInput_ReturnsFalse(string? input)
    {
        bool success = MsfAddress.TryParse(input, out var msf);

        Assert.False(success);
        Assert.Equal(default, msf);
    }

    [Fact]
    public void MsfAddress_Constructor_ThrowsOnOutOfRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MsfAddress(-1, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MsfAddress(0, -1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MsfAddress(0, 60, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MsfAddress(0, 0, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MsfAddress(0, 0, 75));
    }

    [Fact]
    public void MsfAddress_FromLba_ThrowsWhenPrecedingLeadIn()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MsfAddress.FromLba(-151));
    }

    [Fact]
    public void MsfAddress_ArithmeticAndComparison_WorksAccurately()
    {
        var msf1 = new MsfAddress(0, 2, 0); // LBA 0 (150 frames)
        var msf2 = msf1 + 75; // +1 second

        Assert.Equal("00:03:00", msf2.ToString());
        Assert.True(msf2 > msf1);
        Assert.True(msf1 < msf2);
        Assert.Equal(75, msf2 - msf1);

        var msf3 = msf2 - 75;
        Assert.Equal(msf1, msf3);
    }

    [Fact]
    public void LbaAddress_Conversions_And_Operations_Work()
    {
        var lba = new LbaAddress(0);
        Assert.Equal(new MsfAddress(0, 2, 0), lba.ToMsf());
        Assert.Equal(0L, lba.ToByteOffset());

        var lba100 = new LbaAddress(100);
        Assert.Equal(204800L, lba100.ToByteOffset());
        Assert.Equal(lba100, LbaAddress.FromByteOffset(204800L));

        var lbaFromMsf = LbaAddress.FromMsf(new MsfAddress(0, 2, 0));
        Assert.Equal(0, lbaFromMsf.Value);

        Assert.True(lba100 > lba);
        Assert.Equal(100, lba100 - lba);
        Assert.Equal(new LbaAddress(150), lba100 + 50);
    }

    [Fact]
    public void WriteSpeedDescriptor_Converts_48X_CdData_Accurately()
    {
        // 48X CD Data = 48 * 153,600 = 7,372,800 bytes/sec
        var descriptorFromRate = new WriteSpeedDescriptor(7_372_800, DiscMediaType.CdR);
        Assert.Equal(48.0, descriptorFromRate.Multiplier, precision: 4);
        Assert.Equal(7_372_800, descriptorFromRate.BytesPerSecond);

        var descriptorFromMultiplier = WriteSpeedDescriptor.FromMultiplier(48.0, DiscMediaType.CdR);
        Assert.Equal(7_372_800, descriptorFromMultiplier.BytesPerSecond);
        Assert.Equal(48.0, descriptorFromMultiplier.Multiplier, precision: 4);
    }

    [Fact]
    public void WriteSpeedDescriptor_Converts_16X_Dvd_Accurately()
    {
        // 16X DVD = 16 * 1,385,000 = 22,160,000 bytes/sec
        var descriptorFromRate = new WriteSpeedDescriptor(22_160_000, DiscMediaType.DvdPlusR);
        Assert.Equal(16.0, descriptorFromRate.Multiplier, precision: 4);
        Assert.Equal(22_160_000, descriptorFromRate.BytesPerSecond);

        var descriptorFromMultiplier = WriteSpeedDescriptor.FromMultiplier(16.0, DiscMediaType.DvdPlusR);
        Assert.Equal(22_160_000, descriptorFromMultiplier.BytesPerSecond);
        Assert.Equal(16.0, descriptorFromMultiplier.Multiplier, precision: 4);
    }

    [Fact]
    public void OpticalConstants_NominalCapacities_MatchExpectedValues()
    {
        // CD 700MB: 360,000 sectors * 2048 = 737,280,000 bytes
        Assert.Equal(360_000, OpticalConstants.Cd700MbSectors);
        Assert.Equal(737_280_000L, OpticalConstants.Cd700MbBytes);

        // CD 650MB: 333,000 sectors * 2048 = 681,984,000 bytes
        Assert.Equal(333_000, OpticalConstants.Cd650MbSectors);
        Assert.Equal(681_984_000L, OpticalConstants.Cd650MbBytes);

        // CD 90Min: 405,000 sectors * 2048 = 829,440,000 bytes
        Assert.Equal(405_000, OpticalConstants.Cd90MinSectors);
        Assert.Equal(829_440_000L, OpticalConstants.Cd90MinBytes);

        // DVD+R SL: 2,295,104 sectors * 2048 = 4,700,372,992 bytes
        Assert.Equal(2_295_104, OpticalConstants.DvdPlusR5Sectors);
        Assert.Equal(4_700_372_992L, OpticalConstants.DvdPlusR5Bytes);

        // DVD-R SL: 2,298,496 sectors * 2048 = 4,707,319,808 bytes
        Assert.Equal(2_298_496, OpticalConstants.DvdMinusR5Sectors);
        Assert.Equal(4_707_319_808L, OpticalConstants.DvdMinusR5Bytes);

        // DVD-R DL: 4,171,712 sectors * 2048 = 8,543,666,176 bytes
        Assert.Equal(4_171_712, OpticalConstants.DvdMinusR9Sectors);
        Assert.Equal(8_543_666_176L, OpticalConstants.DvdMinusR9Bytes);

        // DVD+R DL: 4,173,824 sectors * 2048 = 8,547,991,552 bytes
        Assert.Equal(4_173_824, OpticalConstants.DvdPlusR9Sectors);
        Assert.Equal(8_547_991_552L, OpticalConstants.DvdPlusR9Bytes);

        // Sector geometry & ECC blocks
        Assert.Equal(2048, OpticalConstants.DataSectorSize);
        Assert.Equal(2352, OpticalConstants.RawSectorSize);
        Assert.Equal(75, OpticalConstants.AudioFramesPerSecond);
        Assert.Equal(16, OpticalConstants.DvdEccBlockSectors);
        Assert.Equal(32768, OpticalConstants.DvdEccBlockBytes);
    }

    [Fact]
    public void DiscMediaType_Extensions_ClassifyCorrectly()
    {
        Assert.True(DiscMediaType.CdR.IsCd());
        Assert.True(DiscMediaType.CdRw.IsCd());
        Assert.False(DiscMediaType.DvdMinusR.IsCd());

        Assert.True(DiscMediaType.DvdPlusR.IsDvd());
        Assert.True(DiscMediaType.DvdMinusRDualLayer.IsDvd());
        Assert.False(DiscMediaType.CdR.IsDvd());

        Assert.True(DiscMediaType.CdRw.IsRewritable());
        Assert.True(DiscMediaType.DvdPlusRw.IsRewritable());
        Assert.False(DiscMediaType.CdR.IsRewritable());

        Assert.True(DiscMediaType.DvdPlusRDualLayer.IsDualLayer());
        Assert.True(DiscMediaType.DvdMinusRDualLayer.IsDualLayer());
        Assert.False(DiscMediaType.DvdPlusR.IsDualLayer());

        Assert.Equal(OpticalConstants.Cd700MbBytes, DiscMediaType.CdR.GetNominalCapacityBytes());
        Assert.Equal(OpticalConstants.DvdPlusR5Bytes, DiscMediaType.DvdPlusR.GetNominalCapacityBytes());
    }

    [Fact]
    public void Abstractions_DataModels_InstantiateProperly()
    {
        var progress = new BurnProgressSnapshot
        {
            CurrentLba = 180000,
            TotalLba = 360000,
            CurrentBytesPerSecond = 7372800,
            CurrentSpeedMultiplier = 48.0,
            BufferLevelPercent = 100.0,
            ElapsedTime = TimeSpan.FromSeconds(30),
            RemainingTime = TimeSpan.FromSeconds(30),
            CurrentActionDescription = "Writing sectors"
        };
        Assert.Equal(50.0, progress.ProgressPercentage);

        var burnResult = BurnOperationResult.Succeeded(360000, TimeSpan.FromSeconds(60), 48.0);
        Assert.True(burnResult.Success);
        Assert.Equal(360000, burnResult.SectorsWritten);

        var ripResult = RipOperationResult.Succeeded(360000, TimeSpan.FromSeconds(60));
        Assert.True(ripResult.Success);
        Assert.Equal(0, ripResult.BadSectorsCount);

        var dataOptions = new DataBurnOptions
        {
            Simulate = false,
            EjectWhenDone = true,
            VolumeLabel = "MY_DISC"
        };
        Assert.True(dataOptions.CloseDisc);
        Assert.True(dataOptions.EjectWhenDone);
        Assert.Equal("MY_DISC", dataOptions.VolumeLabel);

        var audioOptions = new AudioBurnOptions
        {
            UseDiscAtOnce = true,
            WriteCdText = true
        };
        Assert.True(audioOptions.UseDiscAtOnce);
        Assert.Equal(OpticalConstants.CdLeadInFrames, audioOptions.PreGapFrames);

        var track = new AudioTrackSource
        {
            TrackNumber = 1,
            FilePath = "track01.wav",
            Title = "Opening Theme",
            Performer = "Artist",
            Duration = TimeSpan.FromMinutes(3)
        };
        Assert.Equal(1, track.TrackNumber);
        Assert.Equal("Opening Theme", track.Title);
    }
}
