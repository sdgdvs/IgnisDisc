# IgnisDisc Optical Studio

[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-blue.svg)](LICENSE)
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
| Append a session (multi-session) | ✅ | ✅ | Open and appended sessions omit UDF 1.02 so Windows mounts Joliet across all sessions; names longer than 64 characters are shortened there, and the burn result says so |
| Bootable discs (El Torito BIOS + UEFI) | ✅ | ✅ | Hybrid BIOS + UEFI Boot Catalog verified via physical burn, readback, and rip; boot behaviour depends on your loaders |
| DVD-Video authoring | ⚠️ | ✅ | Requires `ffmpeg.exe`; test on a standalone player before relying on it |
| Blu-ray (BD-R / BD-RE) | ❌ | — | Not yet: Blu-ray discs are recognised (type, size, layers) and archive sets can be planned for them, but burning one has never been tried on a real drive |
| CD-Text / ISRC on burned audio CDs | ⚠️ | ✅ | The Windows writer (the default) cannot write lead-in CD-Text. The experimental direct writer (`--native`, or "Direct drive commands" in the app) writes CD-Text, ISRC and catalog codes, then reads the disc back and compares it. Run on real hardware on one drive so far (LG GP60NB50): a CD-RW and a CD-R, where CD-Text, ISRC and catalog codes and the audio all read back correctly |
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
             [--boot-bios <img>] [--boot-uefi <img>] [--fixed-time <unix|iso8601>] [--sha256sums]
             [--blake3sums] [--udf-rev <1.02|1.50|2.00|2.01|2.50|2.60>]
                                               --udf-rev picks the UDF revision (default 1.02; 2.50/2.60 add the
                                               metadata partition BD-Video needs);
                                               --fixed-time (or SOURCE_DATE_EPOCH) gives byte-identical images;
                                               --sha256sums adds a root SHA256SUMS that `sha256sum -c` can check;
                                               --blake3sums adds a BLAKE3SUMS that `b3sum -c` can check
  burn-iso   --drive <id> --iso <file.iso|.nrg|.mdf|.ccd|.img> [--speed <X>] [--verify | --strict-verify] [--no-close] [--overwrite]
             [--native [--test-write | --test-first] [--log-dir <folder>]]
  burn-data  --drive <id> --input <folder> [--label <vol>] [--speed <X>] [--udf] [--joliet]
             [--boot-bios <img>] [--boot-uefi <img>] [--verify | --strict-verify] [--no-close] [--overwrite] [--fixed-time <...>]
             [--sha256sums] [--blake3sums] [--udf-rev <...>] [--native [--test-write | --test-first] [--log-dir <folder>]]
  burn-audio --drive <id> --tracks <a.wav,b.flac,album.cue,list.m3u,...> [--title <album>] [--artist <name>]
             [--speed <X>] [--dao] [--native [--test-write | --test-first] [--log-dir <folder>]]
  burn-dvd   --drive <id> --video-ts <folder|movie> [--label <vol>] [--overwrite]
  rip        --drive <id> --output <file.iso|file.bin> [--raw]
  rip-audio  --drive <id> --output <folder> [--format wav|flac] [--tracks 1,3,5] [--lookup]
             [--read-offset <samples>] [--verify-accuraterip] [--secure] [--continue-on-error] [--cover <image>]
  detect-offset --drive <id>                   Find the drive's audio read offset (AccurateRip)
  archive-stage --input <folder> (--staging <folder> | --plan-only) [--media cd|dvd|dvd-dl|bd|bd-dl|bdxl|bdxl-ql]
             [--redundancy <min %>] [--no-fill]      Recovery data fills each disc; --no-fill keeps exactly that %
  archive-verify --path <folder|drive>         Check an archive disc against its SHA-256 manifest
  recover (--drive <id>|--image <file.iso>) --out <folder>
                                               Copy a damaged archive disc's files out, rebuild them from its
                                               PAR2 recovery data and check them (exit code 2 if any are lost)
  verify     --drive <id> --iso <file.iso|.nrg|.mdf|.ccd|.img>
  erase      --drive <id> [--full]
  scan       --drive <id> [--block-size <sectors>] [--csv <file.csv>]
  drive-report --drive <id>                    Build a drive compatibility report (capabilities and media ID)

