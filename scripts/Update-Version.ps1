<#
.SYNOPSIS
    Atomic, repo-wide version updater for IgnisDisc Optical Studio.

.DESCRIPTION
    Updates the IgnisDisc product version across every project file, XAML view, installer wizard,
    public website, and WinGet manifest catalog.
    
    Optionally builds release binaries, packages installer & zip artifacts, computes SHA-256 hashes,
    stamps hashes & file sizes into the website and WinGet manifests, and verifies with winget validate.

.PARAMETER NewVersion
    The new semantic/marketing version (e.g. "1.09", "1.10", "2.00").

.PARAMETER FourPartVersion
    Optional 4-part assembly version (e.g. "1.0.9.0", "1.10.0.0").
    Defaults automatically based on NewVersion.

.PARAMETER RebuildArtifacts
    If specified, runs dotnet build, dotnet test, scripts/Build-Installer.ps1, computes SHA-256 hashes,
    stamps them into web/public/index.html and the WinGet manifest, and runs winget validate.

.PARAMETER SkipTests
    Skips the dotnet test suite when RebuildArtifacts is specified.

.PARAMETER DryRun
    Displays the changes that would be made without writing files.

.EXAMPLE
    # Bump version to 1.09 across all files
    pwsh scripts/Update-Version.ps1 -NewVersion "1.09"

    # Bump version and rebuild/stamp installer artifacts
    pwsh scripts/Update-Version.ps1 -NewVersion "1.09" -RebuildArtifacts
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidatePattern('^\d+\.\d+(\.\d+)?$')]
    [string]$NewVersion,

    [string]$FourPartVersion = "",

    [switch]$RebuildArtifacts,
    [switch]$SkipTests,
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot

# ---------------------------------------------------------------------------
# 1. Parse Current & Target Versions
# ---------------------------------------------------------------------------
$PropsPath = Join-Path $RepoRoot "Directory.Build.props"
if (-not (Test-Path $PropsPath)) {
    throw "Directory.Build.props not found at $PropsPath"
}

$PropsContent = Get-Content $PropsPath -Raw
if ($PropsContent -match '<Version>([^<]+)</Version>') {
    $CurrentVersion = $matches[1].Trim()
} else {
    throw "Could not determine current <Version> from $PropsPath"
}

if ($CurrentVersion -eq $NewVersion) {
    Write-Warning "Current version is already '$NewVersion'. Checking for missing version references or rebuilding artifacts..."
}

# No slipstreaming: never rebuild or restamp a version that has already been published.
if (Get-Command gh -ErrorAction SilentlyContinue) {
    & gh release view "v$NewVersion" --repo tether-works/IgnisDisc *> $null
    if ($LASTEXITCODE -eq 0) {
        throw "v$NewVersion is already published on GitHub. Published files are never replaced; choose the next version number instead."
    }
}

# Auto-compute 4-part assembly version if not provided
if ([string]::IsNullOrWhiteSpace($FourPartVersion)) {
    if ($NewVersion -match '^(\d+)\.0?(\d+)$') {
        $FourPartVersion = "$($matches[1]).0.$($matches[2]).0"
    } elseif ($NewVersion -match '^(\d+)\.(\d+)\.(\d+)$') {
        $FourPartVersion = "$($matches[1]).$($matches[2]).$($matches[3]).0"
    } else {
        $FourPartVersion = "$NewVersion.0.0"
    }
}

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "  IgnisDisc Version Updater: v$CurrentVersion -> v$NewVersion ($FourPartVersion)" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

$ModifiedFiles = [System.Collections.Generic.List[string]]::new()

function Set-FileText([string]$Path, [string]$Content, [string]$Description) {
    if ($DryRun) {
        Write-Host "  [DRY-RUN] Would update $Description ($Path)" -ForegroundColor Yellow
    } else {
        Set-Content -Path $Path -Value $Content -Encoding UTF8 -NoNewline
        Write-Host "  [UPDATED] $Description" -ForegroundColor Green
    }
    $ModifiedFiles.Add($Path)
}

