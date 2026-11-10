using System.Diagnostics;

namespace CdBurner.Core.Archival;

/// <summary>
/// Locates and invokes external par2cmdline executable to generate recovery data.
/// </summary>
public static class Par2Runner
{
    /// <summary>
    /// Searches for par2cmdline on the host machine.
    /// </summary>
    /// <param name="customPath">Optional custom path to verify first.</param>
    /// <returns>Path to par2 executable, or null if not found.</returns>
    public static string? FindPar2Executable(string? customPath = null)
    {
        if (!string.IsNullOrWhiteSpace(customPath))
        {
            try
            {
                if (File.Exists(customPath))
                {
                    return Path.GetFullPath(customPath);
                }
            }
            catch
            {
            }
        }

        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathEnv))
        {
            var dirs = pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var dir in dirs)
            {
                try
                {
                    if (!Directory.Exists(dir))
                    {
                        continue;
                    }

                    string exePath = Path.Combine(dir, "par2.exe");
                    if (File.Exists(exePath))
                    {
                        return Path.GetFullPath(exePath);
                    }

                    string noExtPath = Path.Combine(dir, "par2");
                    if (File.Exists(noExtPath))
                    {
                        return Path.GetFullPath(noExtPath);
                    }
                }
                catch
                {
                }
            }
        }

        // Per-user install location used when par2cmdline is unpacked from its GitHub release (not on PATH).
        string localPrograms = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "par2cmdline", "par2.exe");
        if (File.Exists(localPrograms))
        {
            return localPrograms;
        }

        string baseDir = AppContext.BaseDirectory;
        if (!string.IsNullOrEmpty(baseDir))
        {
            try
            {
                string appExe = Path.Combine(baseDir, "par2.exe");
                if (File.Exists(appExe))
                {
                    return Path.GetFullPath(appExe);
                }
            }
            catch
            {
            }
        }

        return null;
    }

    /// <summary>
    /// Builds the argument list for par2 create command in the exact order required.
    /// </summary>
    /// <param name="outputPar2Path">Output .par2 file path.</param>
    /// <param name="baseDirectory">Base directory for relative paths in recovery volume.</param>
    /// <param name="redundancyPercent">Redundancy percentage (e.g. 10).</param>
    /// <returns>List of CLI arguments.</returns>
    public static IReadOnlyList<string> BuildCreateArguments(string outputPar2Path, string baseDirectory, int redundancyPercent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPar2Path);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

        return new List<string>
        {
            "create",
            "-q",
            $"-r{redundancyPercent}",
            "-R",
            $"-B{baseDirectory}",
            outputPar2Path,
            // Bare wildcard, expanded by par2 relative to the working directory (the disc root). A quoted
            // "C:\path with spaces\*" would not be expanded by the Windows C runtime.
            "*"
        };
    }

    /// <summary>
    /// Runs par2cmdline in a scratch folder to create recovery volumes, then moves them to discRoot/RECOVERY.
    /// </summary>
    /// <param name="par2Path">Path to par2 executable.</param>
    /// <param name="discRoot">Local directory of the staged disc root.</param>
    /// <param name="redundancyPercent">Redundancy percentage.</param>
    /// <param name="scratchDirectory">Temporary working directory for par2 generation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">Thrown if par2 returns a non-zero exit code.</exception>
    public static async Task CreateAsync(
        string par2Path,
        string discRoot,
        int redundancyPercent,
        string scratchDirectory,
        CancellationToken cancellationToken = default)
    {
        if (redundancyPercent <= 0)
        {
            return;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(par2Path);
        ArgumentException.ThrowIfNullOrWhiteSpace(discRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(scratchDirectory);

        if (!File.Exists(par2Path))
        {
            throw new FileNotFoundException($"par2 executable not found at '{par2Path}'.", par2Path);
        }

        if (!Directory.Exists(discRoot))
        {
            throw new DirectoryNotFoundException($"Disc root directory not found: '{discRoot}'.");
        }

        Directory.CreateDirectory(scratchDirectory);

        string outputPar2Path = Path.Combine(scratchDirectory, "recovery.par2");
        var args = BuildCreateArguments(outputPar2Path, discRoot, redundancyPercent);

        var psi = new ProcessStartInfo
        {
            FileName = par2Path,
            WorkingDirectory = discRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var proc = new Process { StartInfo = psi };

        if (!proc.Start())
        {
            throw new InvalidOperationException($"Failed to start par2 process at '{par2Path}'.");
        }

        var outTask = proc.StandardOutput.ReadToEndAsync(cancellationToken);
        var errTask = proc.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await Task.WhenAll(outTask, errTask, proc.WaitForExitAsync(cancellationToken)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!proc.HasExited)
                {
                    proc.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }
            throw;
        }
        catch
        {
            try
            {
                if (!proc.HasExited)
                {
                    proc.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }
            throw;
        }

        if (proc.ExitCode != 0)
        {
            string stderr = errTask.IsCompletedSuccessfully ? errTask.Result : string.Empty;
            string lastStderr = stderr.Length > 500 ? stderr[^500..] : stderr;
            throw new InvalidOperationException(
                $"par2 creation failed with exit code {proc.ExitCode}: {lastStderr}");
        }

        string recoveryFolder = Path.Combine(discRoot, ArchiveManifest.RecoveryFolderName);
        Directory.CreateDirectory(recoveryFolder);

        foreach (var file in Directory.EnumerateFiles(scratchDirectory, "*.par2"))
        {
            string dest = Path.Combine(recoveryFolder, Path.GetFileName(file));
            File.Move(file, dest, overwrite: true);
        }
    }

    /// <summary>
    /// Suffix given to a recovery volume while a repair attempt runs without it. par2 only loads "*.par2".
    /// </summary>
    private const string HeldVolumeSuffix = ".held";

    /// <summary>
    /// Repairs a disc copy in place: runs "par2 repair -q -B. RECOVERY\recovery.par2" with WorkingDirectory = folderRoot.
    /// <para>
    /// par2 solves for the damaged blocks with the lowest-numbered recovery blocks it has. When a recovery volume
    /// is missing those numbers have gaps, and for some combinations of damaged blocks the equations cannot be
    /// solved ("RS computation error") even though spare recovery blocks exist. In that case the repair is tried
    /// again with one recovery volume set aside at a time, which gives par2 a different set of equations.
    /// </para>
    /// <para>
    /// A failed attempt can leave a damaged original renamed to "name.1" with nothing under its own name; such
    /// files are renamed back, so a failed repair never changes which files exist.
    /// </para>
    /// </summary>
    /// <param name="par2Path">Path to par2 executable.</param>
    /// <param name="folderRoot">Root folder containing the disc copy to repair.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The exit code (0 = repaired or nothing to do) and the backup files par2 left behind.</returns>
    /// <exception cref="ArgumentException">Thrown if parameters are null or whitespace.</exception>
    /// <exception cref="FileNotFoundException">Thrown if par2 or RECOVERY\recovery.par2 is missing.</exception>
    /// <exception cref="DirectoryNotFoundException">Thrown if folderRoot directory does not exist.</exception>
    public static async Task<Par2RepairResult> RepairAsync(
        string par2Path,
        string folderRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(par2Path);
        ArgumentException.ThrowIfNullOrWhiteSpace(folderRoot);

        if (!Directory.Exists(folderRoot))
        {
            throw new DirectoryNotFoundException($"Folder root directory not found: '{folderRoot}'.");
        }

        string recoveryFolder = Path.Combine(folderRoot, ArchiveManifest.RecoveryFolderName);
        ReleaseHeldVolumes(recoveryFolder);

        var filesBefore = new HashSet<string>(
            Directory.EnumerateFiles(folderRoot, "*", SearchOption.AllDirectories), StringComparer.OrdinalIgnoreCase);

        int exitCode;
        string errorOutput;
        int attempts = 1;
        try
        {
            (exitCode, errorOutput) = await RunRepairOnceAsync(par2Path, folderRoot, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            RestoreDisplacedOriginals(folderRoot, filesBefore);
            throw;
        }

        if (exitCode != 0)
        {
            RestoreDisplacedOriginals(folderRoot, filesBefore);

            if (errorOutput.Contains("RS computation error", StringComparison.OrdinalIgnoreCase))
            {
                var volumes = Directory.EnumerateFiles(recoveryFolder, "*.vol*.par2")
                    .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                foreach (string volume in volumes)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    string held = volume + HeldVolumeSuffix;
                    File.Move(volume, held);
                    int retryExitCode;
                    try
                    {
                        (retryExitCode, _) = await RunRepairOnceAsync(par2Path, folderRoot, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        RestoreDisplacedOriginals(folderRoot, filesBefore);
                        throw;
                    }
                    finally
                    {
                        File.Move(held, volume);
                    }

                    attempts++;
                    if (retryExitCode == 0)
                    {
                        exitCode = 0;
                        errorOutput = string.Empty;
                        break;
                    }

                    RestoreDisplacedOriginals(folderRoot, filesBefore);
                }
            }
        }

        return new Par2RepairResult(exitCode, errorOutput, FindRepairBackups(folderRoot, filesBefore), attempts);
    }

    /// <summary>
    /// Puts back recovery volumes that an interrupted earlier repair left set aside.
    /// </summary>
    private static void ReleaseHeldVolumes(string recoveryFolder)
    {
        if (!Directory.Exists(recoveryFolder))
        {
            return;
        }

        foreach (string held in Directory.EnumerateFiles(recoveryFolder, "*.par2" + HeldVolumeSuffix).ToList())
        {
            string volume = held[..^HeldVolumeSuffix.Length];
            if (!File.Exists(volume))
            {
                File.Move(held, volume);
            }
        }
    }

    /// <summary>
    /// Files created since <paramref name="filesBefore"/> whose name is an earlier file's name plus ".&lt;number&gt;":
    /// the copies par2 makes of each original before it writes the repaired file.
    /// </summary>
    private static List<(string Backup, string Original)> FindNewBackups(string folderRoot, HashSet<string> filesBefore)
    {
        var backups = new List<(string, string)>();
        foreach (string file in Directory.EnumerateFiles(folderRoot, "*", SearchOption.AllDirectories))
        {
            if (filesBefore.Contains(file))
            {
                continue;
            }

            int dot = file.LastIndexOf('.');
            if (dot <= 0 || dot == file.Length - 1 || file.AsSpan(dot + 1).ContainsAnyExceptInRange('0', '9'))
            {
                continue;
            }

            string original = file[..dot];
            if (filesBefore.Contains(original))
            {
                backups.Add((file, original));
            }
        }

        return backups;
    }

    private static void RestoreDisplacedOriginals(string folderRoot, HashSet<string> filesBefore)
    {
        foreach (var (backup, original) in FindNewBackups(folderRoot, filesBefore))
        {
            if (!File.Exists(original))
            {
                File.Move(backup, original);
            }
        }
    }

    private static IReadOnlyList<string> FindRepairBackups(string folderRoot, HashSet<string> filesBefore) =>
        FindNewBackups(folderRoot, filesBefore)
            .Where(b => File.Exists(b.Original))
            .Select(b => b.Backup)
            .ToList();

    private static async Task<(int ExitCode, string ErrorOutput)> RunRepairOnceAsync(
        string par2Path,
        string folderRoot,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(par2Path);
        ArgumentException.ThrowIfNullOrWhiteSpace(folderRoot);

        if (!File.Exists(par2Path))
        {
            throw new FileNotFoundException($"par2 executable not found at '{par2Path}'.", par2Path);
        }

        if (!Directory.Exists(folderRoot))
        {
            throw new DirectoryNotFoundException($"Folder root directory not found: '{folderRoot}'.");
        }

        string recoveryPar2 = Path.Combine(folderRoot, ArchiveManifest.RecoveryFolderName, "recovery.par2");
        if (!File.Exists(recoveryPar2))
        {
            throw new FileNotFoundException($"Recovery file not found: '{recoveryPar2}'.", recoveryPar2);
        }

        var psi = new ProcessStartInfo
        {
            FileName = par2Path,
            WorkingDirectory = folderRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        psi.ArgumentList.Add("repair");
        psi.ArgumentList.Add("-q");
        psi.ArgumentList.Add("-B.");
        psi.ArgumentList.Add(Path.Combine(ArchiveManifest.RecoveryFolderName, "recovery.par2"));

        using var proc = new Process { StartInfo = psi };

        if (!proc.Start())
        {
            throw new InvalidOperationException($"Failed to start par2 process at '{par2Path}'.");
        }

        var outTask = proc.StandardOutput.ReadToEndAsync(cancellationToken);
        var errTask = proc.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await Task.WhenAll(outTask, errTask, proc.WaitForExitAsync(cancellationToken)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!proc.HasExited)
                {
                    proc.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }
            throw;
        }
        catch
        {
            try
            {
                if (!proc.HasExited)
                {
                    proc.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }
            throw;
        }

        return (proc.ExitCode, errTask.IsCompletedSuccessfully ? errTask.Result : string.Empty);
    }
}

/// <summary>
/// Outcome of <see cref="Par2Runner.RepairAsync"/>.
/// </summary>
/// <param name="ExitCode">par2's exit code for the attempt that counted (0 = repaired or nothing to do).</param>
/// <param name="ErrorOutput">What par2 wrote to its error stream when the repair failed; empty on success.</param>
/// <param name="BackupFiles">Full paths of the "name.N" copies of damaged originals that par2 created during this
/// repair and whose original exists again. Files that were already there before the repair are never listed.</param>
/// <param name="Attempts">Number of par2 runs made.</param>
public sealed record Par2RepairResult(int ExitCode, string ErrorOutput, IReadOnlyList<string> BackupFiles, int Attempts);
