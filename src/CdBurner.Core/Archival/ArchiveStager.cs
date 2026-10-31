namespace CdBurner.Core.Archival;

/// <summary>
/// Stages archival disc files into structured staging directories with manifests and PAR2 recovery data.
/// </summary>
public static class ArchiveStager
{
    /// <summary>
    /// Stages an archive plan onto local disk, creating disc01, disc02, etc. directories.
    /// </summary>
    /// <param name="plan">The archive plan describing disc layouts.</param>
    /// <param name="stagingRoot">Destination root folder for staging.</param>
    /// <param name="par2Path">Optional path to par2cmdline executable. Required if plan.RedundancyPercent > 0.</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of staged disc directory paths.</returns>
    /// <exception cref="InvalidOperationException">Thrown if redundancy is requested but par2Path is null.</exception>
    /// <exception cref="IOException">Thrown if any target disc directory already exists and is non-empty.</exception>
    public static async Task<IReadOnlyList<string>> StageAsync(
        ArchivePlan plan,
        string stagingRoot,
        string? par2Path,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingRoot);

        if (plan.RedundancyPercent > 0 && par2Path == null)
        {
            throw new InvalidOperationException(
                "PAR2 recovery data requires par2cmdline (par2.exe). Install it or use redundancy 0.");
        }

        foreach (var disc in plan.Discs)
        {
            string discFolderName = $"disc{disc.DiscNumber:D2}";
            string discDir = Path.Combine(stagingRoot, discFolderName);
            if (Directory.Exists(discDir) && Directory.EnumerateFileSystemEntries(discDir).Any())
            {
                throw new IOException($"Target staging directory '{discDir}' already exists and is non-empty.");
            }
        }

        var stagedDiscs = new List<string>(plan.Discs.Count);

        for (int d = 0; d < plan.Discs.Count; d++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var disc = plan.Discs[d];
            string discFolderName = $"disc{disc.DiscNumber:D2}";
            string discRoot = Path.Combine(stagingRoot, discFolderName);

            Directory.CreateDirectory(discRoot);

            for (int f = 0; f < disc.Files.Count; f++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var file = disc.Files[f];
                string destFilePath = Path.Combine(discRoot, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));

                string? destDir = Path.GetDirectoryName(destFilePath);
                if (!string.IsNullOrEmpty(destDir))
                {
                    Directory.CreateDirectory(destDir);
                }

                progress?.Report($"Staging disc {disc.DiscNumber}/{plan.Discs.Count}: copying {file.RelativePath}");
                File.Copy(file.FullPath, destFilePath, overwrite: false);

                DateTime lastWriteUtc = File.GetLastWriteTimeUtc(file.FullPath);
                File.SetLastWriteTimeUtc(destFilePath, lastWriteUtc);
            }

            progress?.Report($"Staging disc {disc.DiscNumber}/{plan.Discs.Count}: writing manifest...");
            await ArchiveManifest.WriteAsync(discRoot, plan, disc, cancellationToken).ConfigureAwait(false);

            if (plan.RedundancyPercent > 0)
            {
                string scratchDir = Path.Combine(stagingRoot, ".par2-scratch", discFolderName);
                try
                {
                    progress?.Report($"Staging disc {disc.DiscNumber}/{plan.Discs.Count}: generating PAR2 recovery data...");
                    await Par2Runner.CreateAsync(par2Path!, discRoot, plan.RedundancyPercent, scratchDir, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    try
                    {
                        if (Directory.Exists(scratchDir))
                        {
                            Directory.Delete(scratchDir, recursive: true);
                        }
                    }
                    catch
                    {
                    }
                }
            }

            stagedDiscs.Add(discRoot);
        }

        try
        {
            string parentScratch = Path.Combine(stagingRoot, ".par2-scratch");
            if (Directory.Exists(parentScratch) && !Directory.EnumerateFileSystemEntries(parentScratch).Any())
            {
                Directory.Delete(parentScratch, recursive: false);
            }
        }
        catch
        {
        }

        return stagedDiscs;
    }
}