# ---------------------------------------------------------------------------
# 2. Directory.Build.props
# ---------------------------------------------------------------------------
$newProps = $PropsContent
$newProps = [regex]::Replace($newProps, '<Version>[^<]+</Version>', "<Version>$NewVersion</Version>")
$newProps = [regex]::Replace($newProps, '<InformationalVersion>[^<]+</InformationalVersion>', "<InformationalVersion>$NewVersion</InformationalVersion>")
$newProps = [regex]::Replace($newProps, '<AssemblyVersion>[^<]+</AssemblyVersion>', "<AssemblyVersion>$FourPartVersion</AssemblyVersion>")
$newProps = [regex]::Replace($newProps, '<FileVersion>[^<]+</FileVersion>', "<FileVersion>$FourPartVersion</FileVersion>")
$newProps = [regex]::Replace($newProps, '<Company>tetherWorks</Company>', '<Company>tetherworks</Company>')
$newProps = [regex]::Replace($newProps, '<Copyright>Copyright \(c\) 2026 tetherWorks</Copyright>', '<Copyright>Copyright (c) 2026 tetherworks</Copyright>')

if ($newProps -ne $PropsContent) {
    Set-FileText $PropsPath $newProps "Directory.Build.props"
}

# ---------------------------------------------------------------------------
# 2b. Legal Notice Banner (src/CdBurner.Core/LegalBanner.cs)
# ---------------------------------------------------------------------------
$LegalBannerPath = Join-Path $RepoRoot "src\CdBurner.Core\LegalBanner.cs"
if (Test-Path $LegalBannerPath) {
    $banner = Get-Content $LegalBannerPath -Raw
    $newBanner = [regex]::Replace($banner, 'IgnisDisc v\d+\.\d+(\.\d+)?', "IgnisDisc v$NewVersion")
    if ($newBanner -ne $banner) {
        Set-FileText $LegalBannerPath $newBanner "src/CdBurner.Core/LegalBanner.cs (LegalBanner)"
    }
}

# ---------------------------------------------------------------------------
# 3. WPF Main Window (Title attribute)
# ---------------------------------------------------------------------------
$MainWindowPath = Join-Path $RepoRoot "src\CdBurner.App\MainWindow.xaml"
if (Test-Path $MainWindowPath) {
    $xaml = Get-Content $MainWindowPath -Raw
    $newXaml = [regex]::Replace($xaml, 'Title="IgnisDisc Optical Studio v[^"]+"', "Title=`"IgnisDisc Optical Studio v$NewVersion`"")
    if ($newXaml -ne $xaml) {
        Set-FileText $MainWindowPath $newXaml "src/CdBurner.App/MainWindow.xaml (Title)"
    }
}

# ---------------------------------------------------------------------------
# 4. Setup Wizard (tools/CdBurner.Setup/Program.cs)
# ---------------------------------------------------------------------------
$SetupProgPath = Join-Path $RepoRoot "tools\CdBurner.Setup\Program.cs"
if (Test-Path $SetupProgPath) {
    $prog = Get-Content $SetupProgPath -Raw
    $newProg = [regex]::Replace($prog, 'public const string AppVersion = "[^"]+";', "public const string AppVersion = `"$NewVersion`";")
    if ($newProg -ne $prog) {
        Set-FileText $SetupProgPath $newProg "tools/CdBurner.Setup/Program.cs (AppVersion)"
    }
}

# ---------------------------------------------------------------------------
# 5. scripts/Publish-ReleaseAndSite.ps1
# ---------------------------------------------------------------------------
$PubScriptPath = Join-Path $RepoRoot "scripts\Publish-ReleaseAndSite.ps1"
if (Test-Path $PubScriptPath) {
    $pub = Get-Content $PubScriptPath -Raw
    $newPub = [regex]::Replace($pub, '\[string\]\$Version = "[^"]+"', "[string]`$Version = `"$NewVersion`"")
    if ($newPub -ne $pub) {
        Set-FileText $PubScriptPath $newPub "scripts/Publish-ReleaseAndSite.ps1 (`$Version default)"
    }
}

