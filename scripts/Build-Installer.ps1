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

Write-Host "=== Step 2: Packaging Portable Archive & Setup Payload ===" -ForegroundColor Cyan
if (Test-Path $PortableZipPath) { Remove-Item $PortableZipPath -Force }
if (Test-Path $PayloadZipPath) { Remove-Item $PayloadZipPath -Force }

Compress-Archive -Path "$PublishDir\*" -DestinationPath $PortableZipPath -CompressionLevel Optimal -Force
Copy-Item -Path $PortableZipPath -Destination $PayloadZipPath -Force

Write-Host "=== Step 3: Building Standalone Single-File Setup Wizard (IgnisDisc-Setup.exe) ===" -ForegroundColor Cyan
try {
    dotnet publish (Join-Path $SetupProjectDir "CdBurner.Setup.csproj") `
        -c $Configuration `
        -r $Runtime `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -o $InstallerDir
    if ($LASTEXITCODE -ne 0) { throw "Failed to publish IgnisDisc-Setup" }
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
