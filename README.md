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
| Burn data disc from files / folders | ✅ | ✅ | ISO 9660 + Joliet + UDF bridge; UDF 1.02 by default, 1.50 to 2.60 on request (`--udf-rev`) |
| Burn an existing `.iso` | ✅ | ✅ | |
| Create `.iso` image files | ✅ | — | Validated against an independent reader (DiscUtils) in the test suite |
| Verify after burn | ✅ | ✅ | Reads every sector back and compares it with the image. The drive is told to read the disc itself, not its memory, where burn data can still be held; `--strict-verify` does that for every read. Damage is reported by file |
| Hash lists on data discs (`SHA256SUMS` / `BLAKE3SUMS`) | ✅ | ✅ | Optional (`--sha256sums`, `--blake3sums`, or checkboxes on the Data Disc screen); written to the disc root and checked file by file after the burn |
| Rip data disc to `.iso` / `.bin` + `.cue` | ✅ | ✅ | |
| Burn audio CD (Disc-at-Once / Track-at-Once) | ✅ | ✅ | WAV, FLAC (built-in decoder, incl. 24-bit / hi-res / surround), MP3/M4A via Media Foundation; `.cue` sheets (per-track or single-file albums) and `.m3u`/`.m3u8`/`.pls` playlists; loudness leveling or an optional -18 LUFS normalisation with a -1 dBTP limiter, crossfade, preview, printable jewel-case insert. Opus, Ogg and MP3 can also be decoded through the optional ffmpeg download, trimmed to the exact length for gapless albums; the lossy decoding and the normalisation have only been tested on files and the virtual drive, with one 2021 ffmpeg build, not on a burned disc |
| Burn / verify / convert legacy images | ✅ | ✅ | Nero `.nrg`, Alcohol 120% `.mdf`/`.mds`, CloneCD `.ccd`/`.img`, incl. Mode 2 / XA; streamed, no size limit. DiscJuggler `.cdi` (v2, v3, v3.5) is read as well, checked only on hand-built files that follow the published layout, not on an image made by DiscJuggler; the first data track is used |
| Disc copy (one or two drives, multi-copy) | ⚠️ | ✅ | Swap prompts between copies; audio CDs copied track by track. A mixed-mode CD (data track first, then audio) is copied whole with direct drive commands turned on, otherwise only its audio; done once on a real drive (one drive, a CD-RW copied to a CD-R). The source audio is read with error checks and re-reads, and a copy stops before writing when sectors still read wrong |
| Rip audio CD to `.wav` / `.flac` / `.mp3` / `.opus` / `.m4a` + `.cue` | ✅ | ✅ | Reads the real TOC and CD-Text; optional MusicBrainz track names. AAC uses the encoder built into Windows; FLAC, MP3 and Opus use the optional ffmpeg download (no encoder is bundled). Ripping has run on a real drive (LG GP60NB50): tracks from CD-Rs and a CD-RW burned by IgnisDisc came back identical to the source audio. The encoders and output formats are otherwise tested on the virtual drive only; the built-in FLAC decoder (used for burning) is also checked against files made by the official flac 1.5.0 encoder |
| AccurateRip verification + read-offset correction | ✅ | ✅ | Detects your drive's offset from a disc in the AccurateRip database |
| Secure audio ripping (C2 error pointers + re-reads) | ✅ | ✅ | Flags suspect sectors; can be slow on badly scratched discs |
| Archive Studio: span folders across discs | ✅ | ✅ | SHA-256 manifest on every disc; optional PAR2 recovery data (needs `par2.exe`) fills the space left on each disc, split over an inner, middle and outer zone with a copy of the volume descriptor in each; `recover` copies the files off a damaged archive disc and rebuilds them, also with sector 16, the first UDF anchor and one whole zone destroyed (tested on images). An archive burn can also write a HEALTH.json and HEALTH.svg report, and `check-disc --compare` sets a later read against it; this has only been tested on the virtual drive, not run on a real drive |
| Quick / full erase of rewritable media | ✅ | ✅ | |
| Re-burn a used DVD+RW / DVD-RAM | ✅ | ✅ | Asks before replacing the files on the disc (`--overwrite` on the command line); finalized CD-RW / sequential DVD-RW require `erase` first |
| Surface scan | ✅ | ✅ | Per-track Full TOC (`0x02`) scan skips unrecorded multi-session gaps and auto-selects 2352B raw mode on audio tracks |
| Append a session (multi-session) | ✅ | ✅ | Open and appended sessions omit UDF 1.02 so Windows mounts Joliet across all sessions; names longer than 64 characters are shortened there, and the burn result says so |
| Bootable discs (El Torito BIOS + UEFI) | ✅ | ✅ | Hybrid BIOS + UEFI Boot Catalog verified via physical burn, readback, and rip; boot behaviour depends on your loaders |
| DVD-Video authoring | ⚠️ | ✅ | Requires `ffmpeg.exe`; test on a standalone player before relying on it. With direct drive commands one 60 s test disc was burned to a DVD+RW on a real drive (read back and decoded in full), but not played in a stand-alone player |
| Blu-ray (BD-R / BD-RE) | ❌ | — | Not yet: Blu-ray discs are recognised (type, size, layers) and archive sets can be planned for them, and the experimental direct writer handles single-layer BD-R and BD-RE in the drive simulator, but burning one has never been tried on a real drive |
| Burn CDs with direct drive commands (experimental, off by default) | ⚠️ | ✅ | Audio CDs Session-at-Once and data CDs Track-at-Once without the Windows writer (`--native`, or "Direct drive commands" in the app); the disc is always read back and compared. Run on real hardware on one drive so far (LG GP60NB50), on CD-R and CD-RW. Mixed-mode CDs (a Mode 1 data track, then audio) from a `.cue` file too: one CD-RW so far, where Windows read the data track's files back identical. Blank single-layer DVD-R, DVD-RW and DVD+R can also be written this way. A further session on a CD-R, CD-RW, DVD-R, DVD-RW or DVD+R left open is added the same way: on real discs on the same drive, three sessions each on a CD-RW, a CD-R and a DVD+R and four on a DVD-R, every file of every session identical (the first DVD-R try was refused with nothing written, because the drive rejects the writer's old track query on DVD; fixed before these runs); DVD-RW only in the simulator so far. On real hardware so far two DVD-Rs on the same drive (Verbatim): one closed in one pass (372 MB), one left open, after which Windows' own writer added a second session; every file of each session identical when read through Windows. Two DVD+RWs too (Verbatim, 372 MB each, every file identical): a used one written over with leave to replace its data, and a new one, formatted first by the drive. A single-layer DVD+R too (Verbatim, 198 MB, finalized by the drive, every file identical). A DVD-Video disc was written to a used DVD+RW this way too (a 60 s test card, 44 MB): every sector read back and the title decodes in full, but it has not been played in a stand-alone player. Dual-layer DVD+R (with the layer break set by IgnisDisc), dual-layer DVD-R (layer jump recording, on drives that offer it), DVD-RW (sequential or in overwrite mode), DVD-RAM and single-layer BD-R and BD-RE have run only in the built-in drive simulator. Dual-layer DVD-R on a drive without layer jump recording, and multi-layer Blu-ray discs, still use the Windows writer |
| CD-Text / ISRC on burned audio CDs | ⚠️ | ✅ | The Windows writer (the default) cannot write lead-in CD-Text. The experimental direct writer (`--native`, or "Direct drive commands" in the app) writes CD-Text, ISRC and catalog codes, then reads the disc back and compares it. Run on real hardware on one drive so far (LG GP60NB50): a CD-RW and a CD-R, where CD-Text, ISRC and catalog codes and the audio all read back correctly |
| DVD+R book type (bitsetting) | ❌ | ✅ | Needs drive-vendor-specific commands; reports "not supported" instead of pretending |
| Test-write / simulation | ⚠️ | ✅ | With the experimental direct writer (`--native --test-write`): the whole burn with the laser off. Run on one drive (LG GP60NB50) on CD-R and CD-RW. DVD-R has passed a test write on the same drive (a Verbatim DVD-R, still blank after a reload); DVD-RW only in the drive simulator so far; DVD+R discs have none. The Windows writer has no test write |
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
  burn-iso   --drive <id> --iso <file.iso|.nrg|.mdf|.ccd|.img|.cdi> [--speed <X|max>] [--no-verify | --strict-verify] [--no-close] [--overwrite]
             [--native [--test-write | --test-first] [--log-dir <folder>]]
  burn-data  --drive <id> --input <folder> [--label <vol>] [--speed <X|max>] [--udf] [--joliet]
             [--boot-bios <img>] [--boot-uefi <img>] [--no-verify | --strict-verify] [--no-close] [--overwrite] [--fixed-time <...>]
             [--sha256sums] [--blake3sums] [--udf-rev <...>] [--native [--test-write | --test-first] [--log-dir <folder>]]
  burn-audio --drive <id> --tracks <a.wav,b.flac,album.cue,list.m3u,...> [--title <album>] [--artist <name>]
             [--speed <X|max>] [--dao] [--allow-damaged] [--native [--test-write | --test-first] [--write-offset <n|auto>]
             [--log-dir <folder>]]
  burn-dvd   --drive <id> --video-ts <folder|movie> [--label <vol>] [--speed <X|max>] [--no-verify] [--overwrite]
             [--native [--test-write | --test-first] [--log-dir <folder>]]
  rip        --drive <id> --output <file.iso|file.bin> [--raw]
  rip-audio  --drive <id> --output <folder> [--format wav|flac|mp3|opus|aac] [--bitrate <kbps>] [--tracks 1,3,5] [--lookup]
             [--read-offset <samples>] [--verify-accuraterip] [--no-secure] [--continue-on-error] [--cover <image>]
                                               Exit code 2 when sectors may be wrong (still suspect or unreadable)
  detect-offset --drive <id>                   Find the drive's audio read offset (AccurateRip)
  copy-disc  --source <id> [--target <id>] [--copies <n>] [--speed <X|max>] [--raw] [--native [--log-dir <folder>]]
             [--allow-suspect] [--keep-image <folder>] [--wait-minutes <n>]
                                               Copy a disc with one drive (the tray opens, it waits for a blank) or two;
                                               stops before writing if source audio still reads wrong
  check-disc --drive <id> [--compare <file>]   Read a CD once with C2 error pointers: where it is weak (may skip); --compare sets it against a HEALTH.json
  archive-stage --input <folder> (--staging <folder> | --plan-only) [--media cd|dvd|dvd-dl|bd|bd-dl|bdxl|bdxl-ql]
             [--redundancy <min %>] [--no-fill]      Recovery data fills each disc; --no-fill keeps exactly that %
             [--keep-folders | --order size|path]    Largest-first packing (default); whole folders; or path order.
                                                     Every disc gets CATALOG.JSON and INDEX.HTML listing the whole set
  archive-burn --drive <id> --disc <staged disc folder> [--native [--test-write | --test-first] [--log-dir <folder>]]
                                               Burn one staged archive disc; --native uses direct drive commands; --health-certificate writes discNN.HEALTH.json + .svg (off by default)
  archive-verify --path <folder|drive>         Check an archive disc against its SHA-256 manifest
  recover (--drive <id>|--image <file.iso>) --out <folder>
                                               Copy a damaged archive disc's files out, rebuild them from its
                                               PAR2 recovery data and check them (exit code 2 if any are lost)
  verify     --drive <id> --iso <file.iso|.nrg|.mdf|.ccd|.img|.cdi>
  verify     --drive <id> --cue <file.cue> [--title <album>] [--artist <name>] [--no-cd-text] [--write-offset <n|auto>]
                                               Check an audio or mixed-mode CD burned with --native against its .cue,
                                               without writing (track table, CD-Text, codes, every sample)
  erase      --drive <id> [--full]
  scan       --drive <id> [--block-size <sectors>] [--csv <file.csv>]
  drive-report --drive <id>                    Build a drive compatibility report (capabilities and media ID)
  eject      --drive <id>                      Open the tray
  load       --drive <id>                      Close the tray and wait until the drive is ready; on a drive that
                                               cannot close its own tray (a spring tray, as on slim USB drives) it
                                               sends nothing, says to push the tray in by hand and exits 1

