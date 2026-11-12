using CdBurner.Core.Compilation;
using CdBurner.Core.Iso;
using CdBurner.Core.Primitives;

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
        var staged = await StageDiscsAsync(plan, stagingRoot, par2Path, progress, cancellationToken).ConfigureAwait(false);
        return staged.Select(d => d.DiscRoot).ToList();
    }

    /// <summary>
    /// Stages the plan like <see cref="StageAsync"/> and reports, per disc, the recovery settings used and the
    /// sectors taken. With <see cref="ArchivePlan.FillFreeSpace"/>, each disc's recovery data is sized to fill it up
    /// to <see cref="ArchivePlan.EffectiveCeilingSectors"/>; without it, recovery data is sized to the requested
    /// redundancy percentage while still using 2,048-byte-aligned blocks, one recovery volume, and the ceiling check.
    /// </summary>
    public static async Task<IReadOnlyList<ArchiveStagedDisc>> StageDiscsAsync(
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

        var stagedDiscs = new List<ArchiveStagedDisc>(plan.Discs.Count);

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
                string destFilePath = ArchiveManifest.ResolveInsideRoot(discRoot, file.RelativePath)
                    ?? throw new InvalidOperationException($"Refusing to stage '{file.RelativePath}': the path points outside the disc folder.");

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

            Par2RecoveryLayout? layout = null;
            long budget = 0;
            if (plan.RedundancyPercent > 0)
            {
                string scratchDir = Path.Combine(stagingRoot, ".par2-scratch", discFolderName);
                try
                {
                    var sources = ListSourceFiles(discRoot);
                    long freeSectors = RecoveryBudgetSectors(plan.EffectiveCeilingSectors, discRoot);
                    int dataSectors = SectorsUnder(discRoot);
                    budget = plan.FillFreeSpace
                        ? freeSectors
                        : Math.Min(freeSectors, (long)Math.Round(dataSectors * (plan.RedundancyPercent / 100.0)));
                    layout = Par2RecoverySizer.Plan(sources, budget);
                    if (layout == null && !plan.FillFreeSpace)
                    {
                        long oneBlock = Par2RecoverySizer.MinimumBudgetSectors(sources);
                        if (freeSectors >= oneBlock)
                        {
                            budget = oneBlock;
                            layout = Par2RecoverySizer.Plan(sources, budget);
                        }
                    }

                    if (layout != null)
                    {
                        string fillDetail = plan.FillFreeSpace ? "to fill the disc " : string.Empty;
                        progress?.Report($"Staging disc {disc.DiscNumber}/{plan.Discs.Count}: generating PAR2 recovery data " +
                                         $"{fillDetail}({layout.RecoveryBlocks:N0} blocks of {layout.BlockSize / 1024.0:N0} KB, " +
                                         $"{layout.RedundancyPercent:N0} %)...");
                        layout = await CreateFittingAsync(par2Path!, discRoot, layout, plan.EffectiveCeilingSectors, scratchDir, cancellationToken)
                            .ConfigureAwait(false);
                    }
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

            int recoverySectors = SectorsUnder(Path.Combine(discRoot, ArchiveManifest.RecoveryFolderName));
            stagedDiscs.Add(new ArchiveStagedDisc(discRoot, layout, recoverySectors, SectorsUnder(discRoot), layout != null ? budget : 0));
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

    /// <summary>Files par2 will protect: everything on the disc so far (data and manifests).</summary>
    internal static List<Par2SourceFile> ListSourceFiles(string discRoot) =>
        Directory.EnumerateFiles(discRoot, "*", SearchOption.AllDirectories)
            .Select(path => new Par2SourceFile(Path.GetRelativePath(discRoot, path).Replace('\\', '/'), new FileInfo(path).Length))
            .OrderBy(f => f.RelativePath, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Room for recovery data: the ceiling minus the image the disc makes with the recovery files in it, minus
    /// those files. The image size comes from the image builder's own layout (ISO 9660 + Joliet + UDF, as
    /// <c>burn-data --udf</c> writes it), so the file system's share is exact rather than estimated.
    /// </summary>
    public static long RecoveryBudgetSectors(int ceilingSectors, string discRoot)
    {
        // The two files par2 will write, at a size near what they will have: the layout barely depends on it.
        long room = Math.Max(0, ceilingSectors - SectorsUnder(discRoot)) * (long)OpticalConstants.DataSectorSize;
        var placeholders = new[] { ("recovery.par2", 4096L), ("recovery.vol00000+32768.par2", Math.Max(0, room - 4096)) };
        long placeholderSectors = placeholders.Sum(p => (p.Item2 + OpticalConstants.DataSectorSize - 1) / OpticalConstants.DataSectorSize);
        long fileSystem = ImageSectors(discRoot, placeholders) - SectorsUnder(discRoot) - placeholderSectors;
        return ceilingSectors - SectorsUnder(discRoot) - fileSystem;
    }

    /// <summary>Sectors the disc folder's image takes (plus optional extra RECOVERY files), from the builder's layout.</summary>
    public static int ImageSectors(string discRoot, IReadOnlyList<(string Name, long Size)>? extraRecoveryFiles = null)
    {
        var root = CompilationDirectory.CreateRoot();
        CompilationFolderImporter.Import(root, discRoot, strict: false);
        if (extraRecoveryFiles != null)
        {
            var recovery = root.AddDirectory(ArchiveManifest.RecoveryFolderName);
            foreach (var (name, size) in extraRecoveryFiles)
            {
                recovery.AddFile(new CompilationFile(name, string.Empty, size));
            }
        }
        return IsoUdfImageBuilder.CalculateTotalSectors(root);
    }

    /// <summary>
    /// Creates the recovery data and checks the disc's image fits the ceiling. The size formula is exact for
    /// par2cmdline 1.4.0; should another version write more, the excess blocks are dropped and it is created again.
    /// </summary>
    private static async Task<Par2RecoveryLayout> CreateFittingAsync(
        string par2Path, string discRoot, Par2RecoveryLayout layout, int ceilingSectors, string scratchDir, CancellationToken ct)
    {
        string recoveryFolder = Path.Combine(discRoot, ArchiveManifest.RecoveryFolderName);
        for (int attempt = 0; ; attempt++)
        {
            await Par2Runner.CreateAsync(par2Path, discRoot, layout, scratchDir, ct).ConfigureAwait(false);
            int image = ImageSectors(discRoot);
            if (image <= ceilingSectors)
            {
                return layout with { RecoverySectors = SectorsUnder(recoveryFolder) };
            }

            long excessBytes = (image - ceilingSectors) * (long)OpticalConstants.DataSectorSize;
            int fewer = (int)(excessBytes / layout.BlockSize) + 2;
            if (attempt == 2 || layout.RecoveryBlocks <= fewer)
            {
                throw new InvalidOperationException(
                    $"The disc image would take {image:N0} sectors with its recovery data, more than the {ceilingSectors:N0} allowed.");
            }

            Directory.Delete(recoveryFolder, recursive: true);
            layout = layout with { RecoveryBlocks = layout.RecoveryBlocks - fewer };
        }
    }

    private static int SectorsUnder(string folder) =>
        !Directory.Exists(folder)
            ? 0
            : (int)Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Sum(f => (new FileInfo(f).Length + OpticalConstants.DataSectorSize - 1) / OpticalConstants.DataSectorSize);
}
