using CdBurner.Core;
using Xunit;

namespace CdBurner.Core.Tests;

public class LegalBannerTests
{
    [Fact]
    public void VersionNotice_MatchesGplSpecificationAndTetherworksAttribution()
    {
        Assert.Equal(
            "IgnisDisc v1.11 — Copyright (C) 2026 Tetherworks (https://ignisdisc.tetherworks.dev)\n" +
            "License GPLv3+: GNU GPL version 3 or later <https://gnu.org/licenses/gpl.html>. Free & open-source software; comes with ABSOLUTELY NO WARRANTY.",
            LegalBanner.VersionNotice);

        Assert.Equal(
            "IgnisDisc v1.11 — Copyright (C) 2026 Tetherworks (https://ignisdisc.tetherworks.dev)",
            LegalBanner.Line1);

        Assert.Equal(
            "License GPLv3+: GNU GPL version 3 or later <https://gnu.org/licenses/gpl.html>. Free & open-source software; comes with ABSOLUTELY NO WARRANTY.",
            LegalBanner.Line2);

        Assert.Equal(
            "IgnisDisc v1.11 (GPL-3.0-or-later, https://ignisdisc.tetherworks.dev)",
            LegalBanner.JsonEngine);
    }
}
