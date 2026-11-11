using CdBurner.Core.Burning;
using CdBurner.Core.Compilation;
using CdBurner.Core.Iso;

namespace CdBurner.Core.Tests;

/// <summary>What a successful burn still tells the user: names cut to fit, and the reinsert hint after an added session.</summary>
public class BurnWarningTests
{
    private static readonly string LongName = new string('n', 70) + ".txt";

    private static CompilationDirectory Tree(params string[] names)
    {
        var root = CompilationDirectory.CreateRoot();
        var folder = root.AddDirectory("folder");
        foreach (string name in names)
        {
            folder.AddFile(name, new byte[] { 1 });
        }
        return root;
    }

    [Fact]
    public void ClosedDiscWithUdf_KeepsLongNames_NothingToReport()
    {
        Assert.Null(DataBurnService.DescribeShortenedNames(Tree(LongName), new IsoBuildOptions(), sessionStartLba: 0));
    }

    [Fact]
    public void DiscWithoutUdf_ReportsTheCountAndAnExample()
    {
        string? note = DataBurnService.DescribeShortenedNames(
            Tree(LongName, "short.txt", new string('m', 65)), new IsoBuildOptions(IncludeUdf: false), sessionStartLba: 0);

        Assert.NotNull(note);
        Assert.Contains("2 name(s) longer than 64 characters were shortened", note);
        Assert.Contains("/folder/", note);
        Assert.Contains("contents of the files are complete", note);
    }

    [Fact]
    public void AddedSession_HasNoUdf_EvenWhenUdfWasAskedFor()
    {
        string? note = DataBurnService.DescribeShortenedNames(Tree(LongName), new IsoBuildOptions(), sessionStartLba: 131_152);

        Assert.NotNull(note);
        Assert.Contains("a session added to a disc", note);
    }

    [Fact]
    public void NamesOfExactlySixtyFourCharacters_Fit()
    {
        Assert.Null(DataBurnService.DescribeShortenedNames(
            Tree(new string('x', 64)), new IsoBuildOptions(IncludeUdf: false), sessionStartLba: 0));
    }

    [Fact]
    public void LongFolderNames_AreCountedToo()
    {
        var root = CompilationDirectory.CreateRoot();
        root.AddDirectory(new string('d', 80)).AddFile("a.txt", new byte[] { 1 });

        Assert.Contains("1 name(s)", DataBurnService.DescribeShortenedNames(root, new IsoBuildOptions(IncludeUdf: false), 0));
    }

    [Fact]
    public void ReinsertHint_FollowsAnAddedSessionOnARealDrive_Only()
    {
        Assert.Null(DataBurnService.ComposeBurnWarning(null, sessionAdded: false, virtualDrive: false));
        Assert.Null(DataBurnService.ComposeBurnWarning(null, sessionAdded: true, virtualDrive: true));
        Assert.Equal(DataBurnService.AddedSessionHint, DataBurnService.ComposeBurnWarning(null, sessionAdded: true, virtualDrive: false));
        Assert.Equal("names", DataBurnService.ComposeBurnWarning("names", sessionAdded: false, virtualDrive: false));
        Assert.Equal("names " + DataBurnService.AddedSessionHint, DataBurnService.ComposeBurnWarning("names", sessionAdded: true, virtualDrive: false));
    }
}
