using System.Text;

namespace CdBurner.Core.Compilation;

/// <summary>
/// Provides filename sanitization and conversion utilities for ISO 9660 (Level 1, Level 2)
/// and Microsoft Joliet optical filesystem naming standards.
/// </summary>
public static class IsoNameSanitizer
{
    private static readonly HashSet<char> IllegalJolietChars = new()
    {
        '\\', '/', ':', '*', '?', '"', '<', '>', '|', ';'
    };

    /// <summary>
    /// Converts a filename to ISO 9660 Level 1 format: 8.3 uppercase [A-Z0-9_] with a ';1' version suffix.
    /// Non-compliant characters are replaced with underscores.
    /// </summary>
    /// <param name="fileName">The original filename or path.</param>
    /// <param name="collisionIndex">Optional collision index (e.g. 1, 2) to disambiguate identical names.</param>
    /// <returns>A valid ISO 9660 Level 1 file identifier ending with ';1'.</returns>
    public static string ToIso9660Level1Name(string fileName, int collisionIndex = 0)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return "FILE;1";
        }

        string cleanName = Path.GetFileName(fileName);
        if (string.IsNullOrEmpty(cleanName))
        {
            cleanName = fileName;
        }

        var (rawBase, rawExt) = SplitNameAndExtension(cleanName);

        string baseName = SanitizeDCharacters(rawBase);
        if (string.IsNullOrEmpty(baseName))
        {
            baseName = "FILE";
        }

        string ext = SanitizeDCharacters(rawExt);
        if (ext.Length > 3)
        {
            ext = ext[..3];
        }

        if (collisionIndex > 0)
        {
            string suffix = $"_{collisionIndex}";
            int maxBaseLength = Math.Max(1, 8 - suffix.Length);
            if (baseName.Length > maxBaseLength)
            {
                baseName = baseName[..maxBaseLength];
            }
            baseName += suffix;
        }
        else
        {
            if (baseName.Length > 8)
            {
                baseName = baseName[..8];
            }
        }

        return string.IsNullOrEmpty(ext) ? $"{baseName};1" : $"{baseName}.{ext};1";
    }

    /// <summary>
    /// Converts a filename to ISO 9660 Level 2 format: up to 31 characters uppercase [A-Z0-9_.] with a ';1' version suffix.
    /// Non-compliant characters are replaced with underscores while preserving the file extension.
    /// </summary>
    /// <param name="fileName">The original filename or path.</param>
    /// <returns>A valid ISO 9660 Level 2 file identifier ending with ';1'.</returns>
    public static string ToIso9660Level2Name(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return "FILE;1";
        }

        string cleanName = Path.GetFileName(fileName);
        if (string.IsNullOrEmpty(cleanName))
        {
            cleanName = fileName;
        }

        var (rawBase, rawExt) = SplitNameAndExtension(cleanName);

        string baseName = SanitizeDCharacters(rawBase);
        if (string.IsNullOrEmpty(baseName))
        {
            baseName = "FILE";
        }

        string ext = SanitizeDCharacters(rawExt);

        if (!string.IsNullOrEmpty(ext))
        {
            if (ext.Length > 30)
            {
                ext = ext[..30];
            }

            int maxBaseLength = 31 - 1 - ext.Length;
            if (baseName.Length > maxBaseLength)
            {
                baseName = baseName[..maxBaseLength];
            }

            return $"{baseName}.{ext};1";
        }
        else
        {
            if (baseName.Length > 31)
            {
                baseName = baseName[..31];
            }

            return $"{baseName};1";
        }
    }

    /// <summary>
    /// Converts a filename to Joliet format: truncates to at most 64 UTF-16 characters while preserving the extension
    /// and replacing illegal Joliet characters (\ / : * ? " &lt; &gt; | ;) with underscores.
    /// </summary>
    /// <param name="fileName">The original filename or path.</param>
    /// <returns>A valid Joliet filename.</returns>
    public static string ToJolietName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return "File";
        }

        string cleanName = Path.GetFileName(fileName);
        if (string.IsNullOrEmpty(cleanName))
        {
            cleanName = fileName;
        }

        // Replace illegal Joliet characters
        var sb = new StringBuilder(cleanName.Length);
        foreach (char c in cleanName)
        {
            if (IllegalJolietChars.Contains(c) || c < 0x20)
            {
                sb.Append('_');
            }
            else
            {
                sb.Append(c);
            }
        }
        string sanitized = sb.ToString();

        if (sanitized.Length <= 64)
        {
            return sanitized;
        }

        // Truncate to 64 UTF-16 code units while preserving extension and surrogate pairs
        int lastDotIndex = sanitized.LastIndexOf('.');
        if (lastDotIndex > 0)
        {
            string ext = sanitized[lastDotIndex..]; // e.g. ".txt"
            if (ext.Length >= 64)
            {
                return TruncateUtf16Safe(sanitized, 64);
            }

            int maxBaseLength = 64 - ext.Length;
            string basePart = sanitized[..lastDotIndex];
            if (basePart.Length > maxBaseLength)
            {
                basePart = TruncateUtf16Safe(basePart, maxBaseLength);
            }

            return basePart + ext;
        }

        return TruncateUtf16Safe(sanitized, 64);
    }

    private static string TruncateUtf16Safe(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        if (maxLength > 0 && char.IsHighSurrogate(value[maxLength - 1]))
        {
            maxLength--;
        }

        return value[..maxLength];
    }

    /// <summary>
    /// Converts a directory name to ISO 9660 Level 1 format: up to 8 d-characters uppercase [A-Z0-9_] with no extension or version suffix.
    /// </summary>
    public static string ToIso9660DirectoryName(string dirName)
    {
        if (string.IsNullOrWhiteSpace(dirName))
        {
            return "DIR";
        }

        string cleanName = Path.GetFileName(dirName);
        if (string.IsNullOrEmpty(cleanName))
        {
            cleanName = dirName;
        }

        string sanitized = SanitizeDCharacters(cleanName);
        if (string.IsNullOrEmpty(sanitized))
        {
            sanitized = "DIR";
        }

        return sanitized.Length > 8 ? sanitized[..8] : sanitized;
    }

    private static (string BaseName, string Extension) SplitNameAndExtension(string fileName)
    {
        int lastDot = fileName.LastIndexOf('.');
        if (lastDot > 0)
        {
            return (fileName[..lastDot], fileName[(lastDot + 1)..]);
        }
        else if (lastDot == 0 && fileName.Length > 1)
        {
            // E.g. ".gitignore" -> base is "gitignore", no extension
            return (fileName[1..], string.Empty);
        }

        return (fileName, string.Empty);
    }

    private static string SanitizeDCharacters(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(input.Length);
        foreach (char c in input)
        {
            if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))
            {
                sb.Append(c);
            }
            else if (c >= 'a' && c <= 'z')
            {
                sb.Append(char.ToUpperInvariant(c));
            }
            else
            {
                sb.Append('_');
            }
        }

        return sb.ToString();
    }
}
