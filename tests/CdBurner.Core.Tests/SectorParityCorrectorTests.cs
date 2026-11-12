using CdBurner.Core.Images;
using Xunit;

namespace CdBurner.Core.Tests;

/// <summary>
/// Raw CD-ROM sectors repaired with their P/Q parity (ECMA-130 Annex A). One wrong byte per codeword is inside the
/// parity bound and must come back exact; damage beyond it must be reported, never returned as good.
/// (Audit findings F1 and F2, 2026-11-09.)
/// </summary>
public class SectorParityCorrectorTests
{
    private static byte[] Payload(int seed)
    {
        var data = new byte[RawSectorCodec.UserDataSectorSize];
        new Random(seed).NextBytes(data);
        return data;
    }

    private static byte[] Mode1(byte[] payload, int lba = 1234) => RawSectorCodec.Convert2048To2352Mode1(payload, lba);

    private static byte[] Mode2(byte[] payload, int lba = 1234)
    {
        var raw = new byte[RawSectorCodec.RawSectorSize];
        RawSectorCodec.Convert2048To2352Mode2Xa(payload, lba, raw);
        return raw;
    }

    [Fact]
    public void IntactSectors_HaveNoParityViolations()
    {
        Assert.Equal(0, SectorParityCorrector.Violations(Mode1(Payload(1))));
        var mode2 = Mode2(Payload(2));
        mode2.AsSpan(12, 4).Clear(); // Form 1 parity is over a zeroed header
        Assert.Equal(0, SectorParityCorrector.Violations(mode2));
    }

    [Fact]
    public void Mode1_AnySingleWrongByte_IsRepairedExactly()
    {
        var rng = new Random(7);
        for (int trial = 0; trial < 400; trial++)
        {
            byte[] payload = Payload(trial);
            byte[] raw = Mode1(payload);
            int position = rng.Next(12, RawSectorCodec.RawSectorSize); // everything after the sync is protected
            raw[position] ^= (byte)rng.Next(1, 256);

            byte[] data = RawSectorCodec.Extract2048From2352(raw, out bool valid);

            Assert.True(valid, $"trial {trial}, byte {position}");
            Assert.Equal(payload, data);
        }
    }

    [Fact]
    public void Mode1_TwoWrongBytesInDifferentCodewords_AreRepaired()
    {
        byte[] payload = Payload(3);
        byte[] raw = Mode1(payload);
        raw[16] ^= 0x5A;   // plane 0
        raw[1001] ^= 0xC3; // plane 1

        Assert.Equal(payload, RawSectorCodec.Extract2048From2352(raw, out bool valid));
        Assert.True(valid);
    }

    [Fact]
    public void Mode2Form1_SingleWrongByte_IsRepairedAndTheHeaderKept()
    {
        var rng = new Random(11);
        for (int trial = 0; trial < 200; trial++)
        {
            byte[] payload = Payload(100 + trial);
            byte[] raw = Mode2(payload);
            int position = rng.Next(16, RawSectorCodec.RawSectorSize); // header bytes are outside Form 1 parity
            raw[position] ^= (byte)rng.Next(1, 256);

            byte[] data = RawSectorCodec.Extract2048From2352(raw, out bool valid);

            Assert.True(valid, $"trial {trial}, byte {position}");
            Assert.Equal(payload, data);
        }
    }

    [Fact]
    public void HeavyDamage_IsNeverReportedAsGood()
    {
        var rng = new Random(13);
        for (int trial = 0; trial < 200; trial++)
        {
            byte[] payload = Payload(500 + trial);
            byte[] raw = trial % 2 == 0 ? Mode1(payload) : Mode2(payload);
            int errors = rng.Next(3, 400);
            for (int i = 0; i < errors; i++)
            {
                raw[rng.Next(24, 2064)] ^= (byte)rng.Next(1, 256); // inside the user data of both modes
            }

            byte[] data = RawSectorCodec.Extract2048From2352(raw, out bool valid);

            if (valid)
            {
                Assert.Equal(payload, data); // only an exact repair may be called good
            }
        }
    }

    [Fact]
    public void ZeroFilledMode2Body_IsNotTakenAsIntact()
    {
        // F2: subheader, data, EDC and parity all zero form a valid codeword, the pattern of a blanked sector.
        byte[] raw = Mode2(Payload(4));
        raw.AsSpan(16).Clear();

        RawSectorCodec.Extract2048From2352(raw, out bool valid);

        Assert.False(valid);
    }

    [Fact]
    public void Mode2Form2_IsRefused()
    {
        var raw = new byte[RawSectorCodec.RawSectorSize];
        RawSectorCodec.Convert2048To2352Mode2Xa(Payload(5), 0, raw, submode: 0x20);

        Assert.Throws<InvalidDataException>(() => RawSectorCodec.Extract2048From2352(raw, out _));
    }

    [Fact]
    public void ZeroFilledUserDataWithARealSubheader_IsStillGood()
    {
        // All-zero file data is legitimate; only a sector with nothing at all after the header is suspect.
        byte[] payload = new byte[RawSectorCodec.UserDataSectorSize];
        Assert.Equal(payload, RawSectorCodec.Extract2048From2352(Mode2(payload), out bool valid));
        Assert.True(valid);
        Assert.Equal(payload, RawSectorCodec.Extract2048From2352(Mode1(payload), out bool valid1));
        Assert.True(valid1);
    }
}
