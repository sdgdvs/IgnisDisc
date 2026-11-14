using CdBurner.Core.Iso;
using CdBurner.Core.Primitives;

namespace CdBurner.Core.Archival;

/// <summary>What a recovery run found and fixed.</summary>
/// <param name="OutputFolder">Folder the disc's files were copied (and repaired) into.</param>
/// <param name="UnreadableSectors">Sectors that could not be read (zero-filled in the copy).</param>
/// <param name="DamagedFiles">Files that had unreadable sectors or could not be copied whole, before repair.</param>
/// <param name="HadRecoveryData">The disc carried PAR2 recovery data.</param>
/// <param name="RepairAttempted">par2 was run.</param>
/// <param name="FilesChecked">Files checked against the disc's manifest after repair.</param>
/// <param name="StillMissing">Files the manifest lists that are still missing.</param>
/// <param name="StillCorrupt">Files whose checksum still does not match.</param>
/// <param name="Problem">Why recovery could not run or finish, in plain words; null when it ran.</param>
public sealed record ArchiveRecoveryReport(
    string OutputFolder,
    IReadOnlyList<int> UnreadableSectors,
    IReadOnlyList<string> DamagedFiles,
    bool HadRecoveryData,
    bool RepairAttempted,
    int FilesChecked,
    IReadOnlyList<string> StillMissing,
    IReadOnlyList<string> StillCorrupt,
    string? Problem)
{
    /// <summary>Every file the manifest lists is present and matches its checksum.</summary>
    public bool FullyRecovered => Problem == null && FilesChecked > 0 && StillMissing.Count == 0 && StillCorrupt.Count == 0;

    /// <summary>
    /// Both checksum lists on the disc (manifest and index) read back whole. When either is damaged, a file that is
    /// not flagged may simply not have been checked, so no file is reported as repaired.
    /// </summary>
    public bool ChecksumListsIntact => FilesChecked > 0 && !StillCorrupt.Any(entry =>
        entry.StartsWith(ArchiveManifest.ManifestFileName, StringComparison.OrdinalIgnoreCase) ||
        entry.StartsWith(ArchiveManifest.IndexFileName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Damaged files that now match their checksum (empty when the checksum lists cannot be trusted).</summary>
    public IReadOnlyList<string> RepairedFiles => ChecksumListsIntact
        ? DamagedFiles.Where(f => !StillMissing.Contains(f) && !StillCorrupt.Contains(f)).ToList()
        : Array.Empty<string>();

    /// <summary>A short report for people, one fact per line.</summary>
    public string ToText()
    {
        var lines = new List<string>
        {
            FullyRecovered
                ? (DamagedFiles.Count == 0 ? "Result: every file read correctly; nothing needed repair." : "Result: every file recovered.")
                : "Result: NOT fully recovered.",
            $"Copy written to: {OutputFolder}",
            $"Unreadable sectors: {UnreadableSectors.Count:N0}",
            $"Damaged files before repair: {DamagedFiles.Count:N0}",
        };
        lines.AddRange(DamagedFiles.Select(f => "  " + f + (RepairedFiles.Contains(f) ? "  (repaired)" : "  (NOT repaired)")));
        lines.Add($"Recovery data on the disc: {(HadRecoveryData ? "yes" : "no")}");
        if (FilesChecked > 0 && !ChecksumListsIntact)
        {
            lines.Add("The disc's checksum lists are damaged themselves, so no file can be confirmed as intact.");
        }
        lines.Add($"Files checked against the manifest: {FilesChecked:N0}");
        lines.AddRange(StillMissing.Select(f => "  missing: " + f));
        lines.AddRange(StillCorrupt.Select(f => "  still damaged: " + f));
        if (Problem != null)
        {
            lines.Add("Problem: " + Problem);
        }
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }
}

/// <summary>
/// Gets an archive disc's files back from an image of it (read with retries, unreadable sectors zero-filled):
/// copies every file out, rebuilds damaged ones from the disc's PAR2 recovery data, then checks everything against
/// the disc's manifest. Never reports a file as repaired unless its checksum matches.
/// </summary>
public static class ArchiveRecoveryService
{
    public static async Task<ArchiveRecoveryReport> RecoverFromImageAsync(
        string imagePath,
        IReadOnlyCollection<int> unreadableSectors,
        string outputFolder,
        string? par2Path,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFolder);
        ArgumentNullException.ThrowIfNull(unreadableSectors);

        if (Directory.Exists(outputFolder) && Directory.EnumerateFileSystemEntries(outputFolder).Any())
        {
            throw new IOException($"Output folder '{outputFolder}' is not empty.");
        }
        Directory.CreateDirectory(outputFolder);

        var bad = new SortedSet<int>(unreadableSectors);
        var damaged = new List<string>();
        ArchiveRecoveryReport Fail(string problem) => new(outputFolder, bad.ToList(), damaged, false, false, 0,
            Array.Empty<string>(), Array.Empty<string>(), problem);

        await using (var image = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: false))
        {
            var files = ReadFileList(image, out string? listProblem, out int? descriptorCopyLba);
            if (descriptorCopyLba is int copyLba)
            {
                progress?.Report($"The volume descriptor at sector 16 is unreadable; using its copy at sector {copyLba:N0}.");
            }
            if (files == null)
            {
                return Fail("the disc's file list could not be read (" + listProblem + "). PAR2 needs the file list to know where each file is.");
            }

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string relative = file.VirtualPath.TrimStart('/');
                string? destination = ArchiveManifest.ResolveInsideRoot(outputFolder, relative);
                if (destination == null)
                {
                    continue; // a name that would land outside the folder is never written
                }

                progress?.Report($"Copying {relative}");
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                long sectors = (file.SizeBytes + OpticalConstants.DataSectorSize - 1) / OpticalConstants.DataSectorSize;
                bool hitBadSector = bad.GetViewBetween(file.StartLba, (int)Math.Min(int.MaxValue, file.StartLba + Math.Max(0, sectors - 1))).Count > 0
                                    && sectors > 0;
                bool copiedWhole = await CopyFileAsync(image, file, destination, cancellationToken).ConfigureAwait(false);
                if (hitBadSector || !copiedWhole)
                {
                    damaged.Add(relative);
                }
            }
        }

        string recoveryFolder = Path.Combine(outputFolder, ArchiveManifest.RecoveryFolderName);
        bool hasRecovery = Directory.Exists(recoveryFolder) && Directory.EnumerateFiles(recoveryFolder, "*.par2").Any();
        bool repairAttempted = false;
        IReadOnlyList<string> backups = Array.Empty<string>();
        string? repairError = null;

        // Images made elsewhere come without a list of unreadable sectors: the manifest finds those files instead.
        // A manifest that is itself damaged (or missing) is something par2 can rebuild, so it counts too.
        bool manifestReadable = true;
        try
        {
            var before = await ArchiveManifest.VerifyAsync(outputFolder, null, cancellationToken).ConfigureAwait(false);
            foreach (string path in before.Corrupt.Concat(before.Missing))
            {
                if (!damaged.Contains(path, StringComparer.OrdinalIgnoreCase))
                {
                    damaged.Add(path);
                }
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException or FormatException or IOException)
        {
            manifestReadable = false;
        }

        if ((damaged.Count > 0 || !manifestReadable) && hasRecovery && par2Path != null)
        {
            progress?.Report($"Rebuilding {damaged.Count} damaged file(s) from the recovery data...");
            var repair = await Par2Runner.RepairAsync(par2Path, outputFolder, cancellationToken).ConfigureAwait(false);
            repairAttempted = true;
            backups = repair.BackupFiles;
            repairError = repair.ExitCode == 0 ? null : repair.ErrorOutput;
        }

        ArchiveVerificationResult verification;
        try
        {
            progress?.Report("Checking every file against the disc's manifest...");
            verification = await ArchiveManifest.VerifyAsync(outputFolder, null, cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return new ArchiveRecoveryReport(outputFolder, bad.ToList(), damaged, hasRecovery, repairAttempted, 0,
                Array.Empty<string>(), Array.Empty<string>(),
                "the disc has no IgnisDisc archive manifest, so the files cannot be checked.");
        }

        string? problem = null;
        if (!verification.IsIntact)
        {
            problem = !hasRecovery
                ? "the disc has no recovery data, so damaged files cannot be rebuilt."
                : par2Path == null
                    ? "par2 is not installed (Settings > optional tools, or par2.exe on PATH), so damaged files were not rebuilt."
                    : repairError != null && repairError.Contains("pathlength", StringComparison.OrdinalIgnoreCase)
                        ? "par2 cannot handle a file path longer than 260 characters; recover into a shorter folder, such as C:\\Rescued."
                        : "there was more damage than the recovery data can rebuild.";
        }
        else
        {
            // par2 keeps damaged originals as name.1; once every file checks out they are only clutter.
            foreach (string backup in backups)
            {
                try { File.Delete(backup); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        return new ArchiveRecoveryReport(outputFolder, bad.ToList(), damaged, hasRecovery, repairAttempted,
            verification.FilesChecked, verification.Missing, verification.Corrupt, problem);
    }

    /// <summary>
    /// The disc's files with their full names. The UDF tree has them all (ISO 9660 has 8.3 names and Joliet stops at
    /// 64 characters, and a disc burned as ISO + UDF has no Joliet at all), so it is used whenever it lists at least
    /// as many files as the ISO 9660 / Joliet tree. When sector 16 is unreadable, the ISO 9660 tree is found through
    /// a copy of its volume descriptor (archive discs carry one per recovery zone); <paramref name="descriptorCopyLba"/>
    /// then says which copy was used.
    /// </summary>
    internal static IReadOnlyList<IsoFileEntry>? ReadFileList(Stream image, out string? problem, out int? descriptorCopyLba)
    {
        problem = null;
        descriptorCopyLba = null;
        image.Position = 0;
        var udfRoot = UdfTreeReader.TryRead(image);
        List<IsoFileEntry>? udfFiles = udfRoot == null ? null : Flatten(udfRoot).ToList();

        List<IsoFileEntry>? isoFiles = null;
        try
        {
            image.Position = 0;
            using var reader = IsoImageReader.FromDamagedStream(image, leaveOpen: true);
            isoFiles = reader.AllFiles.ToList();
            descriptorCopyLba = reader.DescriptorCopyLba;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or InvalidOperationException)
        {
            problem = ex.Message;
        }

        if (udfFiles != null && (isoFiles == null || udfFiles.Count >= isoFiles.Count))
        {
            return udfFiles;
        }
        return isoFiles;
    }

    private static IEnumerable<IsoFileEntry> Flatten(IsoDirectoryEntry directory) =>
        directory.Files.Concat(directory.SubDirectories.SelectMany(Flatten));

    /// <summary>
    /// Copies a file's sectors straight from the image (no whole-file buffer, so files over 2 GB work). A file that
    /// runs past the end of the image is padded with zeros to its full length, so par2 can line its blocks up.
    /// </summary>
    private static async Task<bool> CopyFileAsync(Stream image, IsoFileEntry file, string destination, CancellationToken ct)
    {
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
        long start = (long)file.StartLba * OpticalConstants.DataSectorSize;
        long available = Math.Clamp(image.Length - start, 0, file.SizeBytes);
        byte[] buffer = new byte[1 << 20];
        long copied = 0;
        image.Position = start;
        while (copied < available)
        {
            int n = await image.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, available - copied)), ct).ConfigureAwait(false);
            if (n <= 0) break;
            await output.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            copied += n;
        }
        if (copied < file.SizeBytes)
        {
            output.SetLength(file.SizeBytes);
            return false;
        }
        return true;
    }
}
