# IgnisDisc Optical Studio

[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)
[![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%2F%2011%20x64-lightgrey.svg)]()
[![.NET](https://img.shields.io/badge/.NET-10-blue.svg)](https://dotnet.microsoft.com/)

A free, open-source CD / DVD / Blu-ray burning and mastering tool for Windows 10 and 11, with a WPF desktop app
(`IgnisDisc.exe`) and a scriptable command-line tool (`ignisdisc-cli.exe`). It burns through Windows' own
burning API (IMAPIv2), reads discs through SCSI pass-through, and ships a virtual optical drive so the whole
pipeline can be tested without hardware or blank media.

> Internal project and namespace names still use `CdBurner.*`; the product name is IgnisDisc.

---

## What works today

| Feature | Physical drives | Virtual drive | Notes |
| --- | :---: | :---: | --- |
| Burn data disc from files / folders | ✅ | ✅ | ISO 9660 + Joliet + UDF 1.02 bridge |
| Burn an existing `.iso` | ✅ | ✅ | |
| Create `.iso` image files | ✅ | — | Validated against an independent reader (DiscUtils) in the test suite |
| Verify after burn | ✅ | ✅ | SHA-256 readback |
| Rip data disc to `.iso` / `.bin` + `.cue` | ✅ | ✅ | |
| Burn audio CD (Disc-at-Once / Track-at-Once) | ✅ | ✅ | MP3/FLAC/WAV/M4A decoded via Media Foundation |
| Rip audio CD to `.wav` + `.cue` | ✅ | ✅ | Reads the real TOC; no AccurateRip / offset correction yet |
| Quick / full erase of rewritable media | ✅ | ✅ | |
| Surface scan | ✅ | ✅ | |
| Append a session (multi-session) | ⚠️ | ✅ | Appended sessions are ISO 9660 + Joliet (no UDF) |
| Bootable discs (El Torito BIOS + UEFI) | ⚠️ | ✅ | Image creation tested; boot behaviour depends on your loaders |
| DVD-Video authoring | ⚠️ | ✅ | Requires `ffmpeg.exe`; test on a standalone player before relying on it |
| CD-Text / ISRC on burned audio CDs | ❌ | ✅ | IMAPIv2 cannot write lead-in CD-Text; metadata is kept in projects and `.cue` files |
| DVD+R book type (bitsetting) | ❌ | ✅ | Needs drive-vendor-specific commands; reports "not supported" instead of pretending |
| Test-write / simulation | ❌ | ✅ | Not available through IMAPIv2 |

✅ supported · ⚠️ works but needs more real-hardware testing · ❌ not available on physical drives yet

Reading from physical drives uses SCSI pass-through when Windows allows it and falls back to standard volume reads
(data) or `IOCTL_CDROM_RAW_READ` (audio) otherwise, so no administrator rights are needed.

---

## Install

Download `IgnisDisc-Setup.exe` (per-user install, no admin rights) or the portable `.zip` from the Releases page.
The builds are currently unsigned, so Windows SmartScreen may show a warning on first run.

---

## Command-line usage

```text
ignisdisc-cli <command> [options]

  drives [--json]                              List optical drives (physical and virtual)
  media-info --drive <id>                      Disc status, capacity, sessions, speeds
  build-iso  --input <folder> --output <file.iso> [--label <vol>] [--udf] [--joliet]
             [--boot-bios <img>] [--boot-uefi <img>]
  burn-iso   --drive <id> --iso <file.iso> [--speed <X>] [--verify] [--no-close]
  burn-data  --drive <id> --input <folder> [--label <vol>] [--speed <X>] [--udf] [--joliet]
             [--boot-bios <img>] [--boot-uefi <img>] [--verify] [--no-close]
  burn-audio --drive <id> --tracks <a.wav,b.wav,...> [--title <album>] [--artist <name>] [--dao]
  burn-dvd   --drive <id> --video-ts <folder|movie> [--label <vol>]
  rip        --drive <id> --output <file.iso|file.bin> [--raw]
  verify     --drive <id> --iso <file.iso>
  erase      --drive <id> [--full]
  scan       --drive <id> [--block-size <sectors>]

Every command accepts --json for machine-readable output. --drive takes a drive letter (e.g. D:) or the ID
shown by `drives`.
```

`--udf` / `--joliet`: by default an image contains ISO 9660, Joliet and UDF. Passing either flag limits the image to
ISO 9660 plus the file systems you name.

Examples:

```powershell
ignisdisc-cli drives
ignisdisc-cli build-iso --input "C:\Photos\2026" --output C:\temp\photos.iso --label PHOTOS_2026
ignisdisc-cli burn-iso --drive D: --iso C:\Images\ubuntu.iso --speed 8 --verify
ignisdisc-cli burn-data --drive D: --input "C:\Deploy" --label INSTALLER --boot-bios etfsboot.com --boot-uefi efisys.bin
ignisdisc-cli burn-audio --drive D: --tracks "01.wav,02.wav,03.wav" --title "Demo Album" --artist "Me" --dao
ignisdisc-cli rip --drive D: --output C:\Backup\disc.iso
ignisdisc-cli erase --drive D:
```

---

## Desktop app

Workspaces: Data Disc (including bootable discs and Save as ISO), Audio CD, DVD-Video, Disc Image (burn/rip/verify
`.iso` and `.bin/.cue`), Drive Utilities (erase, scan, media inspector), Virtual Drive manager, and Settings.
Projects save as `.cdbproj` JSON files.

---

## Building from source

Requirements: Windows 10 1903+ or Windows 11, x64, and the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```powershell
dotnet build CdBurner.sln -c Release
dotnet test  CdBurner.sln -c Release
```

The `Core` test project also runs on Linux/macOS (`-p:EnableWindowsTargeting=true`); hardware and WPF tests need Windows.

Build the installer and portable zip:

```powershell
pwsh scripts/Build-Installer.ps1
# optionally bundle ffmpeg (an LGPL build is recommended) together with its license:
pwsh scripts/Build-Installer.ps1 -FfmpegPath C:\tools\ffmpeg\bin\ffmpeg.exe -FfmpegLicensePath C:\tools\ffmpeg\LICENSE.txt
```

### Project layout

| Project | Contents |
| --- | --- |
| `src/CdBurner.Core` | File system builder (ISO 9660 / Joliet / UDF / El Torito), audio, CUE, DVD-Video, persistence, services |
| `src/CdBurner.Hardware` | IMAPIv2 COM interop, SCSI pass-through, physical sector reader, virtual drive simulator |
| `src/CdBurner.Cli` | `ignisdisc-cli` / `cdburner-cli` |
| `src/CdBurner.App` | WPF desktop app (`IgnisDisc.exe`) |
| `tools/CdBurner.Setup` | Per-user installer / uninstaller |
| `tests/*` | xUnit tests (virtual drive, image validation with an independent reader, CLI and view-model tests) |

---

## License

MIT — see [LICENSE](LICENSE) and [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