`eject` and `load` have been tested on the virtual drive only, not on a real drive.

Every command accepts --json for machine-readable output. --drive takes a drive letter (e.g. D:) or the ID
shown by `drives`.
```

`--udf` / `--joliet`: by default an image contains ISO 9660, Joliet and UDF. Passing either flag limits the image to
ISO 9660 plus the file systems you name.

Verify (on by default; `--no-verify` skips it): after the burn every sector is read back and compared with the image. A drive can answer a read from
its own memory instead of the disc, so the check tells it to read the disc itself (the FUA bit) wherever burn data
can still be in that memory: the last part written and the start of the disc, each twice the size of the buffer the
drive reports. The rest is read normally. `--strict-verify` sets that bit on every read instead; it leaves nothing
to the drive's buffer size, and is several times slower (measured on one USB DVD writer: 1.2 MB/s against 3.5 MB/s).
The app has the same choice under Settings ("Strict verify").

Write speed (when `--speed` is not given, and in the app unless Settings says otherwise): the fastest speed the drive
writes at a constant linear velocity for the inserted disc, up to 16x for CD and 8x for DVD, taken from the drive's
own speed list. CAV and partial-CAV speeds rise towards the outer edge, so the drive keeps adjusting the laser; the
slowest speed is not automatically the best either. On a slim USB writer that offers CD-R at 10x CLV, 16x and 24x CAV,
this is 10x.
A small built-in table of media IDs caps that default for discs known to read weakly: the FORNET CD-R (ATIP 97m26s0x) from the batch that failed the read-back check on an LG GP60NB50 (see the status section below). The app reads the media ID when a disc changes and shows the reason next to the speed picker. The cap is tested with unit tests only, and the burn commands on the command line do not apply it yet.
`media-info` shows the choice and why; `--speed <X>`, `--speed slowest` and `--speed max` override it.

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
  a test write and refuse to record until the disc is reloaded. A drive that says it can close its own tray reloads
  the disc and goes on to the real burn; any other (slim drives with spring trays included) stops after the test and
  says so. The automatic reload has run only in the simulator.
- `--native --write-offset <samples|auto>`: the drive's write offset is corrected so that a rip with the drive's
  AccurateRip read offset gives the source back sample for sample. `auto` (the default) takes the read offset from
  AccurateRip's drive list (downloaded when needed) and the combined offset measured by earlier direct burns on the
  same drive; the first burn on a drive only measures it. `0` turns the correction off.

Status: experimental. On real hardware it has run on one drive (an LG GP60NB50): test writes on CD-R and CD-RW, a
two-track CD-RW with CD-Text, a four-track CD-R with CD-Text, catalog and ISRC codes, pre-emphasis and copy
flags, gap marks and audio in front of track 1, and an eight-track MP3 album on several CD-Rs (the read-back check
failed on two discs of an old batch that read weakly towards the outer edge, and passed on a newer one; a secure rip
of each disc matched the source). A mixed-mode CD-RW (data track, then audio) and its copies on CD-R read back
identical to the source. The write-offset correction was checked on one CD-R: an offset-corrected rip matched the source sample for sample. Other drives have
only been run against the built-in drive simulator, and no disc has been tried in a stand-alone CD player yet.

`--native` on `burn-iso` and `burn-data` (experimental, also "Direct drive commands" on the app's Data Disc and Disc
Image screens) writes the image to a blank CD-R or CD-RW with IgnisDisc's own drive commands: one Mode 1 data track,
Track-at-Once, at a speed picked from the drive's own list for that disc (by default its fastest constant-velocity speed up to 16x, or the `--speed` given,
rounded down to one the drive offers), never left to Windows. The disc is always read back sector by sector from the
disc itself afterwards, even with `--no-verify`. A blank single-layer DVD-R or DVD-RW is written Disc-At-Once (or in
packets when `--no-close` leaves it open) and a DVD+R in sequence, then closed or finalized; this DVD path has
run on one drive so far. On DVD-Rs: a test write, a 372 MB burn closed in one pass, and a disc left open to which
Windows' own writer then added a second session, every file of both sessions reading back identical. On a DVD+R: a
198 MB burn finalized by the drive, every file reading back identical. DVD-RW has run only in the built-in drive
simulator. A CD-R, CD-RW, DVD-R, DVD-RW or DVD+R left open gets its next
session the same way, laid out from the disc's next writable address with the earlier files kept. On real discs on
the same drive: three sessions each on a CD-RW, a CD-R and a DVD+R, and four on a DVD-R, every file of every session
identical when read through Windows and straight from the drive, before and after reinserting the disc; DVD-RW has
only run in the simulator. A dual-layer DVD-R is written with layer jump recording when the drive offers it (simulator only so far). Any
other disc (dual-layer DVD-R on a drive without layer jump, multi-layer Blu-ray, a Blu-ray or dual-layer disc left
open) is burned by the Windows writer as before, and the result says so. A DVD+RW is
written from sector 0 (formatted first if it is new); one that holds data is only written over with `--overwrite`.
A used DVD+RW (written over) and a new one (formatted first) have been burned this way on real hardware. On a real used DVD+RW, a second burn without `--overwrite` was refused before anything was written, by the direct writer and by the Windows writer, and the first burn's files stayed identical.
A DVD-RW in overwrite mode and a DVD-RAM are handled the same way (a partly formatted DVD-RW is expanded first; a
never-formatted DVD-RAM is formatted first), so far only in the simulator. A single-layer BD-R is written like a DVD+R and
finalized, and a BD-RE is formatted first when new and then written like a DVD+RW; Blu-ray has also only run in the
simulator, and multi-layer Blu-ray discs go to the Windows writer. On a dual-layer DVD+R the layer break is set before
writing: an image that fits on the first layer stays there, a larger one is split in half (the first layer never
smaller than the second); simulator only so far. On the DVD-Video screen, "Direct drive commands" puts the break of a
dual-layer DVD+R on a cell boundary instead, read from the title sets' IFO files, where players expect the layer
change (simulator only). The DVD-Video screen's direct path has also written a 60 s test disc to a used DVD+RW on
real hardware: every sector read back and the title decodes in full; it has not been played in a stand-alone player.
An audio CD written this way can be left open for a second session, and a data session can be added after it (CD-Extra); this and the optional recovery-data session on an audio CD have run only in the built-in drive simulator, not on a real disc.
`--test-write` and
`--test-first` work as for audio (DVD+R has no test write). On real hardware it has run on one drive so far; other
drives are still to be tried.

Examples:

```powershell
ignisdisc-cli drives
ignisdisc-cli build-iso --input "C:\Photos\2026" --output C:\temp\photos.iso --label PHOTOS_2026
ignisdisc-cli burn-iso --drive D: --iso C:\Images\ubuntu.iso --speed 8
ignisdisc-cli burn-data --drive D: --input "C:\Deploy" --label INSTALLER --boot-bios etfsboot.com --boot-uefi efisys.bin
ignisdisc-cli burn-audio --drive D: --tracks "01.wav,02.wav,03.wav" --title "Demo Album" --artist "Me" --dao
ignisdisc-cli rip --drive D: --output C:\Backup\disc.iso
ignisdisc-cli copy-disc --source D: --native
ignisdisc-cli rip-audio --drive D: --output C:\Music\Album --format flac --lookup --verify-accuraterip
ignisdisc-cli archive-stage --input "C:\Photos" --staging C:\temp\archive --media dvd --redundancy 10
ignisdisc-cli archive-burn --drive D: --disc C:\temp\archive\disc01 --native
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

- Direct drive-command writing tried on real DVD-RW discs (DVD-R, DVD+R and DVD+RW have run on real discs), then
  DVD-RAM, Blu-ray and DVD±R DL layer-break control (audio CDs and data CDs can already be written this
  way as an experimental option)
- More drives and disc types tried with the direct writer, so it can leave experimental status
- Blu-ray burning, tested on real drives
- CUETools Database (CTDB) verification and audio repair alongside AccurateRip
- Enhanced CD (CD-Extra: audio first, data in a second session) and copying both tracks of a mixed-mode CD

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
