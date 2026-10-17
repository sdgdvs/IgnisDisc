param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot
$PublishDir = Join-Path $RepoRoot "artifacts\publish\$Runtime"
$InstallerDir = Join-Path $RepoRoot "artifacts\installer"
$SetupProjectDir = Join-Path $RepoRoot "tools\CdBurner.Setup"
$PayloadZipPath = Join-Path $SetupProjectDir "payload.zip"
$PortableZipPath = Join-Path $InstallerDir "CdBurner-Portable-$Runtime.zip"

Write-Host "=== Step 1: Publishing Self-Contained CdBurner.App & CdBurner.Cli ($Runtime) ===" -ForegroundColor Cyan
if (Test-Path $PublishDir) {
    Remove-Item -Path $PublishDir -Recurse -Force
}
New-Item -ItemType Directory -Path $PublishDir -Force | Out-Null
New-Item -ItemType Directory -Path $InstallerDir -Force | Out-Null

dotnet publish (Join-Path $RepoRoot "src\CdBurner.App\CdBurner.App.csproj") `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=false `
    -o $PublishDir
if ($LASTEXITCODE -ne 0) { throw "Failed to publish CdBurner.App" }

dotnet publish (Join-Path $RepoRoot "src\CdBurner.Cli\CdBurner.Cli.csproj") `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=false `
    -o $PublishDir
if ($LASTEXITCODE -ne 0) { throw "Failed to publish CdBurner.Cli" }

Write-Host "=== Step 2: Packaging Portable Archive & Setup Payload ===" -ForegroundColor Cyan
if (Test-Path $PortableZipPath) { Remove-Item $PortableZipPath -Force }
if (Test-Path $PayloadZipPath) { Remove-Item $PayloadZipPath -Force }

Compress-Archive -Path "$PublishDir\*" -DestinationPath $PortableZipPath -CompressionLevel Optimal -Force
Copy-Item -Path $PortableZipPath -Destination $PayloadZipPath -Force

Write-Host "=== Step 3: Building Standalone Single-File Setup Wizard (CdBurner-Setup.exe) ===" -ForegroundColor Cyan
try {
    dotnet publish (Join-Path $SetupProjectDir "CdBurner.Setup.csproj") `
        -c $Configuration `
        -r $Runtime `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -o $InstallerDir
    if ($LASTEXITCODE -ne 0) { throw "Failed to publish CdBurner.Setup" }
}
finally {
    if (Test-Path $PayloadZipPath) {
        Remove-Item $PayloadZipPath -Force
    }
}

$SetupExe = Join-Path $InstallerDir "CdBurner-Setup.exe"
if (-not (Test-Path $SetupExe)) {
    throw "Installer executable was not produced at $SetupExe"
}

$SetupInfo = Get-Item $SetupExe
$ZipInfo = Get-Item $PortableZipPath
Write-Host ""
Write-Host "=== Installer Build Succeeded ===" -ForegroundColor Green
Write-Host ("Standalone Installer : {0} ({1:N1} MB)" -f $SetupInfo.FullName, ($SetupInfo.Length / 1MB))
Write-Host ("Portable Zip Archive : {0} ({1:N1} MB)" -f $ZipInfo.FullName, ($ZipInfo.Length / 1MB))
Write-Host ("Self-Contained App   : {0}" -f (Join-Path $PublishDir "CdBurner.App.exe"))
