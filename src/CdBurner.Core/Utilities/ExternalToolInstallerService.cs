using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;

namespace CdBurner.Core.Utilities;

/// <summary>
/// Result of downloading and extracting an optional external tool executable.
/// </summary>
/// <param name="Success">True if the executable was downloaded and extracted successfully.</param>
/// <param name="ExecutablePath">Full path to the installed executable on disk, or null on failure.</param>
/// <param name="LicensePath">Full path to the extracted license file next to the executable, if present in the archive.</param>
/// <param name="ErrorMessage">Error description if the installation failed; otherwise null.</param>
public sealed record ToolInstallResult(
    bool Success,
    string? ExecutablePath,
    string? LicensePath,
    string? ErrorMessage = null);

/// <summary>
/// Downloads and extracts optional external command-line tools (<c>ffmpeg.exe</c> and <c>par2.exe</c>)
/// into per-user <c>%LOCALAPPDATA%\Programs</c> directories without requiring administrator rights.
/// </summary>
public static class ExternalToolInstallerService
{
    /// <summary>
    /// Official BtbN LGPL v2.1 Windows x64 FFmpeg release archive URL.
    /// </summary>
    public const string DefaultFfmpegLgplWin64Url =
        "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-lgpl.zip";

    /// <summary>
    /// Official Parchive par2cmdline v1.4.0 Windows x64 release archive URL.
    /// </summary>
    public const string DefaultPar2WinX64Url =
        "https://github.com/Parchive/par2cmdline/releases/download/v1.4.0/par2cmdline-1.4.0-win-x64.zip";

    /// <summary>
    /// Official Parchive par2cmdline v1.4.0 Windows ARM64 release archive URL.
    /// </summary>
    public const string DefaultPar2WinArm64Url =
        "https://github.com/Parchive/par2cmdline/releases/download/v1.4.0/par2cmdline-1.4.0-win-arm64.zip";

    /// <summary>
    /// User-Agent header sent on GitHub release downloads.
    /// </summary>
    public const string DefaultUserAgent = "IgnisDisc-OpticalStudio (+https://github.com/tether-works/IgnisDisc)";

