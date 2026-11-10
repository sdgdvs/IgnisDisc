using System.Security.Cryptography;
using System.Text;
using CdBurner.Core.Compilation;
using CdBurner.Core.Iso;
using Xunit;

namespace CdBurner.Core.Tests;

/// <summary>
/// Roadmap M1.1: with <see cref="IsoBuildOptions.FixedTimestampUtc"/> set, the same tree always builds to the same
/// bytes, whenever and wherever it is built. Without it, images keep carrying the build time.
/// </summary>
public class ReproducibleIsoBuildTests
{
    private static readonly DateTime FixedTime = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Builds a tree whose content depends only on <paramref name="seed"/>; times come from <paramref name="time"/>.</summary>
    private static CompilationDirectory CreateTree(int seed, Func<int, DateTime> time)
    {
        var rng = new Random(seed);
        var root = CompilationDirectory.CreateRoot();
        root.LastWriteTimeUtc = time(0);
        var dirs = new List<CompilationDirectory> { root };
        int stamp = 1;

        int dirCount = rng.Next(0, 6);
        for (int d = 0; d < dirCount; d++)
        {
            var parent = dirs[rng.Next(dirs.Count)];
            var dir = parent.AddDirectory($"Dir{d}_{RandomName(rng)}");
            dir.LastWriteTimeUtc = time(stamp++);
            dirs.Add(dir);
        }

        int fileCount = rng.Next(1, 25);
        for (int f = 0; f < fileCount; f++)
        {
            var parent = dirs[rng.Next(dirs.Count)];
            byte[] data = new byte[rng.Next(0, 9000)];
            rng.NextBytes(data);
            var file = parent.AddFile($"{RandomName(rng)}_{f}.{(f % 3 == 0 ? "txt" : "bin")}", data);
            file!.LastWriteTimeUtc = time(stamp++);
        }
        return root;
    }

    private static string RandomName(Random rng)
    {
        const string chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 -_ü文";
        int length = rng.Next(1, 40);
        var sb = new StringBuilder(length);
        for (int i = 0; i < length; i++)
        {
            sb.Append(chars[rng.Next(chars.Length)]);
        }
        return sb.ToString().Trim() is { Length: > 0 } name ? name : "x";
    }

    private static string BuildHash(CompilationDirectory root, DateTime? fixedTime, bool joliet = true, bool udf = true)
    {
        var options = new IsoBuildOptions(
            VolumeLabel: "REPRO_TEST",
            IncludeJoliet: joliet,
            IncludeUdf: udf,
            FixedTimestampUtc: fixedTime);
        using var stream = IsoUdfImageBuilder.BuildToMemoryStream(root, options);
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }

    [Fact]
    public void FixedTime_RandomTrees_BuiltTwiceWithDifferentLaterTimes_AreByteIdentical()
    {
        // Property check over seeded random trees: everything newer than the fixed time (here: "now", and "now"
        // plus a varying offset as if checked out again later) clamps to it, so the images must match exactly.
        for (int seed = 1; seed <= 40; seed++)
        {
            DateTime laterA = DateTime.UtcNow;
            var first = CreateTree(seed, i => laterA.AddSeconds(i));
            var second = CreateTree(seed, i => laterA.AddDays(3).AddSeconds(i * 7 + seed));

            Assert.Equal(BuildHash(first, FixedTime), BuildHash(second, FixedTime));
        }
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void FixedTime_EveryFileSystemCombination_IsByteIdentical(bool joliet, bool udf)
    {
        var first = CreateTree(7, i => DateTime.UtcNow.AddMinutes(i));
        var second = CreateTree(7, i => DateTime.UtcNow.AddHours(5).AddMinutes(i));
        Assert.Equal(BuildHash(first, FixedTime, joliet, udf), BuildHash(second, FixedTime, joliet, udf));
    }

    [Fact]
    public void FixedTime_KeepsFileTimesOlderThanIt()
    {
        // Clamping only lowers times; a genuinely older file time is kept and still shows in the image.
        var old2010 = CreateTree(3, i => new DateTime(2010, 6, 1, 12, 0, 0, DateTimeKind.Utc).AddSeconds(i));
        var old2011 = CreateTree(3, i => new DateTime(2011, 6, 1, 12, 0, 0, DateTimeKind.Utc).AddSeconds(i));
        Assert.NotEqual(BuildHash(old2010, FixedTime), BuildHash(old2011, FixedTime));
    }

    [Fact]
    public void FixedTime_DifferentFixedTimes_GiveDifferentImages()
    {
        var tree = CreateTree(11, i => DateTime.UtcNow.AddSeconds(i));
        Assert.NotEqual(BuildHash(tree, FixedTime), BuildHash(tree, FixedTime.AddSeconds(1)));
    }

    [Fact]
    public void FixedTime_DifferentTrees_GetDifferentUdfVolumeSetIds()
    {
        // UDF 2.2.2.5 wants the first 16 characters of the Volume Set Identifier to be unique. With a fixed time it
        // comes from a hash of the tree, so two different trees must not share it.
        string IdOf(CompilationDirectory root)
        {
            using var stream = IsoUdfImageBuilder.BuildToMemoryStream(root, new IsoBuildOptions(FixedTimestampUtc: FixedTime));
            byte[] image = stream.ToArray();
            // Find the UDF Primary Volume Descriptor (tag 1) in the Volume Descriptor Sequence.
            for (int lba = 257; lba < image.Length / 2048; lba++)
            {
                int o = lba * 2048;
                if (image[o] == 1 && image[o + 1] == 0 && BitConverter.ToUInt32(image, o + 12) == (uint)lba)
                {
                    return Convert.ToHexString(image, o + 72, 40);
                }
            }
            throw new InvalidOperationException("No UDF Primary Volume Descriptor found.");
        }

        Assert.NotEqual(IdOf(CreateTree(21, _ => FixedTime)), IdOf(CreateTree(22, _ => FixedTime)));
    }

    [Fact]
    public void FixedTime_NamesDifferingOnlyInCase_AreOrderedTheSameEveryTime()
    {
        // List.Sort is not stable: names equal under OrdinalIgnoreCase used to land in whatever order they were added.
        CompilationDirectory Make(bool upperFirst)
        {
            var root = CompilationDirectory.CreateRoot();
            var a = root.AddDirectory(upperFirst ? "Data" : "data");
            var b = root.AddDirectory(upperFirst ? "data" : "Data");
            a.AddFile("one.txt", "1"u8.ToArray());
            b.AddFile("two.txt", "2"u8.ToArray());
            return root;
        }

        var first = Make(upperFirst: true);
        var second = Make(upperFirst: false);
        if (first.Children.Count < 2 || second.Children.Count < 2)
        {
            return; // The compilation merges case-only duplicates, so there is nothing to order.
        }
        Assert.Equal(BuildHash(first, FixedTime), BuildHash(second, FixedTime));
    }

    [Fact]
    public void WithoutFixedTime_VolumeDescriptorCarriesTheBuildTime()
    {
        var tree = CreateTree(5, _ => new DateTime(2012, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        using var stream = IsoUdfImageBuilder.BuildToMemoryStream(tree, new IsoBuildOptions());
        byte[] image = stream.ToArray();

        // ISO 9660 PVD creation date at LBA 16, offset 813: "YYYY" in ASCII.
        string year = Encoding.ASCII.GetString(image, 16 * 2048 + 813, 4);
        Assert.Equal(DateTime.UtcNow.Year.ToString(System.Globalization.CultureInfo.InvariantCulture), year);
    }
}
