using System.IO;
using System.Reflection;
using System.Resources;
using CdBurner.App.Resources;
using Xunit;

namespace CdBurner.Integration.Tests;

/// <summary>
/// The dialog and status text lives in Strings.resx. These tests check that every key resolves and that the
/// English text is exactly what the app showed before it moved out of the code. UiStringsOriginal.tsv holds the
/// literals as they were written in the view models (tab between key and text, "\n" for a line break).
/// </summary>
public sealed class UiStringResourceTests
{
    private static Dictionary<string, string> LoadOriginal()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "UiStringsOriginal.tsv");
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in File.ReadAllLines(path))
        {
            if (line.Length == 0) continue;
            var tab = line.IndexOf('\t');
            Assert.True(tab > 0, $"Bad line in UiStringsOriginal.tsv: {line}");
            map.Add(line[..tab], line[(tab + 1)..].Replace("\\n", "\n"));
        }
        return map;
    }

    private static List<PropertyInfo> StringProperties() =>
        typeof(Strings).GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Where(p => p.PropertyType == typeof(string)).ToList();

    [Fact]
    public void Every_property_resolves_to_non_empty_text()
    {
        var props = StringProperties();
        Assert.NotEmpty(props);
        foreach (var p in props)
        {
            var value = (string?)p.GetValue(null);
            Assert.False(string.IsNullOrWhiteSpace(value), $"Strings.{p.Name} is empty");
        }
    }

    [Fact]
    public void Resource_file_and_accessor_list_the_same_keys()
    {
        var manager = new ResourceManager("CdBurner.App.Resources.Strings", typeof(Strings).Assembly);
        var set = manager.GetResourceSet(System.Globalization.CultureInfo.InvariantCulture, createIfNotExists: true, tryParents: false);
        Assert.NotNull(set);
        var resourceKeys = set!.Cast<System.Collections.DictionaryEntry>().Select(e => (string)e.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var propertyKeys = StringProperties().Select(p => p.Name).OrderBy(k => k, StringComparer.Ordinal).ToList();
        Assert.Equal(resourceKeys, propertyKeys);
    }

    [Fact]
    public void English_text_is_unchanged_from_the_original_literals()
    {
        var original = LoadOriginal();
        Assert.True(original.Count >= 90, "Fixture looks truncated");
        foreach (var (key, expected) in original)
        {
            Assert.Equal(expected, Strings.Get(key));
        }
        Assert.Equal(original.Keys.OrderBy(k => k, StringComparer.Ordinal), StringProperties().Select(p => p.Name).OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void Formatted_dialogs_fill_their_placeholders_as_before()
    {
        Assert.Equal(
            "The DVD+RW in E:\\ already has files on it.\n\nBurning will replace everything on the disc. Continue?",
            Strings.Format(nameof(Strings.DialogReplaceDiscBody), "DVD+RW", "E:\\"));
        Assert.Equal(
            "Burn queue: disc 2 of 3.\n\nInsert a blank disc for 'a.iso' and click OK.",
            Strings.Format(nameof(Strings.DialogQueueInsertDiscBody), 2, 3, "a.iso"));
        Assert.Equal(
            "An unexpected error occurred:\n\nboom",
            Strings.Format(nameof(Strings.DialogUnexpectedErrorBody), "boom"));
    }

    [Fact]
    public void Missing_key_throws_instead_of_returning_blank_text()
    {
        Assert.Throws<MissingManifestResourceException>(() => Strings.Get("NoSuchKey"));
    }
}
