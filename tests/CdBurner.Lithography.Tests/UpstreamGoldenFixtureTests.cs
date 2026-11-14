using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace CdBurner.Lithography.Tests;

/// <summary>
/// Integrity of the upstream golden fixtures (Fixtures/UpstreamGoldens). The hashes themselves are reproduced by
/// scripts/Make-LithographyGoldens.ps1 against the upstream build; these tests make sure the committed inputs and
/// the recorded output sizes stay consistent with the media formats, so a careless edit of the JSON or a PNG fails CI.
/// </summary>
public class UpstreamGoldenFixtureTests
{
    private const string PinnedCommit = "619ec7505ac8955f35fe2f353ebda74b6ea90dda";

    private static readonly string FixtureDir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "UpstreamGoldens");

    private static JsonElement LoadConfig()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(FixtureDir, "upstream-goldens.json")));
        return document.RootElement.Clone();
    }

    private static string Sha256Hex(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    [Fact]
    public void Fixtures_are_pinned_to_the_documented_upstream_commit()
    {
        JsonElement upstream = LoadConfig().GetProperty("upstream");
        Assert.Equal(PinnedCommit, upstream.GetProperty("commit").GetString());
        Assert.Equal("GPL-3.0-or-later", upstream.GetProperty("license").GetString());
    }

    [Fact]
    public void Input_pngs_match_their_recorded_hashes_and_are_64_by_64_rgba()
    {
        JsonElement inputs = LoadConfig().GetProperty("inputs");
        Assert.True(inputs.GetArrayLength() >= 2);
        foreach (JsonElement input in inputs.EnumerateArray())
        {
            string path = Path.Combine(FixtureDir, input.GetProperty("file").GetString()!);
            Assert.Equal(input.GetProperty("sha256").GetString(), Sha256Hex(path));

            // PNG signature and IHDR layout from the PNG specification (ISO/IEC 15948, 5.2 and 11.2.2).
            byte[] png = File.ReadAllBytes(path);
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png[..8]);
            Assert.Equal("IHDR"u8.ToArray(), png[12..16]);
            Assert.Equal(64, ReadBigEndian(png, 16));
            Assert.Equal(64, ReadBigEndian(png, 20));
            Assert.Equal(8, png[24]); // bit depth
            Assert.Equal(6, png[25]); // colour type 6 = RGBA
        }
    }

    [Fact]
    public void Every_case_records_a_command_and_a_sha256_for_each_output()
    {
        Regex sha = new("^[0-9a-f]{64}$");
        JsonElement cases = LoadConfig().GetProperty("cases");
        HashSet<string> ids = new(StringComparer.Ordinal);
        foreach (JsonElement testCase in cases.EnumerateArray())
        {
            string id = testCase.GetProperty("id").GetString()!;
            Assert.True(ids.Add(id), $"duplicate case {id}");
            Assert.Contains(testCase.GetProperty("tool").GetString(), new[] { "disc-image-studio", "golden-harness" });
            string[] args = testCase.GetProperty("args").EnumerateArray().Select(a => a.GetString()!).ToArray();
            Assert.Equal(testCase.GetProperty("tool").GetString() + " " + string.Join(' ', args),
                testCase.GetProperty("commandLine").GetString());
            Assert.All(args, a => Assert.DoesNotContain(" ", a));
            foreach (JsonElement output in testCase.GetProperty("outputs").EnumerateArray())
            {
                Assert.Matches(sha, output.GetProperty("sha256").GetString()!);
                Assert.True(output.GetProperty("bytes").GetInt64() > 0, $"{id}: empty output");
            }
        }
        // Each medium has a picture case and a calibration target.
        foreach (string required in new[] { "cd-picture-track", "dvd-picture-iso", "bd-picture-clusters",
                     "cd-calibration-target", "dvd-calibration-target", "bd-calibration-target" })
            Assert.Contains(required, ids);
    }

    // Expected sizes come from the media formats, not from upstream:
    //   CD-DA sector 2,352 bytes (IEC 60908), canonical PCM WAV header 44 bytes;
    //   DVD user sector 2,048 bytes, recording sector 38,688 channel bits (ECMA-267), 16 sectors per ECC block;
    //   BD cluster 32 x 2,048 bytes; ECC cluster = LDC 304 x RS(248,216) + BIS 24 x RS(62,30) = 76,880 bytes.
    [Theory]
    [InlineData("cd-picture.wav", 44 + 1200L * 2352)]
    [InlineData("cd-picture-plain.raw", 1200L * 2352)]
    [InlineData("cd-calibration.raw", 1200L * 2352)]
    [InlineData("dvd-picture.iso", 1600L * 2048)]
    [InlineData("dvd-picture-exact.iso", 1600L * 2048)]
    [InlineData("dvd-picture-compat.iso", 1600L * 2048)]
    [InlineData("dvd-calibration.iso", 1600L * 2048)]
    [InlineData("dvd-block0.iso", 16L * 2048)]
    [InlineData("dvd-block0.nrzi", 16L * 38688 / 8)]
    [InlineData("bd-picture.iso", 8L * 32 * 2048)]
    [InlineData("bd-calibration.iso", 8L * 32 * 2048)]
    [InlineData("bd-cluster.iso", 32L * 2048)]
    [InlineData("bd-cluster.ecc", 304L * 248 + 24L * 62)]
    public void Recorded_output_sizes_follow_the_media_format(string file, long expectedBytes)
    {
        JsonElement output = LoadConfig().GetProperty("cases").EnumerateArray()
            .SelectMany(c => c.GetProperty("outputs").EnumerateArray())
            .Single(o => o.GetProperty("file").GetString() == file);
        Assert.Equal(expectedBytes, output.GetProperty("bytes").GetInt64());
    }

    private static int ReadBigEndian(byte[] data, int offset) =>
        (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];
}