    /// <summary>
    /// Returns the default per-user directory where FFmpeg is installed (<c>%LOCALAPPDATA%\Programs\ffmpeg</c>).
    /// </summary>
    public static string GetDefaultFfmpegInstallDirectory()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "Programs", "ffmpeg");
    }

    /// <summary>
    /// Returns the default per-user directory where par2cmdline is installed (<c>%LOCALAPPDATA%\Programs\par2cmdline</c>).
    /// </summary>
    public static string GetDefaultPar2InstallDirectory()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "Programs", "par2cmdline");
    }

    /// <summary>
    /// Downloads and extracts the LGPL Windows build of <c>ffmpeg.exe</c> (and its companion license text)
    /// into the specified or default per-user directory.
    /// </summary>
    public static Task<ToolInstallResult> InstallFfmpegAsync(
        string? targetDirectory = null,
        HttpClient? httpClient = null,
        string? downloadUrlOverride = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        string installDir = string.IsNullOrWhiteSpace(targetDirectory)
            ? GetDefaultFfmpegInstallDirectory()
            : Path.GetFullPath(targetDirectory);
        string url = string.IsNullOrWhiteSpace(downloadUrlOverride)
            ? DefaultFfmpegLgplWin64Url
            : downloadUrlOverride;

        return DownloadAndExtractToolAsync(
            toolDisplayName: "FFmpeg (LGPL)",
            executableFileName: "ffmpeg.exe",
            licenseOutputFileName: "ffmpeg-LICENSE.txt",
            downloadUrl: url,
            installDirectory: installDir,
            httpClient: httpClient,
            progress: progress,
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Downloads and extracts <c>par2.exe</c> (<c>par2cmdline</c>) into the specified or default per-user directory.
    /// </summary>
    public static Task<ToolInstallResult> InstallPar2Async(
        string? targetDirectory = null,
        HttpClient? httpClient = null,
        string? downloadUrlOverride = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        string installDir = string.IsNullOrWhiteSpace(targetDirectory)
            ? GetDefaultPar2InstallDirectory()
            : Path.GetFullPath(targetDirectory);
        string defaultUrl = RuntimeInformation.OSArchitecture == Architecture.Arm64
            ? DefaultPar2WinArm64Url
            : DefaultPar2WinX64Url;
        string url = string.IsNullOrWhiteSpace(downloadUrlOverride)
            ? defaultUrl
            : downloadUrlOverride;

        return DownloadAndExtractToolAsync(
            toolDisplayName: "par2cmdline (par2.exe)",
            executableFileName: "par2.exe",
            licenseOutputFileName: "par2-LICENSE.txt",
            downloadUrl: url,
            installDirectory: installDir,
            httpClient: httpClient,
            progress: progress,
            cancellationToken: cancellationToken);
    }

    private static async Task<ToolInstallResult> DownloadAndExtractToolAsync(
        string toolDisplayName,
        string executableFileName,
        string licenseOutputFileName,
        string downloadUrl,
        string installDirectory,
        HttpClient? httpClient,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        bool ownsClient = httpClient == null;
        HttpClient client = httpClient ?? CreateDefaultHttpClient();

        string tempZipPath = Path.Combine(Path.GetTempPath(), $"ignisdisc_tool_{Guid.NewGuid():N}.zip");

        try
        {
            Directory.CreateDirectory(installDirectory);
            progress?.Report($"Downloading {toolDisplayName}...");

            using (var request = new HttpRequestMessage(HttpMethod.Get, downloadUrl))
            {
                if (!request.Headers.UserAgent.Any())
                {
                    request.Headers.UserAgent.ParseAdd(DefaultUserAgent);
                }

                using var response = await client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    return new ToolInstallResult(
                        false,
                        null,
                        null,
                        $"HTTP {(int)response.StatusCode} ({response.ReasonPhrase}) while downloading {toolDisplayName}.");
                }

                long? totalBytes = response.Content.Headers.ContentLength;
                await using var networkStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using var fileStream = new FileStream(tempZipPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

                byte[] buffer = new byte[81920];
                long totalRead = 0;
                int bytesRead;
                while ((bytesRead = await networkStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
                    totalRead += bytesRead;

                    if (totalBytes is > 0)
                    {
                        double pct = (double)totalRead / totalBytes.Value * 100.0;
                        double mbRead = totalRead / (1024.0 * 1024.0);
                        double mbTotal = totalBytes.Value / (1024.0 * 1024.0);
                        progress?.Report($"Downloading {toolDisplayName}... {pct:F0}% ({mbRead:F1} / {mbTotal:F1} MB)");
                    }
                }
            }

            progress?.Report($"Extracting {executableFileName}...");

            string targetExePath = Path.Combine(installDirectory, executableFileName);
            string tempExePath = targetExePath + ".tmp";
            string? extractedLicensePath = null;

            using (var archive = ZipFile.OpenRead(tempZipPath))
            {
                var exeEntry = archive.Entries.FirstOrDefault(e =>
                    string.Equals(Path.GetFileName(e.FullName), executableFileName, StringComparison.OrdinalIgnoreCase) &&
                    e.Length > 0);

                if (exeEntry == null)
                {
                    return new ToolInstallResult(
                        false,
                        null,
                        null,
                        $"Archive did not contain '{executableFileName}'.");
                }

                exeEntry.ExtractToFile(tempExePath, overwrite: true);

                var licenseEntry = archive.Entries.FirstOrDefault(e =>
                {
                    string fileName = Path.GetFileName(e.FullName);
                    return e.Length > 0 &&
                           (fileName.StartsWith("LICENSE", StringComparison.OrdinalIgnoreCase) ||
                            fileName.StartsWith("COPYING", StringComparison.OrdinalIgnoreCase));
                });

                if (licenseEntry != null)
                {
                    string targetLicense = Path.Combine(installDirectory, licenseOutputFileName);
                    licenseEntry.ExtractToFile(targetLicense, overwrite: true);
                    extractedLicensePath = targetLicense;
                }
            }

            File.Move(tempExePath, targetExePath, overwrite: true);
            progress?.Report($"{toolDisplayName} installed at {targetExePath}.");

            return new ToolInstallResult(true, targetExePath, extractedLicensePath, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ToolInstallResult(false, null, null, ex.Message);
        }
        finally
        {
            if (ownsClient)
            {
                client.Dispose();
            }

            try
            {
                if (File.Exists(tempZipPath))
                {
                    File.Delete(tempZipPath);
                }
            }
            catch
            {
            }
        }
    }

    private static HttpClient CreateDefaultHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(5)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(DefaultUserAgent);
        return client;
    }
}
