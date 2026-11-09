# IgnisDisc Optical Studio

[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)
[![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%2F%2011%20x64-lightgrey.svg)]()
[![.NET](https://img.shields.io/badge/.NET-10-blue.svg)](https://dotnet.microsoft.com/)

A free, open-source CD and DVD burning and mastering tool for Windows 10 and 11, with a WPF desktop app
(`IgnisDisc.exe`) and a scriptable command-line tool (`ignisdisc-cli.exe`). It burns through Windows' own
burning API (IMAPIv2), reads discs through SCSI pass-through, and ships a virtual optical drive so the whole
pipeline can be tested without hardware or blank media.

> Internal project and namespace names still use `CdBurner.*`; the product name is IgnisDisc.

---

## What works today

<!-- SUPPORT-TABLE-START -->
| Feature | Physical drives | Virtual drive | Notes |
| --- | :---: | :---: | --- |
| Burn data disc from files / folders | ✅ | ✅ | ISO 9660 + Joliet + UDF 1.02 bridge |
| Burn an existing `.iso` | ✅ | ✅ | |
| Create `.iso` image files | ✅ | — | Validated against an independent reader (DiscUtils) in the test suite |
| Verify after burn | ✅ | ✅ | SHA-256 readback |
| Rip data disc to `.iso` / `.bin` + `.cue` | ✅ | ✅ | |
| Burn audio CD (Disc-at-Once / Track-at-Once) | ✅ | ✅ | WAV, FLAC (built-in decoder, incl. 24-bit / hi-res / surround), MP3/M4A via Media Foundation; `.cue` sheets (per-track or single-file albums) and `.m3u`/`.m3u8`/`.pls` playlists; loudness leveling, crossfade, preview, printable jewel-case insert |
| Burn / verify / convert legacy images | ✅ | ✅ | Nero `.nrg`, Alcohol 120% `.mdf`/`.mds`, CloneCD `.ccd`/`.img`, incl. Mode 2 / XA; streamed, no size limit |
| Disc copy (one or two drives, multi-copy) | ⚠️ | ✅ | Swap prompts between copies; audio CDs copied track by track |
| Rip audio CD to `.wav` / `.flac` + `.cue` | ✅ | ✅ | Reads the real TOC and CD-Text; optional MusicBrainz track names |
| AccurateRip verification + read-offset correction | ✅ | ✅ | Detects your drive's offset from a disc in the AccurateRip database |
| Secure audio ripping (C2 error pointers + re-reads) | ✅ | ✅ | Flags suspect sectors; can be slow on badly scratched discs |
| Archive Studio: span folders across discs | ✅ | ✅ | SHA-256 manifest on every disc; optional PAR2 recovery data (needs `par2.exe`) |
| Quick / full erase of rewritable media | ✅ | ✅ | |
| Re-burn a used DVD+RW / DVD-RAM | ✅ | ✅ | Asks before replacing the files on the disc (`--overwrite` on the command line); finalized CD-RW / sequential DVD-RW require `erase` first |
| Surface scan | ✅ | ✅ | Per-track Full TOC (`0x02`) scan skips unrecorded multi-session gaps and auto-selects 2352B raw mode on audio tracks |
| Append a session (multi-session) | ✅ | ✅ | Open and appended sessions omit UDF 1.02 so Windows mounts Joliet across all sessions |
| Bootable discs (El Torito BIOS + UEFI) | ✅ | ✅ | Hybrid BIOS + UEFI Boot Catalog verified via physical burn, readback, and rip; boot behaviour depends on your loaders |
| DVD-Video authoring | ⚠️ | ✅ | Requires `ffmpeg.exe`; test on a standalone player before relying on it |
| Blu-ray (BD-R / BD-RE) | ❌ | — | Not yet: Blu-ray media are not recognised by the app; planned, and will be tested on real discs first |
| CD-Text / ISRC on burned audio CDs | ❌ | ✅ | IMAPIv2 cannot write lead-in CD-Text; metadata is kept in projects and `.cue` files |
| DVD+R book type (bitsetting) | ❌ | ✅ | Needs drive-vendor-specific commands; reports "not supported" instead of pretending |
| Test-write / simulation | ❌ | ✅ | Not available through IMAPIv2 |
<!-- SUPPORT-TABLE-END -->

✅ supported · ⚠️ works but needs more real-hardware testing · ❌ not available on physical drives yet

Reading from physical drives uses SCSI pass-through when Windows allows it and falls back to standard volume reads
(data) or `IOCTL_CDROM_RAW_READ` (audio) otherwise, so no administrator rights are needed.

---

## Install

Download `IgnisDisc-Setup.exe` (per-user install, no admin rights) or the portable `.zip` from the Releases page.
The builds are not code-signed yet, so Windows SmartScreen may show a warning on first run; check the SHA-256 checksum
listed on the release page first. IgnisDisc has no telemetry. It goes online only to look up an audio CD
(MusicBrainz titles, AccurateRip checksums — both on by default when ripping and switchable off) and, if you turn it
on, to check GitHub for a newer release. [SECURITY.md](SECURITY.md) lists exactly what is sent.

---

## Command-line usage

```text
ignisdisc-cli <command> [options]

  drives [--json]                              List optical drives (physical and virtual)
  media-info --drive <id>                      Disc status, capacity, sessions, speeds
  build-iso  --input <folder> --output <file.iso> [--label <vol>] [--udf] [--joliet]
             [--boot-bios <img>] [--boot-uefi <img>]
  burn-iso   --drive <id> --iso <file.iso|.nrg|.mdf|.ccd|.img> [--speed <X>] [--verify] [--no-close] [--overwrite]
  burn-data  --drive <id> --input <folder> [--label <vol>] [--speed <X>] [--udf] [--joliet]
             [--boot-bios <img>] [--boot-uefi <img>] [--verify] [--no-close] [--overwrite]
  burn-audio --drive <id> --tracks <a.wav,b.flac,album.cue,list.m3u,...> [--title <album>] [--artist <name>]
             [--speed <X>] [--dao] [--native --test-write]
  burn-dvd   --drive <id> --video-ts <folder|movie> [--label <vol>] [--overwrite]
  rip        --drive <id> --output <file.iso|file.bin> [--raw]
  rip-audio  --drive <id> --output <folder> [--format wav|flac] [--tracks 1,3,5] [--lookup]
             [--read-offset <samples>] [--verify-accuraterip] [--secure] [--continue-on-error]
  detect-offset --drive <id>                   Find the drive's audio read offset (AccurateRip)
  archive-stage --input <folder> (--staging <folder> | --plan-only) [--media cd|dvd|dvd-dl]
             [--redundancy <0-100>]
  archive-verify --path <folder|drive>         Check an archive disc against its SHA-256 manifest
  verify     --drive <id> --iso <file.iso|.nrg|.mdf|.ccd|.img>
  erase      --drive <id> [--full]
  scan       --drive <id> [--block-size <sectors>]

Every command accepts --json for machine-readable output. --drive takes a drive letter (e.g. D:) or the ID
shown by `drives`.
```

`--udf` / `--joliet`: by default an image contains ISO 9660, Joliet and UDF. Passing either flag limits the image to
ISO 9660 plus the file systems you name.

`--overwrite`: a used DVD+RW or DVD-RAM is rewritten from the start, so a burn replaces every file on it. Burns refuse
to do that unless you pass `--overwrite`. Sequential rewritable discs (CD-RW, sequential DVD-RW) cannot be overwritten
in place once finalized—run `ignisdisc-cli erase --drive <id>` before burning them again.

`--native --test-write` (experimental): a test write of the audio CD using IgnisDisc's own drive commands instead of
Windows' burning service — Session-at-Once, with CD-Text in the lead-in, and the laser off, so the disc is not
written. It needs a blank CD-R or CD-RW and a drive that offers Session-at-Once test writes, and saves a log of every
drive command under `%LOCALAPPDATA%\IgnisDisc\logs`. Real native burns are not enabled yet.

Examples:

```powershell
ignisdisc-cli drives
ignisdisc-cli build-iso --input "C:\Photos\2026" --output C:\temp\photos.iso --label PHOTOS_2026
ignisdisc-cli burn-iso --drive D: --iso C:\Images\ubuntu.iso --speed 8 --verify
ignisdisc-cli burn-data --drive D: --input "C:\Deploy" --label INSTALLER --boot-bios etfsboot.com --boot-uefi efisys.bin
ignisdisc-cli burn-audio --drive D: --tracks "01.wav,02.wav,03.wav" --title "Demo Album" --artist "Me" --dao
ignisdisc-cli rip --drive D: --output C:\Backup\disc.iso
ignisdisc-cli rip-audio --drive D: --output C:\Music\Album --format flac --lookup --verify-accuraterip --secure
ignisdisc-cli archive-stage --input "C:\Photos" --staging C:\temp\archive --media dvd --redundancy 10
ignisdisc-cli erase --drive D:
```

---

## Desktop app

Workspaces: Data Disc (including bootable discs and Save as ISO), Audio CD (burn, and rip with AccurateRip / secure
mode / read-offset detection), DVD-Video, Disc Image (burn/rip/verify `.iso` and `.bin/.cue`), Archive (plan, stage,
burn, verify and repair multi-disc archives), Drive Utilities (erase, scan, media inspector), Virtual Drive manager,
and Settings.
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

## Roadmap

- Opt-in native SCSI MMC write engine (`SEND CUE SHEET` / `WRITE(10)`) for physical lead-in CD-Text, `INDEX 00` pre-gaps, and DVD±R DL layer-break control
- CUETools Database (CTDB) verification and audio repair alongside AccurateRip
- Blu-ray (`BD-R` / `BD-RE`) media recognition and UDF 2.50 mastering
- Mixed-mode and Enhanced CD compilation and disc copy support

---

## License

MIT — see [LICENSE](LICENSE) and [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
