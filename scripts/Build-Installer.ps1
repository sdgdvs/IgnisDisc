param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$FfmpegPath = "",
    [string]$FfmpegLicensePath = ""
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot
$PublishDir = Join-Path $RepoRoot "artifacts\publish\$Runtime"
$InstallerDir = Join-Path $RepoRoot "artifacts\installer"
$SetupProjectDir = Join-Path $RepoRoot "tools\CdBurner.Setup"
$BootstrapperProjectDir = Join-Path $RepoRoot "tools\CdBurner.SetupBootstrapper"
$PayloadZipPath = Join-Path $BootstrapperProjectDir "payload.zip"
$PortableZipPath = Join-Path $InstallerDir "IgnisDisc-Portable-$Runtime.zip"

Write-Host "=== Step 1: Publishing Self-Contained IgnisDisc (GUI & CLI, $Runtime) ===" -ForegroundColor Cyan
if (Test-Path $PublishDir) {
    Remove-Item -Path $PublishDir -Recurse -Force
}
if (Test-Path $InstallerDir) {
    Remove-Item -Path $InstallerDir -Recurse -Force
}
New-Item -ItemType Directory -Path $PublishDir -Force | Out-Null
New-Item -ItemType Directory -Path $InstallerDir -Force | Out-Null

dotnet publish (Join-Path $RepoRoot "src\CdBurner.App\CdBurner.App.csproj") `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=false `
    -o $PublishDir
if ($LASTEXITCODE -ne 0) { throw "Failed to publish IgnisDisc GUI App" }

dotnet publish (Join-Path $RepoRoot "src\CdBurner.Cli\CdBurner.Cli.csproj") `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=false `
    -o $PublishDir
if ($LASTEXITCODE -ne 0) { throw "Failed to publish CLI" }

# The setup wizard / uninstaller (Uninstall-IgnisDisc.exe) is published into the same folder so it shares the app's
# .NET runtime instead of carrying its own copy.
dotnet publish (Join-Path $SetupProjectDir "CdBurner.Setup.csproj") `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=false `
    -o $PublishDir
if ($LASTEXITCODE -ne 0) { throw "Failed to publish the setup wizard" }

# Provide convenience launcher aliases so both IgnisDisc.exe / CdBurner.App.exe and ignisdisc-cli.exe / cdburner-cli.exe work seamlessly
$IgnisGuiExe = Join-Path $PublishDir "IgnisDisc.exe"
$CdBurnerGuiExe = Join-Path $PublishDir "CdBurner.App.exe"
if (Test-Path $IgnisGuiExe) {
    Copy-Item -Path $IgnisGuiExe -Destination $CdBurnerGuiExe -Force
}

$IgnisCliExe = Join-Path $PublishDir "ignisdisc-cli.exe"
$CdBurnerCliExe = Join-Path $PublishDir "cdburner-cli.exe"
if (Test-Path $CdBurnerCliExe) {
    Copy-Item -Path $CdBurnerCliExe -Destination $IgnisCliExe -Force
}

# Optionally bundle ffmpeg.exe for DVD-Video transcoding.
# Only an ffmpeg build you deliberately pass in is bundled, together with its license text, because ffmpeg is
# LGPL/GPL software and redistributing it requires shipping the license and offering the corresponding source.
# Recommended: an official LGPL build (e.g. https://github.com/BtbN/FFmpeg-Builds, "lgpl" variant).
if ($FfmpegPath) {
    if (-not (Test-Path $FfmpegPath)) { throw "FfmpegPath not found: $FfmpegPath" }
    if (-not $FfmpegLicensePath -or -not (Test-Path $FfmpegLicensePath)) {
        throw "When bundling ffmpeg, pass -FfmpegLicensePath pointing at the LICENSE file that ships with that ffmpeg build."
    }
    Copy-Item -Path $FfmpegPath -Destination (Join-Path $PublishDir "ffmpeg.exe") -Force
    Copy-Item -Path $FfmpegLicensePath -Destination (Join-Path $PublishDir "ffmpeg-LICENSE.txt") -Force
    Write-Host "Bundled ffmpeg.exe and its license for DVD-Video transcoding" -ForegroundColor Green
} else {
    Write-Host "ffmpeg not bundled (pass -FfmpegPath and -FfmpegLicensePath to include it). DVD-Video transcoding will use an ffmpeg found on PATH." -ForegroundColor Yellow
}

# Ship the project license and third-party notices with every build
foreach ($doc in @("LICENSE", "THIRD-PARTY-NOTICES.md")) {
    $src = Join-Path $RepoRoot $doc
    if (Test-Path $src) { Copy-Item -Path $src -Destination (Join-Path $PublishDir $doc) -Force }
}

Write-Host "=== Step 2: Packaging Portable Archive & Setup Payload ===" -ForegroundColor Cyan
if (Test-Path $PortableZipPath) { Remove-Item $PortableZipPath -Force }
if (Test-Path $PayloadZipPath) { Remove-Item $PayloadZipPath -Force }

# Portable zip: the app and CLI without the installer's wizard/uninstaller.
$portableItems = Get-ChildItem -LiteralPath $PublishDir | Where-Object { $_.Name -notlike "Uninstall-IgnisDisc*" }
Compress-Archive -LiteralPath $portableItems.FullName -DestinationPath $PortableZipPath -CompressionLevel Optimal -Force

# Installer payload: everything, including the wizard that installs it.
Compress-Archive -Path "$PublishDir\*" -DestinationPath $PayloadZipPath -CompressionLevel Optimal -Force

Write-Host "=== Step 3: Building the Setup Launcher (IgnisDisc-Setup.exe, .NET Framework 4.8 built into Windows) ===" -ForegroundColor Cyan
try {
    dotnet build (Join-Path $BootstrapperProjectDir "CdBurner.SetupBootstrapper.csproj") -c $Configuration
    if ($LASTEXITCODE -ne 0) { throw "Failed to build IgnisDisc-Setup launcher" }
    Copy-Item -Path (Join-Path $BootstrapperProjectDir "bin\$Configuration\net48\IgnisDisc-Setup.exe") -Destination $InstallerDir -Force
}
finally {
    if (Test-Path $PayloadZipPath) {
        Remove-Item $PayloadZipPath -Force
    }
}

$SetupExe = Join-Path $InstallerDir "IgnisDisc-Setup.exe"
if (-not (Test-Path $SetupExe)) {
    throw "Installer executable was not produced at $SetupExe"
}

$SetupInfo = Get-Item $SetupExe
$ZipInfo = Get-Item $PortableZipPath
Write-Host ""
Write-Host "=== IgnisDisc Installer Build Succeeded ===" -ForegroundColor Green
Write-Host ("Standalone Installer : {0} ({1:N1} MB)" -f $SetupInfo.FullName, ($SetupInfo.Length / 1MB))
Write-Host ("Portable Zip Archive : {0} ({1:N1} MB)" -f $ZipInfo.FullName, ($ZipInfo.Length / 1MB))
Write-Host ("Self-Contained App   : {0}" -f (Join-Path $PublishDir "IgnisDisc.exe"))
