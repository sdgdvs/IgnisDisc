using CdBurner.Core.Images;
using Xunit;

namespace CdBurner.Core.Tests;

public sealed class DiscJugglerImageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ignis-cdi-" + Guid.NewGuid().ToString("N"));

    public DiscJugglerImageTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static byte[] MakeIso(int sectors)
    {
        byte[] iso = new byte[sectors * 2048];
        for (int i = 0; i < iso.Length; i++) iso[i] = (byte)((i * 31 + i / 2048) & 0xFF);
        "CD001"u8.CopyTo(iso.AsSpan(16 * 2048 + 1));
        iso[16 * 2048] = 1;
        return iso;
    }

    private static byte[] ToRaw2352(byte[] iso, byte mode)
    {
        int n = iso.Length / 2048;
        byte[] raw = new byte[n * 2352];
        for (int i = 0; i < n; i++)
        {
            int o = i * 2352;
            raw.AsSpan(o + 1, 10).Fill(0xFF);
            raw[o + 15] = mode;
            iso.AsSpan(i * 2048, 2048).CopyTo(raw.AsSpan(o + 16));
        }
        return raw;
    }

    [Theory]
    [InlineData(2, 0x80000004u)]
    [InlineData(3, 0x80000005u)]
    [InlineData(35, 0x80000006u)]
    public async Task Cdi_Mode1_2048_ConvertsToIsoAndBinCue(int version, uint versionCode)
    {
        byte[] iso = MakeIso(24);
        string cdi = Path.Combine(_dir, $"v{version}.cdi");
        LegacyDiscImageConverter.CreateDiscJugglerFile(cdi, iso, 2048, version);

        // Trailer layout from the cdirip reader: last 8 bytes = version, descriptor position.
        byte[] file = File.ReadAllBytes(cdi);
        Assert.Equal(versionCode, BitConverter.ToUInt32(file, file.Length - 8));

        Assert.True(LegacyDiscImageConverter.IsLegacyImageFile(cdi));
        var info = LegacyDiscImageConverter.Inspect(cdi);
        Assert.Equal(24, info.SectorCount);
        Assert.Equal(2048, info.SectorSizeBytes);
        Assert.False(info.IsAudioDisc);

        string outIso = Path.Combine(_dir, "out.iso");
        Assert.Equal(24, await LegacyDiscImageConverter.ConvertToIsoAsync(cdi, outIso));
        Assert.Equal(iso, File.ReadAllBytes(outIso));

        string bin = Path.Combine(_dir, "out.bin");
        await LegacyDiscImageConverter.ConvertImageToBinCueAsync(cdi, bin);
        Assert.Equal(iso, File.ReadAllBytes(bin));
        Assert.Contains("MODE1/2048", File.ReadAllText(Path.ChangeExtension(bin, ".cue")));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(35)]
    public async Task Cdi_RawMode1_2352_ExtractsUserData(int version)
    {
        byte[] iso = MakeIso(20);
        string cdi = Path.Combine(_dir, $"raw{version}.cdi");
        LegacyDiscImageConverter.CreateDiscJugglerFile(cdi, ToRaw2352(iso, 1), 2352, version);

        var info = LegacyDiscImageConverter.Inspect(cdi);
        Assert.True(info.IsRaw2352);
        string outIso = Path.Combine(_dir, "raw.iso");
        await LegacyDiscImageConverter.ConvertToIsoAsync(cdi, outIso);
        Assert.Equal(iso, File.ReadAllBytes(outIso));

        using var stream = LegacyDiscImageConverter.OpenIsoUserDataStream(cdi);
        Assert.Equal(iso.Length, stream.Length);
    }

    [Fact]
    public async Task Cdi_Mode2Xa_UsesOffset24()
    {
        byte[] iso = MakeIso(20);
        string cdi = Path.Combine(_dir, "xa.cdi");
        byte[] raw = ToRaw2352(iso, 2);
        // Mode 2 Form 1: user data begins after the 8-byte subheader, i.e. at 24.
        for (int i = 0; i < 20; i++) iso.AsSpan(i * 2048, 2048).CopyTo(raw.AsSpan(i * 2352 + 24));
        LegacyDiscImageConverter.CreateDiscJugglerFile(cdi, raw, 2352, 3);
        string outIso = Path.Combine(_dir, "xa.iso");
        await LegacyDiscImageConverter.ConvertToIsoAsync(cdi, outIso);
        Assert.Equal(iso, File.ReadAllBytes(outIso));
    }

    [Fact]
    public async Task Cdi_AudioOnly_IsAudioAndRefusesIso()
    {
        string cdi = Path.Combine(_dir, "audio.cdi");
        LegacyDiscImageConverter.CreateDiscJugglerFile(cdi, new byte[2352 * 10], 2352, 3, audio: true);
        Assert.True(LegacyDiscImageConverter.Inspect(cdi).IsAudioDisc);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            LegacyDiscImageConverter.ConvertToIsoAsync(cdi, Path.Combine(_dir, "a.iso")));
    }

    [Fact]
    public void Cdi_UnknownVersion_IsRejected()
    {
        string cdi = Path.Combine(_dir, "bad.cdi");
        File.WriteAllBytes(cdi, new byte[64]);
        Assert.Throws<InvalidDataException>(() => LegacyDiscImageConverter.Inspect(cdi));
    }

    [Fact]
    public void Cdi_TruncatedDescriptor_IsRejected()
    {
        string cdi = Path.Combine(_dir, "t.cdi");
        LegacyDiscImageConverter.CreateDiscJugglerFile(cdi, MakeIso(20), 2048, 3);
        byte[] f = File.ReadAllBytes(cdi);
        // Keep version, but point the descriptor 20 bytes before the end: markers cannot match.
        BitConverter.GetBytes(28u).CopyTo(f, f.Length - 4);
        File.WriteAllBytes(cdi, f);
        Assert.Throws<InvalidDataException>(() => LegacyDiscImageConverter.Inspect(cdi));
    }
}
