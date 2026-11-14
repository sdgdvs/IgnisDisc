<#
.SYNOPSIS
    Reproduces the DiscImageStudio golden outputs recorded in
    tests/CdBurner.Lithography.Tests/Fixtures/UpstreamGoldens/upstream-goldens.json.

.DESCRIPTION
    Clones DiscImageStudio into a scratch folder outside the repository (never committed), checks out the commit
    pinned in the fixtures JSON, builds its command-line tool and a small calibration-target host, runs every case
    of the JSON on the fixture PNGs and compares the SHA-256 of each output with the recorded value.

    Every case runs -Runs times (default 2) into separate folders; all runs must give identical bytes.

    Calibration targets are only produced by upstream's desktop app, so the script writes a short console host
    (below, in $HarnessProgram) into the scratch folder. It calls the same public upstream methods the app's
    calibration burn calls and writes the stream to a file.

    Sidecar JSON files written by upstream contain absolute paths and the generation time, so they are not hashed.

.PARAMETER WorkDir
    Scratch folder for the clone, the builds and the run outputs. Must be outside the repository.

.PARAMETER Update
    Write the hashes, sizes and command lines of this run into the fixtures JSON instead of comparing.
    Use only when the pinned commit or the case list changes on purpose.

.PARAMETER CreateInputs
    Rewrite the fixture PNGs from their pattern number before running (they are generated, not drawn).

.PARAMETER Runs
    How many times every case runs. All runs must match each other.

.EXAMPLE
    pwsh -NoProfile -File scripts/Make-LithographyGoldens.ps1
    pwsh -NoProfile -File scripts/Make-LithographyGoldens.ps1 -WorkDir D:\scratch\goldens
