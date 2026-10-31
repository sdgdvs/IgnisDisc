using System.Diagnostics;
using System.Text.RegularExpressions;

namespace CdBurner.Core.DvdVideo;

/// <summary>
/// Locates an available FFmpeg executable on the host system across application directories,
/// user PATH environment variables, package managers (WinGet, Chocolatey), and common installed software.
/// </summary>
public static class FfmpegLocator
{
    private static readonly string[] CommonPaths =
    [
        @"C:\ffmpeg\bin\ffmpeg.exe",
        @"C:\ProgramData\chocolatey\bin\ffmpeg.exe",
    ];

    private static readonly Regex QuotedExeRegex = new(@"""(?<path>[^""]*ffmpeg\.exe)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex UnquotedExeRegex = new(@"(?<path>[A-Za-z]:\\[^ \r\n\t""]+ffmpeg\.exe)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Searches for a valid FFmpeg executable or wrapper script on the system.
    /// </summary>
    /// <param name="customPath">Optional caller-specified path to test first.</param>
    /// <returns>The absolute path to the FFmpeg executable, or <c>null</c> if not found.</returns>
    public static string? FindFfmpegExecutable(string? customPath = null)
    {
        // 1. Explicit custom path
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
                // Ignore path format issues in custom path
            }
        }

        // 2. Application base directory
        string baseDir = AppContext.BaseDirectory;
        if (!string.IsNullOrEmpty(baseDir))
        {
            string[] appCandidates =
            [
                Path.Combine(baseDir, "ffmpeg.exe"),
                Path.Combine(baseDir, "tools", "ffmpeg", "ffmpeg.exe"),
                Path.Combine(baseDir, "ffmpeg", "ffmpeg.exe"),
            ];

            foreach (var candidate in appCandidates)
            {
                try
                {
                    if (File.Exists(candidate))
                    {
                        return Path.GetFullPath(candidate);
                    }
                }
                catch
                {
                    // Ignore candidate read errors
                }
            }
        }

        // 3. System PATH environment directories
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathEnv))
        {
            var directories = pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var dir in directories)
            {
                try
                {
                    if (!Path.IsPathFullyQualified(dir) || !Directory.Exists(dir))
                    {
                        continue;
                    }

                    // Direct ffmpeg.exe in PATH dir
                    string exePath = Path.Combine(dir, "ffmpeg.exe");
                    if (File.Exists(exePath))
                    {
                        return Path.GetFullPath(exePath);
                    }

                    // ffmpeg.cmd in PATH dir
                    string cmdPath = Path.Combine(dir, "ffmpeg.cmd");
                    if (File.Exists(cmdPath))
                    {
                        string? resolved = ResolveFromBatchScript(cmdPath);
                        if (!string.IsNullOrEmpty(resolved))
                        {
                            return Path.GetFullPath(resolved);
                        }
                    }

                    // ffmpeg.bat in PATH dir
                    string batPath = Path.Combine(dir, "ffmpeg.bat");
                    if (File.Exists(batPath))
                    {
                        string? resolved = ResolveFromBatchScript(batPath);
                        if (!string.IsNullOrEmpty(resolved))
                        {
                            return Path.GetFullPath(resolved);
                        }
                    }
                }
                catch
                {
                    // Ignore inaccessible or invalid PATH entries
                }
            }
        }

        // 4. Common known installation paths and WinGet
        foreach (var path in CommonPaths)
        {
            try
            {
                if (File.Exists(path))
                {
                    return Path.GetFullPath(path);
                }
            }
            catch
            {
                // Ignore
            }
        }

        try
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(localAppData))
            {
                string wingetPath = Path.Combine(localAppData, "Microsoft", "WinGet", "Links", "ffmpeg.exe");
                if (File.Exists(wingetPath))
                {
                    return Path.GetFullPath(wingetPath);
                }
            }
        }
        catch
        {
            // Ignore SpecialFolder errors
        }

        return null;
    }

    /// <summary>
    /// Checks whether FFmpeg is available on the system, returning the executable path if found.
    /// </summary>
    /// <param name="executablePath">Outputs the resolved executable path if available.</param>
    /// <param name="customPath">Optional custom path to check first.</param>
    /// <returns><c>true</c> if FFmpeg was found; otherwise, <c>false</c>.</returns>
    public static bool IsFfmpegAvailable(out string? executablePath, string? customPath = null)
    {
        executablePath = FindFfmpegExecutable(customPath);
        return executablePath != null;
    }

    /// <summary>
    /// Checks whether FFmpeg is available on the system.
    /// </summary>
    public static bool IsFfmpegAvailable(string? customPath = null) => IsFfmpegAvailable(out _, customPath);

    private static string? ResolveFromBatchScript(string scriptPath)
    {
        try
        {
            foreach (string rawLine in File.ReadLines(scriptPath))
            {
                string line = rawLine.Trim();
                if (string.IsNullOrEmpty(line) ||
                    line.StartsWith("REM ", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(line, "REM", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("::", StringComparison.Ordinal))
                {
                    continue;
                }

                var quotedMatch = QuotedExeRegex.Match(line);
                if (quotedMatch.Success)
                {
                    string target = quotedMatch.Groups["path"].Value;
                    if (Path.IsPathFullyQualified(target) && File.Exists(target))
                    {
                        return target;
                    }
                }

                var unquotedMatch = UnquotedExeRegex.Match(line);
                if (unquotedMatch.Success)
                {
                    string target = unquotedMatch.Groups["path"].Value;
                    if (Path.IsPathFullyQualified(target) && File.Exists(target))
                    {
                        return target;
                    }
                }
            }
        }
        catch
        {
            // Ignore unreadable script files
        }

        return null;
    }
}
