using System.Reflection;
using CdBurner.Core;
using Xunit;

namespace CdBurner.Core.Tests;

public class LegalBannerTests
{
    /// <summary>
    /// The version the assemblies were built as (Directory.Build.props), e.g. "1.12". The banner text is stamped by
    /// the release script, so comparing against this catches a banner that was left on an older version.
    /// </summary>
    private static string BuiltVersion =>
        typeof(LegalBanner).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];

    [Fact]
    public void VersionNotice_MatchesGplSpecificationAndTetherworksAttribution()
    {
        string version = BuiltVersion;
        Assert.Matches(@"^\d+\.\d+$", version);

        // The banner texts are compile-time constants, so the analyzer wants them on the "expected" side.
        Assert.Equal(
            LegalBanner.VersionNotice,
            $"IgnisDisc v{version} — Copyright (C) 2026 Tetherworks (https://ignisdisc.tetherworks.dev)\n" +
            "License GPLv3+: GNU GPL version 3 or later <https://gnu.org/licenses/gpl.html>. Free & open-source software; comes with ABSOLUTELY NO WARRANTY.");

        Assert.Equal(
            LegalBanner.Line1,
            $"IgnisDisc v{version} — Copyright (C) 2026 Tetherworks (https://ignisdisc.tetherworks.dev)");

        Assert.Equal(
            "License GPLv3+: GNU GPL version 3 or later <https://gnu.org/licenses/gpl.html>. Free & open-source software; comes with ABSOLUTELY NO WARRANTY.",
            LegalBanner.Line2);

        Assert.Equal(
            LegalBanner.JsonEngine,
            $"IgnisDisc v{version} (GPL-3.0-or-later, https://ignisdisc.tetherworks.dev)");
    }
}