#>
[CmdletBinding()]
param(
    [string]$WorkDir = (Join-Path ([System.IO.Path]::GetTempPath()) 'ignisdisc-lithography-goldens'),
    [switch]$Update,
    [switch]$CreateInputs,
    [ValidateRange(1, 10)]
    [int]$Runs = 2
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$RepoRoot = Split-Path -Parent $PSScriptRoot
$FixtureDir = Join-Path $RepoRoot 'tests\CdBurner.Lithography.Tests\Fixtures\UpstreamGoldens'
$JsonPath = Join-Path $FixtureDir 'upstream-goldens.json'

$WorkDir = [System.IO.Path]::GetFullPath($WorkDir)
$repoFull = [System.IO.Path]::GetFullPath($RepoRoot).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
if ($WorkDir.StartsWith($repoFull, [System.StringComparison]::OrdinalIgnoreCase) -or
    $WorkDir.TrimEnd('\', '/') -eq $repoFull.TrimEnd('\', '/')) {
    throw "WorkDir '$WorkDir' is inside the repository. Upstream code must stay out of it; choose a folder elsewhere."
}
if ($WorkDir.Contains(' ')) {
    throw "WorkDir '$WorkDir' contains a space; command lines are recorded unquoted, choose a path without spaces."
}

$config = Get-Content -LiteralPath $JsonPath -Raw -Encoding UTF8 | ConvertFrom-Json
$pinnedCommit = [string]$config.upstream.commit
if ($pinnedCommit -notmatch '^[0-9a-f]{40}$') { throw "Pinned commit '$pinnedCommit' is not a full SHA-1." }

function Invoke-Checked {
    # Runs a native program and stops on a non-zero exit code (PowerShell does not by itself).
    param([string]$FilePath, [string[]]$Arguments, [string]$What)
    $output = & $FilePath @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        $output | ForEach-Object { Write-Host "  $_" }
        throw "$What failed with exit code $LASTEXITCODE."
    }
    return $output
}

function Get-Sha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

# ---------------------------------------------------------------------------
# Fixture PNGs: 64x64 RGBA, written byte by byte (stored deflate blocks) so the
# file is identical on every machine and .NET version.
# ---------------------------------------------------------------------------
function Get-Crc32([byte[]]$Data) {
    [uint32]$crc = [uint32]::MaxValue
    foreach ($b in $Data) {
        $crc = $crc -bxor $b
        for ($k = 0; $k -lt 8; $k++) {
            if ($crc -band 1) { $crc = ($crc -shr 1) -bxor 0xEDB88320u } else { $crc = $crc -shr 1 }
        }
    }
    return [uint32]($crc -bxor [uint32]::MaxValue)
}

function Write-UInt32BigEndian([System.IO.Stream]$Stream, [uint32]$Value) {
    $Stream.WriteByte(($Value -shr 24) -band 0xFF); $Stream.WriteByte(($Value -shr 16) -band 0xFF)
    $Stream.WriteByte(($Value -shr 8) -band 0xFF); $Stream.WriteByte($Value -band 0xFF)
}

function Write-PngChunk([System.IO.Stream]$Stream, [string]$Type, [byte[]]$Data) {
    Write-UInt32BigEndian $Stream ([uint32]$Data.Length)
    [byte[]]$typed = [System.Text.Encoding]::ASCII.GetBytes($Type) + $Data
    $Stream.Write($typed, 0, $typed.Length)
    Write-UInt32BigEndian $Stream (Get-Crc32 $typed)
}

function Get-PatternLevel([int]$Pattern, [int]$X, [int]$Y) {
    if ($Pattern -eq 0) {
        # Quadrants: black, white, horizontal ramp, 8-pixel checkerboard.
        if ($Y -lt 32) { if ($X -lt 32) { return 0 } else { return 255 } }
        if ($X -lt 32) { return [int][math]::Floor($X * 255 / 31) }
        if (((($X -shr 3) + ($Y -shr 3)) % 2) -eq 0) { return 0 } else { return 255 }
    }
    if ($Pattern -eq 1) {
        # Concentric rings 4 pixels wide around the image centre (integer distance test).
        $dx = 2 * $X - 63; $dy = 2 * $Y - 63
        $ring = [int][math]::Floor([math]::Sqrt($dx * $dx + $dy * $dy) / 8)
        if (($ring % 2) -eq 0) { return 0 } else { return 255 }
    }
    throw "Unknown fixture pattern $Pattern."
}

function New-FixturePng([string]$Path, [int]$Pattern) {
    $size = 64
    $raw = [System.IO.MemoryStream]::new()
    for ($y = 0; $y -lt $size; $y++) {
        $raw.WriteByte(0)   # PNG filter type None
        for ($x = 0; $x -lt $size; $x++) {
            $v = Get-PatternLevel $Pattern $x $y
            $raw.WriteByte($v); $raw.WriteByte($v); $raw.WriteByte($v); $raw.WriteByte(255)
        }
    }
    [byte[]]$data = $raw.ToArray()
    if ($data.Length -gt 65535) { throw 'Fixture too large for one stored deflate block.' }
    $zlib = [System.IO.MemoryStream]::new()
    $zlib.WriteByte(0x78); $zlib.WriteByte(0x01)           # zlib header, no preset dictionary
    $zlib.WriteByte(0x01)                                   # final stored block
    $len = $data.Length; $nlen = (-bnot $len) -band 0xFFFF
    $zlib.WriteByte($len -band 0xFF); $zlib.WriteByte(($len -shr 8) -band 0xFF)
    $zlib.WriteByte($nlen -band 0xFF); $zlib.WriteByte(($nlen -shr 8) -band 0xFF)
    $zlib.Write($data, 0, $data.Length)
    [uint32]$a = 1; [uint32]$b = 0
    foreach ($c in $data) { $a = ($a + $c) % 65521; $b = ($b + $a) % 65521 }
    Write-UInt32BigEndian $zlib ([uint32](($b -shl 16) -bor $a))   # Adler-32

    $header = [System.IO.MemoryStream]::new()
    Write-UInt32BigEndian $header ([uint32]$size); Write-UInt32BigEndian $header ([uint32]$size)
    $header.Write([byte[]](8, 6, 0, 0, 0), 0, 5)            # 8-bit RGBA, deflate, adaptive filter, no interlace

    $png = [System.IO.MemoryStream]::new()
    $png.Write([byte[]](0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A), 0, 8)
    Write-PngChunk $png 'IHDR' $header.ToArray()
    Write-PngChunk $png 'IDAT' $zlib.ToArray()
    Write-PngChunk $png 'IEND' ([byte[]]@())
    [System.IO.File]::WriteAllBytes($Path, $png.ToArray())
}

Write-Host "Fixture inputs in $FixtureDir"
foreach ($fixture in $config.inputs) {
    $path = Join-Path $FixtureDir $fixture.file
    if ($CreateInputs -or -not (Test-Path -LiteralPath $path)) {
        New-FixturePng -Path $path -Pattern ([int]$fixture.pattern)
        Write-Host "  wrote $($fixture.file)"
    }
    $hash = Get-Sha256 $path
    if ($Update) { $fixture.sha256 = $hash }
    elseif ($hash -ne $fixture.sha256) {
        throw "Fixture $($fixture.file) has SHA-256 $hash, expected $($fixture.sha256)."
    }
}

# ---------------------------------------------------------------------------
# Upstream clone at the pinned commit (scratch folder only).
# ---------------------------------------------------------------------------
New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null
$upstreamDir = Join-Path $WorkDir 'upstream'
if (-not (Test-Path -LiteralPath (Join-Path $upstreamDir '.git'))) {
    Write-Host "Cloning $($config.upstream.repository) into $upstreamDir"
    Invoke-Checked git @('clone', '--quiet', '--no-checkout', [string]$config.upstream.repository, $upstreamDir) 'git clone' | Out-Null
}
$have = & git -C $upstreamDir cat-file -t $pinnedCommit 2>$null
if ($LASTEXITCODE -ne 0 -or $have -ne 'commit') {
    Invoke-Checked git @('-C', $upstreamDir, 'fetch', '--quiet', 'origin') 'git fetch' | Out-Null
}
Invoke-Checked git @('-C', $upstreamDir, '-c', 'advice.detachedHead=false', 'checkout', '--quiet', '--force', $pinnedCommit) 'git checkout' | Out-Null
$head = (Invoke-Checked git @('-C', $upstreamDir, 'rev-parse', 'HEAD') 'git rev-parse' | Select-Object -First 1).ToString().Trim()
if ($head -ne $pinnedCommit) { throw "Upstream HEAD is $head, expected $pinnedCommit." }
$dirty = Invoke-Checked git @('-C', $upstreamDir, 'status', '--porcelain', '--untracked-files=no') 'git status'
if ($dirty) { throw "Upstream checkout in $upstreamDir has local changes; delete the folder and run again." }
Write-Host "Upstream at $head"

# global.json in the upstream tree picks the SDK, so ask from inside it.
Push-Location $upstreamDir
try { $sdk = (Invoke-Checked dotnet @('--version') 'dotnet --version' | Select-Object -First 1).ToString().Trim() }
finally { Pop-Location }

$cliDir = Join-Path $WorkDir 'cli'
Write-Host "Building upstream CLI (.NET SDK $sdk)"
Invoke-Checked dotnet @('build', (Join-Path $upstreamDir 'src\DiscImageStudio.Cli\DiscImageStudio.Cli.csproj'),
    '-c', 'Release', '-o', $cliDir, '-nologo', '-v', 'quiet') 'Upstream CLI build' | Out-Null
$cliExe = Join-Path $cliDir 'disc-image-studio.exe'
if (-not (Test-Path -LiteralPath $cliExe)) { throw "Upstream CLI not found at $cliExe." }

# ---------------------------------------------------------------------------
# Calibration-target host. Mirrors the upstream app's calibration burn wiring:
# CalibrationTarget.Create -> CdTrackGenerator.GeneratePatternToStream (little-endian),
# DvdStreamingGenerator.GeneratePattern, or BluRayStreamingGenerator with a
# BluRayPatternRangeMapping over the target's sampler (Y flipped for schema 14+).
# ---------------------------------------------------------------------------
$HarnessProject = @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <AssemblyName>golden-harness</AssemblyName>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <InvariantGlobalization>true</InvariantGlobalization>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="$(UpstreamRoot)/src/DiscImageStudio.Core/DiscImageStudio.Core.csproj" />
    <ProjectReference Include="$(UpstreamRoot)/src/DiscImageStudio.Cd/DiscImageStudio.Cd.csproj" />
    <ProjectReference Include="$(UpstreamRoot)/src/DiscImageStudio.Dvd/DiscImageStudio.Dvd.csproj" />
    <ProjectReference Include="$(UpstreamRoot)/src/DiscImageStudio.BluRay/DiscImageStudio.BluRay.csproj" />
  </ItemGroup>
</Project>
'@

$HarnessProgram = @'
using System.Globalization;
using DiscImageStudio.BluRay;
using DiscImageStudio.Cd;
using DiscImageStudio.Core.Calibration;
using DiscImageStudio.Dvd;

const string Usage = "usage: calibration-target <cd|dvd|bd> <artwork> <r0-mm> <r1-mm> <sectors> <output>";
if (args.Length != 7 || args[0] != "calibration-target")
{
    Console.Error.WriteLine(Usage);
    return 2;
}
CalibrationDiscKind kind = args[1] switch
{
    "cd" => CalibrationDiscKind.Cd,
    "dvd" => CalibrationDiscKind.Dvd,
    "bd" => CalibrationDiscKind.BluRay,
    _ => throw new ArgumentException($"unknown medium '{args[1]}'"),
};
CalibrationArtwork artwork = Enum.Parse<CalibrationArtwork>(args[2], ignoreCase: false);
double r0 = double.Parse(args[3], NumberStyles.Float, CultureInfo.InvariantCulture);
double r1 = double.Parse(args[4], NumberStyles.Float, CultureInfo.InvariantCulture);
long sectors = long.Parse(args[5], NumberStyles.None, CultureInfo.InvariantCulture);
string output = Path.GetFullPath(args[6]);

double density = kind switch
{
    CalibrationDiscKind.Cd => CdDiscParameters.StandardLinearVelocityMmPerSecond,
    CalibrationDiscKind.Dvd => DvdStreamingOptions.StandardChannelBitLengthNm,
    _ => CalibrationParameters.BluRayChannelBitLengthNm,
};
CalibrationTarget target = CalibrationTarget.Create(new CalibrationParameters(kind, r0, r1, sectors, density),
    artwork: artwork);

using (FileStream file = new(output, FileMode.CreateNew, FileAccess.Write, FileShare.None))
{
    switch (kind)
    {
        case CalibrationDiscKind.Cd:
            CdTrackGenerator.GeneratePatternToStream(target, file, interleave: true,
                audioByteOrder: CdAudioByteOrder.LittleEndian);
            break;
        case CalibrationDiscKind.Dvd:
            DvdStreamingOptions dvd = new(checked((uint)sectors), r0, r1, density, 0,
                ReadCompatibilityMode: target.EffectiveDvdReadCompatibilityMode);
            dvd.Validate();
            DvdStreamingGenerator.GeneratePattern(target, file, dvd);
            break;
        default:
            // The app uses other mappings for address-band targets and the schema-14 reference
            // raster; this host only covers the analytic sampler path.
            if (target.HasWideAddressBands || target.SchemaVersion < 15)
                throw new ArgumentException("BD targets here must be analytic artwork of schema 15 or later.");
            BluRayDiscParameters disc = new(checked((uint)sectors), 1, r0, r1);
            disc.Validate();
            BluRayPatternRangeMapping mapping = new(disc, (x, y) => target.Sample(x, -y), 0, disc.OutputSectors,
                invertMapping: false, allowParallelSampling: true);
            BluRayStreamingGenerator.GenerateToStream(file, new BluRayStreamingOptions(disc, 0, disc.OutputSectors),
                mapping);
            break;
    }
}
Console.WriteLine($"calibration target {args[1]} {artwork} schema {target.SchemaVersion} -> {output}");
return 0;
'@

$harnessSrc = Join-Path $WorkDir 'harness'
$harnessBin = Join-Path $WorkDir 'harness-bin'
New-Item -ItemType Directory -Force -Path $harnessSrc | Out-Null
$utf8 = [System.Text.UTF8Encoding]::new($false)
[System.IO.File]::WriteAllText((Join-Path $harnessSrc 'GoldenHarness.csproj'), $HarnessProject, $utf8)
[System.IO.File]::WriteAllText((Join-Path $harnessSrc 'Program.cs'), $HarnessProgram, $utf8)
Write-Host 'Building calibration-target host'
Invoke-Checked dotnet @('build', (Join-Path $harnessSrc 'GoldenHarness.csproj'), '-c', 'Release', '-o', $harnessBin,
    "-p:UpstreamRoot=$upstreamDir", '-nologo', '-v', 'quiet') 'Calibration host build' | Out-Null
$harnessExe = Join-Path $harnessBin 'golden-harness.exe'

$tools = @{ 'disc-image-studio' = $cliExe; 'golden-harness' = $harnessExe }

# ---------------------------------------------------------------------------
# Run every case $Runs times and compare.
# ---------------------------------------------------------------------------
$fixturesArg = $FixtureDir.Replace('\', '/')
$results = @()   # one hashtable per run: "case/file" -> @{ sha256; bytes }
for ($run = 1; $run -le $Runs; $run++) {
    $outDir = Join-Path $WorkDir "run-$run"
    if (Test-Path -LiteralPath $outDir) { Remove-Item -LiteralPath $outDir -Recurse -Force }
    New-Item -ItemType Directory -Path $outDir | Out-Null
    $outArg = $outDir.Replace('\', '/')
    Write-Host "Run $run of $Runs -> $outDir"
    $hashes = @{}
    foreach ($case in $config.cases) {
        $exe = $tools[[string]$case.tool]
        if (-not $exe) { throw "Case $($case.id): unknown tool '$($case.tool)'." }
        [string[]]$caseArgs = @($case.args | ForEach-Object { ([string]$_).Replace('{fixtures}', $fixturesArg).Replace('{out}', $outArg) })
        Invoke-Checked $exe $caseArgs "Case $($case.id)" | Out-Null
        foreach ($expected in $case.outputs) {
            $path = Join-Path $outDir $expected.file
            if (-not (Test-Path -LiteralPath $path)) { throw "Case $($case.id) did not write $($expected.file)." }
            $hashes["$($case.id)/$($expected.file)"] = @{ sha256 = (Get-Sha256 $path); bytes = (Get-Item -LiteralPath $path).Length }
        }
        Write-Host "  $($case.id)"
    }
    $results += , $hashes
}

$failures = [System.Collections.Generic.List[string]]::new()
foreach ($case in $config.cases) {
    foreach ($expected in $case.outputs) {
        $key = "$($case.id)/$($expected.file)"
        $first = $results[0][$key]
        for ($run = 1; $run -lt $Runs; $run++) {
            if ($results[$run][$key].sha256 -ne $first.sha256) {
                $failures.Add("$key differs between run 1 and run $($run + 1)")
            }
        }
        if ($Update) {
            $expected | Add-Member -NotePropertyName sha256 -NotePropertyValue $first.sha256 -Force
            $expected | Add-Member -NotePropertyName bytes -NotePropertyValue $first.bytes -Force
        }
        elseif (-not ($expected.PSObject.Properties.Name -contains 'sha256') -or $expected.sha256 -ne $first.sha256 -or
                $expected.bytes -ne $first.bytes) {
            $failures.Add("$key is $($first.sha256) ($($first.bytes) bytes), recorded $($expected.sha256)")
        }
        else {
            Write-Host ("  [ PASS ] {0,-52} {1}" -f $key, $first.sha256)
        }
    }
    if ($Update) {
        $line = (@($case.tool) + @($case.args | ForEach-Object { [string]$_ })) -join ' '
        $case | Add-Member -NotePropertyName commandLine -NotePropertyValue $line -Force
    }
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Host "  [ FAIL ] $_" }
    throw "$($failures.Count) golden output(s) did not reproduce."
}

if ($Update) {
    $config.upstream.sdk = $sdk
    $json = ($config | ConvertTo-Json -Depth 10).Replace("`r`n", "`n") + "`n"
    [System.IO.File]::WriteAllText($JsonPath, $json, $utf8)
    Write-Host "Updated $JsonPath"
}
else {
    Write-Host "All golden outputs reproduced $Runs time(s) at upstream $pinnedCommit."
}