# ---------------------------------------------------------------------------
# 6. web/public/app.js
# ---------------------------------------------------------------------------
$AppJsPath = Join-Path $RepoRoot "web\public\app.js"
if (Test-Path $AppJsPath) {
    $appJs = Get-Content $AppJsPath -Raw
    $newAppJs = [regex]::Replace($appJs, 'version:\s*"[^"]*"', "version: `"$NewVersion`"")
    $newAppJs = [regex]::Replace($newAppJs, '/download/v[^/]+/IgnisDisc-Setup\.exe', "/download/v$NewVersion/IgnisDisc-Setup.exe")
    $newAppJs = [regex]::Replace($newAppJs, '/download/v[^/]+/IgnisDisc-Portable-win-x64\.zip', "/download/v$NewVersion/IgnisDisc-Portable-win-x64.zip")
    if ($newAppJs -ne $appJs) {
        Set-FileText $AppJsPath $newAppJs "web/public/app.js (version & download URLs)"
    }
}

# ---------------------------------------------------------------------------
# 7. web/public/index.html
# ---------------------------------------------------------------------------
$PublicHtmlPath = Join-Path $RepoRoot "web\public\index.html"
if (Test-Path $PublicHtmlPath) {
    $html = Get-Content $PublicHtmlPath -Raw
    $newHtml = [regex]::Replace($html, 'id="navVersion">v[^<]+</span>', "id=`"navVersion`">v$NewVersion</span>")
    $newHtml = [regex]::Replace($newHtml, '/download/v[^/]+/IgnisDisc-Setup\.exe', "/download/v$NewVersion/IgnisDisc-Setup.exe")
    $newHtml = [regex]::Replace($newHtml, '/download/v[^/]+/IgnisDisc-Portable-win-x64\.zip', "/download/v$NewVersion/IgnisDisc-Portable-win-x64.zip")
    # Download sub-labels: "v1.08 • 149.2 MB • ..." under the Setup and Portable buttons (anchored on the button ids).
    $newHtml = [regex]::Replace($newHtml, '(id="btnDownloadSetup"[\s\S]*?<span class="dl-sub">)v[^\s•·]+', "`${1}v$NewVersion")
    $newHtml = [regex]::Replace($newHtml, '(id="btnDownloadPortable"[\s\S]*?<span class="dl-sub">)v[^\s•·]+', "`${1}v$NewVersion")
    $newHtml = [regex]::Replace($newHtml, '<strong>IgnisDisc( Optical Studio)? v[^<]+</strong>', "<strong>IgnisDisc v$NewVersion</strong>")
    $newHtml = [regex]::Replace($newHtml, 'IgnisDisc Optical Studio \(v[^)]+\)', "IgnisDisc Optical Studio (v$NewVersion)")
    if ($newHtml -ne $html) {
        Set-FileText $PublicHtmlPath $newHtml "web/public/index.html (version badges & URLs)"
    }
}

# ---------------------------------------------------------------------------
# 8. web/tetherworks/index.html
# ---------------------------------------------------------------------------
$TetherHtmlPath = Join-Path $RepoRoot "web\tetherworks\index.html"
if (Test-Path $TetherHtmlPath) {
    $thtml = Get-Content $TetherHtmlPath -Raw
    $newThtml = [regex]::Replace($thtml, 'IgnisDisc Optical Studio \(v[^)]+\)', "IgnisDisc Optical Studio (v$NewVersion)")
    $newThtml = [regex]::Replace($newThtml, '(id="ignisRev">)v[^<]+(</code>)', "`${1}v$NewVersion`${2}")
    if ($newThtml -ne $thtml) {
        Set-FileText $TetherHtmlPath $newThtml "web/tetherworks/index.html"
    }
}

# ---------------------------------------------------------------------------
# 9. WinGet Manifests (packaging/winget/manifests/t/TetherWorks/IgnisDisc/)
# ---------------------------------------------------------------------------
$WingetBase = Join-Path $RepoRoot "packaging\winget\manifests\t\TetherWorks\IgnisDisc"
if (Test-Path $WingetBase) {
    $NewWingetDir = Join-Path $WingetBase $NewVersion
    $SourceWingetDir = Join-Path $WingetBase $CurrentVersion

    if (-not (Test-Path $SourceWingetDir)) {
        # Fallback to most recent version directory
        $latest = Get-ChildItem -Path $WingetBase -Directory | Sort-Object Name -Descending | Select-Object -First 1
        if ($latest) { $SourceWingetDir = $latest.FullName }
    }

    if (-not $DryRun) {
        if (-not (Test-Path $NewWingetDir)) {
            New-Item -ItemType Directory -Path $NewWingetDir -Force | Out-Null
        }

        # 1. Version manifest: TetherWorks.IgnisDisc.yaml
        $verYaml = @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.version.1.6.0.schema.json

PackageIdentifier: TetherWorks.IgnisDisc
PackageVersion: "$NewVersion"
DefaultLocale: en-US
ManifestType: version
ManifestVersion: 1.6.0
"@
        Set-Content -Path (Join-Path $NewWingetDir "TetherWorks.IgnisDisc.yaml") -Value $verYaml -Encoding UTF8

        # 2. Locale manifest: TetherWorks.IgnisDisc.locale.en-US.yaml
        $srcLocale = Join-Path $SourceWingetDir "TetherWorks.IgnisDisc.locale.en-US.yaml"
        if (Test-Path $srcLocale) {
            $loc = Get-Content $srcLocale -Raw
            $loc = [regex]::Replace($loc, 'PackageVersion:\s*"[^"]*"', "PackageVersion: `"$NewVersion`"")
            $loc = [regex]::Replace($loc, 'ReleaseNotesUrl:\s*https://github\.com/tether-works/IgnisDisc/releases/tag/v[^/\s\r\n]+', "ReleaseNotesUrl: https://github.com/tether-works/IgnisDisc/releases/tag/v$NewVersion")
            $loc = [regex]::Replace($loc, 'Publisher:\s*tetherWorks', "Publisher: tetherworks")
            $loc = [regex]::Replace($loc, 'License:\s*MIT', "License: GPL-3.0-or-later")
            # Pin the licence link to the release's own tag, so the manifest never points at a newer licence text.
            $loc = [regex]::Replace($loc, 'LicenseUrl:\s*https://github\.com/tether-works/IgnisDisc/blob/[^/\s]+/LICENSE', "LicenseUrl: https://github.com/tether-works/IgnisDisc/blob/v$NewVersion/LICENSE")
            Set-Content -Path (Join-Path $NewWingetDir "TetherWorks.IgnisDisc.locale.en-US.yaml") -Value $loc -Encoding UTF8
        }

        # 3. Installer manifest: TetherWorks.IgnisDisc.installer.yaml
        $srcInst = Join-Path $SourceWingetDir "TetherWorks.IgnisDisc.installer.yaml"
        if (Test-Path $srcInst) {
            $inst = Get-Content $srcInst -Raw
            $inst = [regex]::Replace($inst, 'PackageVersion:\s*"[^"]*"', "PackageVersion: `"$NewVersion`"")
            $inst = [regex]::Replace($inst, 'DisplayVersion:\s*"[^"]*"', "DisplayVersion: `"$NewVersion`"")
            $inst = [regex]::Replace($inst, 'ReleaseDate:\s*\d{4}-\d{2}-\d{2}', "ReleaseDate: $(Get-Date -Format 'yyyy-MM-dd')")
            $inst = [regex]::Replace($inst, '/download/v[^/]+/IgnisDisc-Setup\.exe', "/download/v$NewVersion/IgnisDisc-Setup.exe")
            Set-Content -Path (Join-Path $NewWingetDir "TetherWorks.IgnisDisc.installer.yaml") -Value $inst -Encoding UTF8
        }

        Write-Host "  [CREATED] packaging/winget/manifests/t/TetherWorks/IgnisDisc/$NewVersion" -ForegroundColor Green
        $ModifiedFiles.Add($NewWingetDir)
    } else {
        Write-Host "  [DRY-RUN] Would create packaging/winget/manifests/t/TetherWorks/IgnisDisc/$NewVersion" -ForegroundColor Yellow
        Write-Host "  [DRY-RUN] Would generate TetherWorks.IgnisDisc.yaml" -ForegroundColor Yellow
        Write-Host "  [DRY-RUN] Would generate TetherWorks.IgnisDisc.locale.en-US.yaml" -ForegroundColor Yellow
        Write-Host "  [DRY-RUN] Would generate TetherWorks.IgnisDisc.installer.yaml" -ForegroundColor Yellow
    }
}

# ---------------------------------------------------------------------------
# 10. Support Matrix Synchronization (README.md & web/public/index.html)
# ---------------------------------------------------------------------------
$SyncScript = Join-Path $PSScriptRoot "Sync-SupportTable.ps1"
if (Test-Path $SyncScript) {
    if ($DryRun) {
        & pwsh -ExecutionPolicy Bypass -File $SyncScript -DryRun
    } else {
        & pwsh -ExecutionPolicy Bypass -File $SyncScript
    }
}

# ---------------------------------------------------------------------------
# 11. Optional: Rebuild Artifacts, Compute Checksums & Update Site
# ---------------------------------------------------------------------------
if ($RebuildArtifacts -and -not $DryRun) {
    Write-Host "`n==> Running Mandatory Release Verification Gate..." -ForegroundColor Cyan
    & dotnet build "$RepoRoot\CdBurner.sln" --configuration Release
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE" }

    if (-not $SkipTests) {
        Write-Host "==> Running Automated Test Suite..." -ForegroundColor Cyan
        & dotnet test "$RepoRoot\CdBurner.sln" --configuration Release --no-build
        if ($LASTEXITCODE -ne 0) { throw "dotnet test failed with exit code $LASTEXITCODE" }
    }

    Write-Host "==> Packaging Standalone Installer & Portable ZIP via Build-Installer.ps1..." -ForegroundColor Cyan
    & pwsh -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "Build-Installer.ps1")
    if ($LASTEXITCODE -ne 0) { throw "Build-Installer.ps1 failed with exit code $LASTEXITCODE" }

    $SetupExe = Join-Path $RepoRoot "artifacts\installer\IgnisDisc-Setup.exe"
    $PortableZip = Join-Path $RepoRoot "artifacts\installer\IgnisDisc-Portable-win-x64.zip"

    if ((Test-Path $SetupExe) -and (Test-Path $PortableZip)) {
        $setupItem = Get-Item $SetupExe
        $setupHash = (Get-FileHash $SetupExe -Algorithm SHA256).Hash
        $setupMb = [math]::Round($setupItem.Length / 1MB, 1)

        $portableItem = Get-Item $PortableZip
        $portableHash = (Get-FileHash $PortableZip -Algorithm SHA256).Hash
        $portableMb = [math]::Round($portableItem.Length / 1MB, 1)

        Write-Host "`n==> Artifact Checksums & Sizes:" -ForegroundColor Green
        Write-Host "    IgnisDisc-Setup.exe            : $setupMb MB | $setupHash"
        Write-Host "    IgnisDisc-Portable-win-x64.zip : $portableMb MB | $portableHash"

        # Update web/public/index.html with new checksums & sizes
        $html = Get-Content $PublicHtmlPath -Raw
        # Each hash appears twice: in <code id="hash..."> and in the Copy button's data-copy on the next line.
        $html = [regex]::Replace($html, '(<code id="hashSetup">)[A-Fa-f0-9]{64}(</code>\s*<button[^>]*data-copy=")[A-Fa-f0-9]{64}', "`${1}$setupHash`${2}$setupHash")
        $html = [regex]::Replace($html, '(<code id="hashPortable">)[A-Fa-f0-9]{64}(</code>\s*<button[^>]*data-copy=")[A-Fa-f0-9]{64}', "`${1}$portableHash`${2}$portableHash")
        $html = [regex]::Replace($html, '(id="btnDownloadSetup"[\s\S]*?<span class="dl-sub">v[^\s•·]+ [•·] )\d+(\.\d+)? MB', "`${1}$setupMb MB")
        $html = [regex]::Replace($html, '(id="btnDownloadPortable"[\s\S]*?<span class="dl-sub">v[^\s•·]+ [•·] )\d+(\.\d+)? MB', "`${1}$portableMb MB")
        $html = [regex]::Replace($html, '(Two-stage )\d+(\.\d+)? MB( installer)', "`${1}$setupMb MB`${3}")
        Set-FileText $PublicHtmlPath $html "web/public/index.html (Stamped SHA-256 and MB sizes)"

        # Update WinGet installer manifest with hash
        $instYamlPath = Join-Path $WingetBase "$NewVersion\TetherWorks.IgnisDisc.installer.yaml"
        if (Test-Path $instYamlPath) {
            $inst = Get-Content $instYamlPath -Raw
            $inst = [regex]::Replace($inst, 'InstallerSha256:\s*[A-Fa-f0-9]{64}', "InstallerSha256: $setupHash")
            Set-Content -Path $instYamlPath -Value $inst -Encoding UTF8
            Write-Host "  [UPDATED] $instYamlPath (InstallerSha256)" -ForegroundColor Green

            # Validate WinGet manifest
            Write-Host "==> Running WinGet Manifest Validator..." -ForegroundColor Cyan
            & winget validate --manifest (Join-Path $WingetBase $NewVersion)
        }
    }
}

Write-Host "`n==========================================================" -ForegroundColor Green
Write-Host "  Successfully updated IgnisDisc to v$NewVersion across $($ModifiedFiles.Count) files/locations!" -ForegroundColor Green
Write-Host "==========================================================" -ForegroundColor Green
Write-Host "`nRecommended next steps:"
Write-Host "  1. Review changes with: git diff"
Write-Host "  2. Commit changes:      git commit -am `"chore: bump version to v$NewVersion`""
Write-Host "  3. Create git tag:      git tag v$NewVersion"
Write-Host "  4. Upload release:      gh release create v$NewVersion artifacts/installer/IgnisDisc-Setup.exe artifacts/installer/IgnisDisc-Portable-win-x64.zip --title `"IgnisDisc Optical Studio v$NewVersion`""
Write-Host "  5. Deploy websites:     npx --yes firebase-tools deploy --only hosting"
Write-Host "  6. Submit WinGet PR:    git push your-fork && gh pr create --repo microsoft/winget-pkgs`n"
