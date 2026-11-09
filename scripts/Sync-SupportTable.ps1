<#
.SYNOPSIS
    Single source of truth synchronizer for the IgnisDisc Support Matrix.

.DESCRIPTION
    Reads canonical feature rows from docs/support-matrix.json and regenerates
    the support table blocks in:
      - README.md (Markdown table between <!-- SUPPORT-TABLE-START --> and <!-- SUPPORT-TABLE-END -->)
      - web/public/index.html (HTML <tbody> between <!-- SUPPORT-TABLE-START --> and <!-- SUPPORT-TABLE-END -->)

.PARAMETER DryRun
    Displays the changes that would be made without writing files.

.PARAMETER CheckOnly
    Checks whether README.md and web/public/index.html are in sync with docs/support-matrix.json.
    Exits with code 0 if in sync, or non-zero if out of sync.

.EXAMPLE
    pwsh scripts/Sync-SupportTable.ps1
    pwsh scripts/Sync-SupportTable.ps1 -CheckOnly
    pwsh scripts/Sync-SupportTable.ps1 -DryRun
#>
[CmdletBinding()]
param(
    [switch]$DryRun,
    [switch]$CheckOnly
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot

$MatrixPath = Join-Path $RepoRoot "docs\support-matrix.json"
$ReadmePath = Join-Path $RepoRoot "README.md"
$HtmlPath = Join-Path $RepoRoot "web\public\index.html"

if (-not (Test-Path $MatrixPath)) {
    throw "Support matrix canonical data not found at $MatrixPath"
}
if (-not (Test-Path $ReadmePath)) {
    throw "README.md not found at $ReadmePath"
}
if (-not (Test-Path $HtmlPath)) {
    throw "web/public/index.html not found at $HtmlPath"
}

$matrix = Get-Content -Path $MatrixPath -Raw -Encoding UTF8 | ConvertFrom-Json

function Get-Newline([string]$text) {
    if ($text.Contains("`r`n")) { return "`r`n" }
    return "`n"
}

# ---------------------------------------------------------------------------
# 1. Generate Markdown Table for README.md
# ---------------------------------------------------------------------------
$readmeContent = Get-Content -Path $ReadmePath -Raw -Encoding UTF8
$readmeNl = Get-Newline $readmeContent

$mdLines = [System.Collections.Generic.List[string]]::new()
$mdLines.Add("| Feature | Physical drives | Virtual drive | Notes |")
$mdLines.Add("| --- | :---: | :---: | --- |")
foreach ($row in $matrix) {
    $note = if ([string]::IsNullOrEmpty($row.notesMarkdown)) { "" } else { "$($row.notesMarkdown) " }
    $mdLines.Add("| $($row.featureMarkdown) | $($row.physical) | $($row.virtual) | $note|")
}
$newMdBlock = ($mdLines -join $readmeNl)

$readmeMarkerPattern = '<!-- SUPPORT-TABLE-START -->[\s\S]*?<!-- SUPPORT-TABLE-END -->'
if ($readmeContent -notmatch $readmeMarkerPattern) {
    throw "Could not find <!-- SUPPORT-TABLE-START --> ... <!-- SUPPORT-TABLE-END --> in $ReadmePath"
}
$replacementReadme = "<!-- SUPPORT-TABLE-START -->$readmeNl$newMdBlock$readmeNl<!-- SUPPORT-TABLE-END -->"
$updatedReadme = [regex]::Replace($readmeContent, $readmeMarkerPattern, [System.Text.RegularExpressions.MatchEvaluator]{ param($m) $replacementReadme })

# ---------------------------------------------------------------------------
# 2. Generate HTML <tbody> Block for web/public/index.html
# ---------------------------------------------------------------------------
$htmlContent = Get-Content -Path $HtmlPath -Raw -Encoding UTF8
$htmlNl = Get-Newline $htmlContent

$htmlLines = [System.Collections.Generic.List[string]]::new()
$htmlLines.Add("              <tbody>")
foreach ($row in $matrix) {
    $htmlLines.Add("                <tr><td>$($row.featureHtml)</td><td>$($row.physical)</td><td>$($row.notesHtml)</td></tr>")
}
$htmlLines.Add("              </tbody>")
$newHtmlBlock = ($htmlLines -join $htmlNl)

$htmlMarkerPattern = '<!-- SUPPORT-TABLE-START -->[\s\S]*?<!-- SUPPORT-TABLE-END -->'
if ($htmlContent -notmatch $htmlMarkerPattern) {
    throw "Could not find <!-- SUPPORT-TABLE-START --> ... <!-- SUPPORT-TABLE-END --> in $HtmlPath"
}
$replacementHtml = "<!-- SUPPORT-TABLE-START -->$htmlNl$newHtmlBlock$htmlNl              <!-- SUPPORT-TABLE-END -->"
$updatedHtml = [regex]::Replace($htmlContent, $htmlMarkerPattern, [System.Text.RegularExpressions.MatchEvaluator]{ param($m) $replacementHtml })

# ---------------------------------------------------------------------------
# 3. Apply / Verify
# ---------------------------------------------------------------------------
$readmeDiffers = ($updatedReadme -ne $readmeContent)
$htmlDiffers = ($updatedHtml -ne $htmlContent)

if ($CheckOnly) {
    if ($readmeDiffers -or $htmlDiffers) {
        if ($readmeDiffers) {
            Write-Error "README.md support table is out of sync with docs/support-matrix.json"
        }
        if ($htmlDiffers) {
            Write-Error "web/public/index.html support table is out of sync with docs/support-matrix.json"
        }
        exit 1
    }
    Write-Host "Support tables in README.md and web/public/index.html are in sync with docs/support-matrix.json." -ForegroundColor Green
    return
}

if ($DryRun) {
    if ($readmeDiffers) {
        Write-Host "  [DRY-RUN] Would update support table in README.md" -ForegroundColor Yellow
    } else {
        Write-Host "  [IN SYNC] README.md support table is already up-to-date" -ForegroundColor Green
    }
    if ($htmlDiffers) {
        Write-Host "  [DRY-RUN] Would update support table in web/public/index.html" -ForegroundColor Yellow
    } else {
        Write-Host "  [IN SYNC] web/public/index.html support table is already up-to-date" -ForegroundColor Green
    }
    return
}

if ($readmeDiffers) {
    Set-Content -Path $ReadmePath -Value $updatedReadme -Encoding UTF8 -NoNewline
    Write-Host "  [UPDATED] $ReadmePath (Support table regenerated from docs/support-matrix.json)" -ForegroundColor Green
} else {
    Write-Host "  [IN SYNC] $ReadmePath (Already matches docs/support-matrix.json)" -ForegroundColor Green
}

if ($htmlDiffers) {
    Set-Content -Path $HtmlPath -Value $updatedHtml -Encoding UTF8 -NoNewline
    Write-Host "  [UPDATED] $HtmlPath (Support table regenerated from docs/support-matrix.json)" -ForegroundColor Green
} else {
    Write-Host "  [IN SYNC] $HtmlPath (Already matches docs/support-matrix.json)" -ForegroundColor Green
}
