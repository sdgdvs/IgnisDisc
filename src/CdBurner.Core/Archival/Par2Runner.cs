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
            $"{baseDirectory}{Path.DirectorySeparatorChar}*"
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
}
