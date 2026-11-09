namespace CdBurner.Core;

/// <summary>
/// Canonical Appropriate Legal Notice (ALN) and engine identity for IgnisDisc Optical Studio.
/// Complies with GNU GPLv3 Section 5(d) and Section 7(b).
/// </summary>
public static class LegalBanner
{
    /// <summary>
    /// Line 1: Product name, version, copyright attribution, and canonical project URL.
    /// </summary>
    public const string Line1 =
        "IgnisDisc v1.11 — Copyright (C) 2026 Tetherworks (https://ignisdisc.tetherworks.dev)";

    /// <summary>
    /// Line 2: GPLv3+ pointer, copyleft statement, and absolute no-warranty disclaimer.
    /// </summary>
    public const string Line2 =
        "License GPLv3+: GNU GPL version 3 or later <https://gnu.org/licenses/gpl.html>. Free & open-source software; comes with ABSOLUTELY NO WARRANTY.";

    /// <summary>
    /// Complete 2-line Appropriate Legal Notice banner.
    /// </summary>
    public const string VersionNotice =
        "IgnisDisc v1.11 — Copyright (C) 2026 Tetherworks (https://ignisdisc.tetherworks.dev)\n" +
        "License GPLv3+: GNU GPL version 3 or later <https://gnu.org/licenses/gpl.html>. Free & open-source software; comes with ABSOLUTELY NO WARRANTY.";

    /// <summary>
    /// Engine identifier embedded in root properties of machine-readable JSON output payloads.
    /// </summary>
    public const string JsonEngine =
        "IgnisDisc v1.11 (GPL-3.0-or-later, https://ignisdisc.tetherworks.dev)";
}