Every command accepts --json for machine-readable output. --drive takes a drive letter (e.g. D:) or the ID
shown by `drives`.
```

`--udf` / `--joliet`: by default an image contains ISO 9660, Joliet and UDF. Passing either flag limits the image to
ISO 9660 plus the file systems you name.

`--verify`: after the burn every sector is read back and compared with the image. A drive can answer a read from
its own memory instead of the disc, so the check tells it to read the disc itself (the FUA bit) wherever burn data
can still be in that memory: the last part written and the start of the disc, each twice the size of the buffer the
drive reports. The rest is read normally. `--strict-verify` sets that bit on every read instead; it leaves nothing
to the drive's buffer size, and is several times slower (measured on one USB DVD writer: 1.2 MB/s against 3.5 MB/s).
The app has the same choice under Settings ("Strict verify").

`--overwrite`: a used DVD+RW or DVD-RAM is rewritten from the start, so a burn replaces every file on it. Burns refuse
to do that unless you pass `--overwrite`. Sequential rewritable discs (CD-RW, sequential DVD-RW) cannot be overwritten
in place once finalized—run `ignisdisc-cli erase --drive <id>` before burning them again.

`--native` (experimental): burns the audio CD with IgnisDisc's own drive commands instead of Windows' burning
service, Session-at-Once. It writes CD-Text into the lead-in, ISRC and catalog (UPC/EAN) codes, pre-emphasis and
copy flags from a `.cue` sheet's `FLAGS`, the sheet's `INDEX 00` gap marks, and audio in front of track 1. After
writing it reads the whole disc back and compares the track list, the CD-Text, the catalog and ISRC codes and every
audio sample; the burn only
counts as a success if that check passes. It needs a blank CD-R or CD-RW and a drive that reports Session-at-Once,
and saves a log of every drive command under `%LOCALAPPDATA%\IgnisDisc\logs` (or `--log-dir`).

- `--native --test-write`: the same burn with the laser off; the disc stays blank.
- `--native --test-first`: a test write, then the real burn. Most drives keep showing the simulated session after
  a test write and refuse to record until the disc is reloaded; the burn then stops after the test and says so.

Status: experimental. On real hardware it has run on one drive (an LG GP60NB50): test writes on CD-R and CD-RW, a
two-track CD-RW with CD-Text, and a four-track CD-R with CD-Text, catalog and ISRC codes, pre-emphasis and copy
flags, gap marks and audio in front of track 1. Both discs read back identical to the source. Other drives have
only been run against the built-in drive simulator, and no disc has been tried in a stand-alone CD player yet.

`--native` on `burn-iso` and `burn-data` (experimental, also "Direct drive commands" on the app's Data Disc and Disc
Image screens) writes the image to a blank CD-R or CD-RW with IgnisDisc's own drive commands: one Mode 1 data track,
Track-at-Once, at a speed picked from the drive's own list for that disc (the fastest, or the `--speed` you give
rounded down to one the drive offers), never left to Windows. The disc is always read back sector by sector from the
disc itself afterwards, `--verify` or not. Any other disc (DVD, Blu-ray) and a disc that already holds a session are
burned by the Windows writer as before, and the result says so. `--test-write` and `--test-first` work as for audio.
On real hardware it has run on one drive and one CD-RW so far; CD-R and other drives are still to be tried.

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
ignisdisc-cli recover --drive D: --out C:\Rescued\disc01
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

- Direct drive-command writing for DVD and Blu-ray, and DVD±R DL layer-break control (audio CDs and data CDs can
  already be written this way as an experimental option)
- More drives and disc types tried with the direct writer, so it can leave experimental status
- Blu-ray burning, tested on real drives
- CUETools Database (CTDB) verification and audio repair alongside AccurateRip
- Mixed-mode and Enhanced CD compilation and disc copy support

---

## Code signing policy

Free code signing on Windows is provided by [SignPath.io](https://signpath.io), certificate by the
[SignPath Foundation](https://signpath.org) (application and CI pipeline configured; current release binaries are
unsigned until certificate provisioning completes).

- **Committers, reviewers, and approvers:** [tether-works](https://github.com/tether-works) maintainers. External pull
  requests require maintainer review before merge, and release builds are produced exclusively from tagged commits by
  `.github/workflows/release.yml` on GitHub-hosted runners.
- **Privacy policy:** This program will not transfer any information to other networked systems unless specifically
  requested by the user or the person installing or operating it. See [SECURITY.md](SECURITY.md) for full details on
  optional MusicBrainz, AccurateRip, and release update checks.

---

## License

`GPL-3.0-or-later` — see [LICENSE](LICENSE), [TRADEMARKS.md](TRADEMARKS.md), and [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

The source code in this repository is licensed under the GNU General Public License v3.0 or later. Pursuant to Section 7(e) of the GPLv3, the names **"IgnisDisc"**, **"tetherworks"**, **"Tetherworks"**, and the IgnisDisc application icons/logos are trademarks of Tetherworks. You may not distribute modified or unmodified binaries under the name **"IgnisDisc"** without prior written permission.
