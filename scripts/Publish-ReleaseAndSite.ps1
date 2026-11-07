<#
.SYNOPSIS
    Publishes IgnisDisc binaries to GitHub Releases and deploys the Product + FDE Case Study site to Firebase Hosting.

.DESCRIPTION
    1. Verifies (or builds) artifacts/installer/IgnisDisc-Setup.exe and IgnisDisc-Portable-win-x64.zip.
    2. Computes SHA-256 checksums and stamps them + your GitHub/FDE contact links into web/public/app.js.
    3. Creates/updates the GitHub Release via `gh release create` so the 240+ MB binaries are served from GitHub's global CDN (preserving Firebase's 10 GB/mo free tier).
    4. Deploys the static site (`web/public`) to Firebase Hosting via `npx firebase-tools deploy --only hosting`.

.EXAMPLE
    pwsh -ExecutionPolicy Bypass -File scripts/Publish-ReleaseAndSite.ps1 `
        -GitHubRepo "yourusername/ignisdisc" `
        -FirebaseProjectId "ignisdisc-studio" `
        -EngineerName "Your Name" `
        -ContactEmail "you@yourdomain.com" `
        -SocialUrl "https://linkedin.com/in/yourprofile"
#>
param(
    [string]$Version = "1.08",
    [string]$GitHubRepo = "",
    [string]$FirebaseProjectId = "ignisdisc-studio",
    [string]$EngineerInitials = "DT",
    [string]$EngineerName = "tetherworks Systems",
    [string]$EngineerRole = "Open-Source Hardware & Software Systems",
    [string]$ContactEmail = "dev@tetherworks.dev",
    [switch]$RebuildBinaries,
    [switch]$SkipGitHubRelease,
    [switch]$SkipFirebaseDeploy
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot
$InstallerExe = Join-Path $RepoRoot "artifacts\installer\IgnisDisc-Setup.exe"
$PortableZip  = Join-Path $RepoRoot "artifacts\installer\IgnisDisc-Portable-win-x64.zip"

if ($RebuildBinaries -or -not (Test-Path $InstallerExe) -or -not (Test-Path $PortableZip)) {
    Write-Host "==> Building IgnisDisc v$Version Installer & Portable ZIP..." -ForegroundColor Cyan
    & pwsh -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "Build-Installer.ps1")
}

$SetupHash    = (Get-FileHash $InstallerExe -Algorithm SHA256).Hash
$PortableHash = (Get-FileHash $PortableZip  -Algorithm SHA256).Hash

Write-Host "==> Verified Release Artifacts:" -ForegroundColor Green
Write-Host "    IgnisDisc-Setup.exe            SHA-256: $SetupHash"
Write-Host "    IgnisDisc-Portable-win-x64.zip SHA-256: $PortableHash"

# Stamp SITE_CONFIG in web/public/app.js
function Escape-JsAndRegexReplacement([string]$Value) {
    if ($null -eq $Value) { return "" }
    # Escape backslashes and double quotes for JS string literals, then double '$' for PowerShell -replace
    $escaped = $Value.Replace('\', '\\').Replace('"', '\"').Replace('$', '$$')
    return $escaped
}

if (-not [string]::IsNullOrWhiteSpace($GitHubRepo)) {
    $GithubRepoUrl = "https://github.com/$GitHubRepo"
    $SetupUrl      = "$GithubRepoUrl/releases/download/v$Version/IgnisDisc-Setup.exe"
    $PortableUrl   = "$GithubRepoUrl/releases/download/v$Version/IgnisDisc-Portable-win-x64.zip"
    $MailtoUrl     = "mailto:$ContactEmail?subject=IgnisDisc%20%2F%20Systems%20Engineering%20Inquiry"

    $AppJsPath = Join-Path $RepoRoot "web\public\app.js"
    $AppJs = Get-Content $AppJsPath -Raw
    $AppJs = $AppJs -replace 'version:\s*"[^"]*"', ("version: `"" + (Escape-JsAndRegexReplacement $Version) + "`"")
    $AppJs = $AppJs -replace 'githubRepoUrl:\s*"[^"]*"', ("githubRepoUrl: `"" + (Escape-JsAndRegexReplacement $GithubRepoUrl) + "`"")
    $AppJs = $AppJs -replace 'setupDownloadUrl:\s*"[^"]*"', ("setupDownloadUrl: `"" + (Escape-JsAndRegexReplacement $SetupUrl) + "`"")
    $AppJs = $AppJs -replace 'portableDownloadUrl:\s*"[^"]*"', ("portableDownloadUrl: `"" + (Escape-JsAndRegexReplacement $PortableUrl) + "`"")
    $AppJs = $AppJs -replace 'initials:\s*"[^"]*"', ("initials: `"" + (Escape-JsAndRegexReplacement $EngineerInitials) + "`"")
    $AppJs = $AppJs -replace 'name:\s*"[^"]*"', ("name: `"" + (Escape-JsAndRegexReplacement $EngineerName) + "`"")
    $AppJs = $AppJs -replace 'role:\s*"[^"]*"', ("role: `"" + (Escape-JsAndRegexReplacement $EngineerRole) + "`"")
    $AppJs = $AppJs -replace 'email:\s*"[^"]*"', ("email: `"" + (Escape-JsAndRegexReplacement $MailtoUrl) + "`"")
    Set-Content -Path $AppJsPath -Value $AppJs -Encoding UTF8
    Write-Host "==> Updated web/public/app.js with GitHub release URLs and FDE profile." -ForegroundColor Green
}

# Stamp .firebaserc project ID
$FirebaseRcPath = Join-Path $RepoRoot ".firebaserc"
$FirebaseRcJson = @{
    projects = @{
        default = $FirebaseProjectId
    }
} | ConvertTo-Json -Depth 4
Set-Content -Path $FirebaseRcPath -Value $FirebaseRcJson -Encoding UTF8

# Publish GitHub Release
if (-not $SkipGitHubRelease -and -not [string]::IsNullOrWhiteSpace($GitHubRepo)) {
    Write-Host "==> Publishing v$Version binaries to GitHub Releases ($GitHubRepo)..." -ForegroundColor Cyan
    $ReleaseNotes = @"
## IgnisDisc Optical Studio v$Version

### Downloads & SHA-256 Checksums
- **Standalone Setup Wizard (`IgnisDisc-Setup.exe`)**: `$SetupHash`
- **Portable Archive (`IgnisDisc-Portable-win-x64.zip`)**: `$PortableHash`

### Highlights
- Native .NET 10 (`win-x64`) Windows 10/11 Fluent Desktop Studio (`IgnisDisc.exe`) + `ignisdisc-cli.exe`
- ISO 9660 / Joliet / UDF 1.02 Bridge, Multi-Session CD/DVD, Dual BIOS + UEFI El Torito Boot, and Red Book DAO/TAO Audio
- Secure C2 Audio Ripping + AccurateRip + Read-Offset Detection, and Multi-Disc Archive Studio (SHA-256 manifests + PAR2)
- Max supported write-speed negotiation, finalized CD-RW erase guard, Full TOC (`0x02`) surface scanning & SCSI `0x1B` (`START_STOP_UNIT`) spin-down
- Verified against 481 automated hardware-free tests via `VirtualDiscBackend` plus physical optical media testing
"@
    & gh release create "v$Version" $InstallerExe $PortableZip --repo $GitHubRepo --title "IgnisDisc Optical Studio v$Version" --notes $ReleaseNotes
}

# Deploy to Firebase Hosting
if (-not $SkipFirebaseDeploy) {
    Write-Host "==> Deploying web/public to Firebase Hosting ($FirebaseProjectId)..." -ForegroundColor Cyan
    Push-Location $RepoRoot
    try {
        & npx --yes firebase-tools deploy --only hosting --project $FirebaseProjectId
    }
    finally {
        Pop-Location
    }
}
