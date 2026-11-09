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
    /// Standalone zero-dependency PowerShell verification script placed on every staged archive disc.
    /// </summary>
    public const string VerifyPowerShellFileName = "VERIFY.PS1";

    /// <summary>
    /// Standalone Windows batch wrapper for <see cref="VerifyPowerShellFileName"/> placed on every staged archive disc.
    /// </summary>
    public const string VerifyBatchFileName = "VERIFY.BAT";

    /// <summary>
    /// Human-readable long-term archival verification and PAR2 repair guide placed on every staged archive disc.
    /// </summary>
    public const string RecoveryReadmeFileName = "README-RECOVERY.TXT";

    /// <summary>
    /// Opens a file for hashing with a 1 MB sequential read buffer. The default 4 KB buffer turns a large file into
    /// hundreds of thousands of small reads, which is slow on optical drives (3.4 MB/s measured on a DVD).
    /// </summary>
    private static FileStream OpenForHashing(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1 << 20, FileOptions.SequentialScan | FileOptions.Asynchronous);

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
            using (var stream = OpenForHashing(diskPath))
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

        await WriteStandaloneVerificationScriptsAsync(discRoot, plan, disc, cancellationToken).ConfigureAwait(false);

        return index;
    }

    private static async Task WriteStandaloneVerificationScriptsAsync(
        string discRoot,
        ArchivePlan plan,
        ArchiveDiscPlan disc,
        CancellationToken cancellationToken)
    {
        string ps1Path = Path.Combine(discRoot, VerifyPowerShellFileName);
        if (!File.Exists(ps1Path))
        {
            const string ps1Content = """
                $ErrorActionPreference = 'Stop'
                $manifest = Join-Path $PSScriptRoot 'IGNISDISC-MANIFEST.sha256'
                if (-not (Test-Path -LiteralPath $manifest)) {
                    Write-Error "Manifest file not found: $manifest"
                    exit 2
                }
                $checked = 0
                $failed = 0
                foreach ($line in Get-Content -LiteralPath $manifest -Encoding UTF8) {
                    $trimmed = $line.Trim()
                    if ([string]::IsNullOrWhiteSpace($trimmed)) { continue }
                    $idx = $trimmed.IndexOf('  ')
                    if ($idx -lt 0) { continue }
                    $expected = $trimmed.Substring(0, $idx).Trim().ToLowerInvariant()
                    $relPath = $trimmed.Substring($idx + 2).Trim() -replace '/', [IO.Path]::DirectorySeparatorChar
                    $fullPath = Join-Path $PSScriptRoot $relPath
                    $checked++
                    if (-not (Test-Path -LiteralPath $fullPath)) {
                        Write-Host "[MISSING] $relPath" -ForegroundColor Red
                        $failed++
                        continue
                    }
                    $actual = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash.ToLowerInvariant()
                    if ($actual -ne $expected) {
                        Write-Host "[CORRUPT] $relPath" -ForegroundColor Red
                        $failed++
                    } else {
                        Write-Host "[OK]      $relPath" -ForegroundColor Green
                    }
                }
                Write-Host ""
                if ($failed -eq 0) {
                    Write-Host "Archive verification PASSED ($checked files intact)." -ForegroundColor Green
                    exit 0
                } else {
                    Write-Host "Archive verification FAILED ($failed of $checked files missing or corrupt)." -ForegroundColor Red
                    exit 1
                }
                """;
            await File.WriteAllTextAsync(ps1Path, ps1Content.Replace("\r\n", "\n"), Utf8NoBom, cancellationToken).ConfigureAwait(false);
        }

        string batPath = Path.Combine(discRoot, VerifyBatchFileName);
        if (!File.Exists(batPath))
        {
            const string batContent = "@echo off\r\n" +
                "powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"%~dp0VERIFY.PS1\"\r\n" +
                "set EXITCODE=%ERRORLEVEL%\r\n" +
                "if not \"%1\"==\"--nopause\" pause\r\n" +
                "exit /b %EXITCODE%\r\n";
            await File.WriteAllTextAsync(batPath, batContent, Utf8NoBom, cancellationToken).ConfigureAwait(false);
        }

        string readmePath = Path.Combine(discRoot, RecoveryReadmeFileName);
        if (!File.Exists(readmePath))
        {
            string readmeContent = $"""
                ================================================================================
                IGNISDISC ARCHIVAL DISC VERIFICATION & RECOVERY GUIDE
                Set ID:      {plan.SetId}
                Disc:        {disc.DiscNumber} of {plan.Discs.Count}
                Media Type:  {plan.MediaType}
                PAR2 Parity: {plan.RedundancyPercent}%
                ================================================================================

                This disc is self-describing and can be verified on any computer without
                IgnisDisc installed using standard operating system tools.

                1. ZERO-DEPENDENCY VERIFICATION ON WINDOWS
                   - Double-click VERIFY.BAT (or run .\VERIFY.PS1 in PowerShell).
                   - Or verify any individual file manually using built-in Windows tools:
                       certutil -hashfile <filename> SHA256
                     and compare the output against IGNISDISC-MANIFEST.sha256.

                2. ZERO-DEPENDENCY VERIFICATION ON LINUX / MACOS
                   - Open a terminal in the root of this disc and run:
                       sha256sum -c IGNISDISC-MANIFEST.sha256

                3. REPAIRING DAMAGED FILES WITH PAR2 (IF RECOVERY FOLDER IS PRESENT)
                   - Copy the entire contents of this disc to a writable folder on disk.
                   - Open a terminal in that writable folder and run par2cmdline:
                       par2 repair -B. RECOVERY\recovery.par2      (Windows)
                       par2 repair -B. RECOVERY/recovery.par2      (Linux / macOS)
                """.Replace("\r\n", "\n");
            await File.WriteAllTextAsync(readmePath, readmeContent, Utf8NoBom, cancellationToken).ConfigureAwait(false);
        }
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
            using (var stream = OpenForHashing(diskFilePath))
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
