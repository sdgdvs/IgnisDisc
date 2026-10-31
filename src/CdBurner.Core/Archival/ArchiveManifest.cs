using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CdBurner.Core.Archival;

/// <summary>
/// Writes and verifies cryptographic checksum manifests and index metadata for archival discs.
/// </summary>
public static class ArchiveManifest
{
    /// <summary>
    /// Standard filename for the SHA-256 checksum manifest.
    /// </summary>
    public const string ManifestFileName = "IGNISDISC-MANIFEST.sha256";

    /// <summary>
    /// Standard filename for the JSON index metadata.
    /// </summary>
    public const string IndexFileName = "IGNISDISC-ARCHIVE.json";

    /// <summary>
    /// Subfolder name where PAR2 recovery files are placed.
    /// </summary>
    public const string RecoveryFolderName = "RECOVERY";

    /// <summary>
    /// Resolves a manifest-relative path ('/' separators) under <paramref name="root"/>, or returns null if it is
    /// rooted, a UNC/device path, or escapes the root through "..".
    /// </summary>
    internal static string? ResolveInsideRoot(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath) ||
            relativePath.StartsWith('\\') || relativePath.StartsWith('/') || relativePath.Contains(':'))
        {
            return null;
        }

        string fullRoot = Path.GetFullPath(root);
        if (!fullRoot.EndsWith(Path.DirectorySeparatorChar))
        {
            fullRoot += Path.DirectorySeparatorChar;
        }

        string full = Path.GetFullPath(Path.Combine(fullRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        return full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    /// <summary>
    /// Computes SHA-256 digests for all files on the staged disc and writes manifest and index files.
    /// </summary>
    /// <param name="discRoot">Local directory containing the staged disc files.</param>
    /// <param name="plan">The overall archive plan.</param>
    /// <param name="disc">The disc plan for this specific disc.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The generated <see cref="ArchiveIndex"/> metadata.</returns>
    public static async Task<ArchiveIndex> WriteAsync(
        string discRoot,
        ArchivePlan plan,
        ArchiveDiscPlan disc,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(discRoot);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(disc);

        var entries = new List<ArchiveManifestEntry>(disc.Files.Count);
        var manifestBuilder = new StringBuilder();

        foreach (var file in disc.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string normalizedRelative = file.RelativePath.Replace('\\', '/');
            string diskPath = Path.Combine(discRoot, normalizedRelative.Replace('/', Path.DirectorySeparatorChar));

            string hashHex;
            using (var stream = File.OpenRead(diskPath))
            {
                byte[] hashBytes = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
                hashHex = Convert.ToHexStringLower(hashBytes);
            }

            var entry = new ArchiveManifestEntry(normalizedRelative, file.SizeBytes, hashHex);
            entries.Add(entry);

            manifestBuilder.Append(hashHex);
            manifestBuilder.Append("  ");
            manifestBuilder.Append(normalizedRelative);
            manifestBuilder.Append('\n');
        }

        string manifestPath = Path.Combine(discRoot, ManifestFileName);
        await File.WriteAllTextAsync(manifestPath, manifestBuilder.ToString(), Utf8NoBom, cancellationToken).ConfigureAwait(false);

        var index = new ArchiveIndex(
            SetId: plan.SetId,
            DiscNumber: disc.DiscNumber,
            DiscCount: plan.Discs.Count,
            MediaType: plan.MediaType.ToString(),
            CreatedUtc: DateTime.UtcNow,
            RedundancyPercent: plan.RedundancyPercent,
            Files: entries);

        string json = JsonSerializer.Serialize(index, JsonOptions);
        string indexPath = Path.Combine(discRoot, IndexFileName);
        await File.WriteAllTextAsync(indexPath, json, Utf8NoBom, cancellationToken).ConfigureAwait(false);

        return index;
    }

    /// <summary>
    /// Verifies all files in a disc directory against its manifest and reports status.
    /// </summary>
    /// <param name="discRoot">Local directory containing the disc root.</param>
    /// <param name="progress">Optional progress reporter for status messages.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Verification result including missing, corrupt, and recovery status.</returns>
    /// <exception cref="DirectoryNotFoundException">Thrown if discRoot does not exist.</exception>
    /// <exception cref="FileNotFoundException">Thrown if the manifest file does not exist.</exception>
    public static async Task<ArchiveVerificationResult> VerifyAsync(
        string discRoot,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(discRoot);

        if (!Directory.Exists(discRoot))
        {
            throw new DirectoryNotFoundException($"Disc root directory not found: '{discRoot}'.");
        }

        string manifestPath = Path.Combine(discRoot, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException($"Archive manifest file not found: '{manifestPath}'.", manifestPath);
        }

        string? setId = null;
        int? discNumber = null;
        int? discCount = null;

        string indexPath = Path.Combine(discRoot, IndexFileName);
        if (File.Exists(indexPath))
        {
            try
            {
                string json = await File.ReadAllTextAsync(indexPath, cancellationToken).ConfigureAwait(false);
                var index = JsonSerializer.Deserialize<ArchiveIndex>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
                if (index != null)
                {
                    setId = index.SetId;
                    discNumber = index.DiscNumber;
                    discCount = index.DiscCount;
                }
            }
            catch
            {
                // Ignore deserialization errors and leave fields null
            }
        }

        string recoveryPath = Path.Combine(discRoot, RecoveryFolderName);
        bool hasRecoveryData = Directory.Exists(recoveryPath) &&
                               Directory.EnumerateFiles(recoveryPath, "*.par2").Any();

        var lines = await File.ReadAllLinesAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        var manifestLines = lines
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => l.Trim())
            .ToList();

        var missing = new List<string>();
        var corrupt = new List<string>();
        int filesChecked = 0;

        for (int i = 0; i < manifestLines.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string line = manifestLines[i];

            int spaceIndex = line.IndexOf("  ", StringComparison.Ordinal);
            string expectedHash;
            string relativePath;

            if (spaceIndex > 0)
            {
                expectedHash = line[..spaceIndex].Trim();
                relativePath = line[(spaceIndex + 2)..].Trim();
            }
            else
            {
                int singleSpace = line.IndexOf(' ');
                if (singleSpace > 0)
                {
                    expectedHash = line[..singleSpace].Trim();
                    relativePath = line[(singleSpace + 1)..].TrimStart('*', ' ').Trim();
                }
                else
                {
                    continue;
                }
            }

            filesChecked++;
            progress?.Report($"Verifying {filesChecked}/{manifestLines.Count}: {relativePath}");

            // The manifest comes from the disc and is untrusted: never open anything outside the disc root.
            string? diskFilePath = ResolveInsideRoot(discRoot, relativePath);
            if (diskFilePath == null)
            {
                corrupt.Add($"{relativePath} (rejected: path points outside the disc)");
                continue;
            }

            if (!File.Exists(diskFilePath))
            {
                missing.Add(relativePath);
                continue;
            }

            string actualHash;
            using (var stream = File.OpenRead(diskFilePath))
            {
                byte[] hashBytes = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
                actualHash = Convert.ToHexStringLower(hashBytes);
            }

            if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                corrupt.Add(relativePath);
            }
        }

        return new ArchiveVerificationResult(
            FilesChecked: filesChecked,
            Missing: missing,
            Corrupt: corrupt,
            HasRecoveryData: hasRecoveryData,
            SetId: setId,
            DiscNumber: discNumber,
            DiscCount: discCount);
    }
}
