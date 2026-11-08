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
    [string]$Version = "1.10",
    [string]$GitHubRepo = "",
    [string]$FirebaseProjectId = "ignisdisc-studio",
    [string]$EngineerInitials = "TW",
    [string]$EngineerName = "tetherworks",
    [string]$EngineerRole = "Open-Source Hardware & Software Systems",
    [string]$ContactEmail = "developer@tetherworks.local",
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
    $MailtoUrl     = "mailto:${ContactEmail}?subject=IgnisDisc%20%2F%20Systems%20Engineering%20Note"

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
    # No slipstreaming: a published version's files are never replaced. Rebuilds get a new version number,
    # otherwise the checksums on the site, in the WinGet manifest and in users' hands stop matching.
    & gh release view "v$Version" --repo $GitHubRepo *> $null
    if ($LASTEXITCODE -eq 0) {
        throw "Release v$Version already exists on $GitHubRepo. Do not replace published files; bump the version with scripts/Update-Version.ps1 instead."
    }

    # Release notes are written and reviewed by hand (facts only) in docs/release-notes/v<Version>.md.
    $NotesPath = Join-Path $RepoRoot "docs\release-notes\v$Version.md"
    if (-not (Test-Path $NotesPath)) {
        throw "Release notes not found at $NotesPath. Write them before publishing."
    }

    Write-Host "==> Publishing v$Version binaries to GitHub Releases ($GitHubRepo)..." -ForegroundColor Cyan
    & gh release create "v$Version" $InstallerExe $PortableZip --repo $GitHubRepo --title "IgnisDisc v$Version" --notes-file $NotesPath
    if ($LASTEXITCODE -ne 0) { throw "gh release create failed with exit code $LASTEXITCODE" }
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
