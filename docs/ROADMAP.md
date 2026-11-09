# IgnisDisc Roadmap

The goal: what ImgBurn would be today if it had never been abandoned — and the obvious choice on Windows for anyone
burning or ripping CDs and DVDs.

**How to use this file.** It is the single shared engineering roadmap and release checklist for IgnisDisc.

- Tick a box (`- [x]`) in the same commit that finishes the work, and add the date and short commit hash.
- Add new problems where they belong instead of starting another list.
- Anything that needs the owner (hardware, accounts, money, decisions) is marked **[owner]**.
- Risk: **low** = no chance of ruining discs or user data; **med** = new code paths users will hit; **high** = can
  turn discs into coasters or needs real-hardware testing to prove.

Last reviewed: 2026-11-08 (v1.11 pre-release review of the P1 phase 1 / P2 / P3 work; 760 tests passing).

---

## P0 — Trust, correctness and release hygiene (low risk, do first)

### Releases
- [x] Ship the audited features (FLAC/CUE albums, disc copy, legacy images, playlists, crossfade, cover printing) as
      **v1.09**. Released 2026-11-08 (tag `v1.09`); installer smoke-tested, downloaded hashes match the site.
- [x] Stop slipstreaming: `Update-Version.ps1` and `Publish-ReleaseAndSite.ps1` refuse already-published versions;
      release notes come from `docs/release-notes/v<version>.md`. 2026-11-08
- [ ] Get `TetherWorks.IgnisDisc` into WinGet: microsoft/winget-pkgs#474307 now submits v1.10 (switched from 1.08 →
      1.09 → 1.10 on 2026-11-08); CLA signed, waiting on a community moderator. Once merged, later versions go in as
      ordinary update PRs. Put `winget install TetherWorks.IgnisDisc` back on
      the sites only once `winget show --id TetherWorks.IgnisDisc` works. *(low)* **[owner: watch the PR]**
- [x] Installer/WinGet publisher "Ignis Optical Engineering" → "tetherworks". 2026-11-08
- [x] WinGet `FileExtensions` removed: the installer registers no file types. 2026-11-08
- [ ] Code signing, so SmartScreen stops warning: repo policy (`SECURITY.md`) and GitHub-hosted release/signing workflow
      (`.github/workflows/release.yml`) prepared for [SignPath Foundation](https://signpath.org) (free for OSS; signs
      builds made by GitHub Actions once `SIGNPATH_ORG_ID` and `SIGNPATH_API_TOKEN` are configured). *(low)*
      **[owner: submit SignPath Foundation application & add repo secrets]**
- [x] Installer 149.2 MB → 71.2 MB with the same wizard: a .NET Framework 4.8 launcher (built into Windows) unpacks the
      app and runs the wizard on the app's own runtime; the installed uninstaller is 331 KB instead of a 149 MB copy.
      Uninstall now removes the folder completely and only touches its own shortcuts / Apps & Features entry.
      2026-11-08 (ships in v1.10).

### Single source of truth for facts
- [x] README lists the v1.09 features and the CLI accepts legacy images / `.cue` / playlists. 2026-11-08
- [x] Generate volatile facts from the release script instead of typing them by hand. Version, sizes and SHA-256 hashes
      on the site and in WinGet (`Update-Version.ps1`), plus the support table in both `README.md` and
      `web/public/index.html` generated from `docs/support-matrix.json` via `scripts/Sync-SupportTable.ps1` (checked in
      CI with `-CheckOnly`). 2026-11-08
- [x] Sites: aligned landing page strictly with v1.09 physical hardware test matrix, replaced static UI mockups with
      live build screenshots, self-hosted fonts, and enforced a strict Content-Security-Policy. Deployed 2026-11-08.

### GitHub (tether-works)
- [x] CI: `.github/workflows/ci.yml` builds Release (warnings as errors) and runs every test on `windows-latest` for
      each push and PR. 2026-11-08
- [x] Dependabot alerts and security fixes, secret scanning with push protection, and private vulnerability
      reporting turned on. 2026-11-08
- [x] SECURITY.md, CONTRIBUTING.md, issue forms (bug report; drive compatibility report). 2026-11-08

---

## P1 — Native drive-command writer (the big one)

Windows' burning service (IMAPIv2) cannot do several of ImgBurn's signature features. ImgBurn drove the drive itself
with SCSI MMC commands. IgnisDisc already reads discs this way; this epic adds **writing** this way, behind an opt-in
switch that is off by default, with IMAPIv2 remaining the default and the fallback.

Unlocks: CD-Text, test write (simulation), real Session-at-Once audio with audio in pre-gaps (INDEX 00), hidden track
one audio, pre-emphasis, ISRC/MCN in subcode, DVD±R DL layer-break control, overburning, DVD+R book type, and
eventually burning to several drives at once.

Groundwork that already exists: `MmcCommandBuilder` (including `SEND CUE SHEET`, 0x5D), SCSI pass-through,
TOC / CD-Text parsing, the virtual drive simulator.

**Safety rails (apply to every phase):** opt-in switch per burn, default off. Allow only drives whose capability probe
passed. Offer a test write first. Verify after burn is mandatory in native mode. Fall back to IMAPIv2 on any
unexpected sense code. Full SCSI log for every native burn. The virtual drive implements the same MMC write commands,
so every phase is tested without hardware before it touches a real disc.

- [x] **Phase 0 — Design gate.** Design in `docs/NATIVE-WRITER-DESIGN.md` (command sequence per write type,
      error handling and stop-before-the-laser fallback, buffer polling, exclusive access). First feature: audio CD
      Session-at-Once with CD-Text. Test drive (HL-DT-ST GP60NB50) and media (CD-RW, CD-R, DVD±R/RW, DVD±R DL) listed
      there. Fixed on the way: the probe reported CD-Text support for every SAO drive instead of reading the CD
      Mastering R-W bit; `drives --json` and the drive report now show `supportsNativeCdText`. 2026-11-08
- [x] **Phase 1 — Capability probe & Media ID / ATIP / ADIP inspection (read-only).** `GET CONFIGURATION` feature
      descriptors (TAO 0x002D, CD Mastering 0x002E, DVD-R/-RW Write 0x002F, Layer Jump 0x0033, BD Write 0x0041), Write
      Parameters mode page (0x05), and physical media Manufacturer ID / dye inspection (`READ TOC/PMA/ATIP` 0x43 format
      0x04 for CD-R/RW ATIP, `READ DISC STRUCTURE` 0xAD format 0x0E / 0x11 / 0x00 for DVD±R/RW and BD MID) via
      `MmcWriteCapabilityProbe` and `MmcMediaIdInspector`, shown per drive in Drive Utilities and `media-info --json` /
      `drives --json`. Writes nothing. The media ID is read only where it is shown (Drive Utilities, `media-info`,
      `drive-report`), not on every disc-status check. CD-R/RW names come from the Orange Book manufacturer list; the
      DVD-R ID is read from pre-pit fields 3 and 4. Not yet checked against real discs. 2026-11-08
- [ ] **Phase 2 — Test write.** Native Session-at-Once pipeline for audio CDs with the laser off (`MODE SELECT`
      write parameters with the Test Write bit read back before writing, `SEND CUE SHEET`, CD-Text lead-in,
      `WRITE(10)`, `SYNCHRONIZE CACHE`). Done: `NativeSaoAudioWriter` and `VirtualMmcDrive` (with fault injection),
      `ignisdisc-cli burn-audio --native --test-write`, a command log per run. Real writes are refused in code.
      Remaining: a test write on the GP60NB50 with a blank CD-R or CD-RW. *(med)* **[owner: insert a blank disc]**
- [ ] **Phase 3 — Audio CD Session-at-Once with CD-Text.** `SEND CUE SHEET` + lead-in CD-Text packs. Test on CD-RW
      first, then CD-R. *(high)* **[owner: hardware test]**
- [ ] **Phase 4 — Audio extras.** Audio inside pre-gaps (INDEX 00) and hidden track one audio, pre-emphasis flag,
      ISRC/MCN. Also enables bit-exact burning of single-file CUE albums with their real gaps. *(high)*
- [ ] **Phase 5 — DVD.** DVD±R DL layer-break selection (wire the existing DVD-Video layer-break picker to real drives),
      overburn with explicit warnings, DVD+R book type (drive-vendor commands, per-drive allow-list). *(high)*
- [ ] **Phase 6 — Throughput.** Burn to several drives at once; burn queue (IMAPIv2 single-drive burn queue completed
      in P2; multi-drive simultaneous writing remains). *(med)*

---

## P2 — Features competitors have

### Audio
- [ ] Rip to MP3 / Opus / AAC as well as WAV / FLAC (Windows has an AAC encoder; check licences before bundling MP3
      or Opus encoders). *(med)*
- [ ] Gap detection when ripping, writing INDEX 00 into the ripped `.cue`; hidden track one audio; pre-emphasis
      detection. *(med)*
- [ ] CUETools database (CTDB) verification and repair alongside AccurateRip. *(med)*
- [x] ReplayGain 2.0 tags and embedded cover art in FLAC ripped from the command line (`rip-audio --format flac`,
      `--cover <image>`). Loudness per ITU-R BS.1770-4 with gating, checked against the EBU Tech 3341 test signals;
      album gain gates all tracks' blocks together. 2026-11-08
- [ ] The same tags when ripping to FLAC from the app's rip screen. *(low)*

### Images, archival and diagnostics
- [ ] Mixed-mode CUE burning: a data track plus audio tracks currently burns only the audio tracks (`CueSheet.IsMixedMode`
      detection and UI notice in Audio CD Studio implemented; burning the Mode 1/2 data track alongside audio tracks
      remains open). *(med)*
- [x] Copying a mixed-mode / Enhanced CD: detect it from the TOC and say that only the audio tracks are copied.
      2026-11-08
- [ ] Copy both the audio and the data track of a mixed-mode / Enhanced CD. Needs the native writer (P1): IMAPIv2
      cannot write a data track and audio tracks in one pass, nor raw 2,352-byte sectors. *(high)*
- [x] Fixed: Raw Clone Mode burned the staged raw 2,352-byte sectors as if they were 2,048-byte data, producing an
      unreadable copy (since v1.08), and the v1.11 work had routed audio CDs into that path too. Raw mode now burns
      the extracted data and audio CDs always copy track by track. The virtual drive now returns real Mode 1 raw
      sectors, which is what hid the bug. 2026-11-08
- [x] Convert between image formats (`BIN/CUE ↔ ISO`, legacy `.nrg`/`.mdf`/`.ccd` → `BIN/CUE` as well as `→ ISO` via
      `LegacyDiscImageConverter.ConvertImageToBinCueAsync` and `ConvertBinCueToIsoAsync` + Image Studio UI). 2026-11-08
- [x] Self-contained, zero-dependency verification scripts (`VERIFY.BAT`, `VERIFY.PS1`, `README-RECOVERY.TXT`) written
      onto every spanned archive disc by `archive-stage` so discs can be verified 25+ years later using built-in OS
      tools without IgnisDisc installed. 2026-11-08
- [x] Export sector surface scan & transfer rate benchmark data to CSV (`ignisdisc-cli scan --csv <file.csv>` and
      *Export Scan CSV* in Drive Utilities). 2026-11-08
- [ ] DiscJuggler `.cdi` reading. *(low)*
- [ ] Real-hardware test of disc copy: single-drive swap prompt, multi-copy prompts, physical audio CD detection.
      *(low)* **[owner: hardware test]**

### Burning workflow
- [x] Burn queue (several data images one after another) on IMAPIv2 (`BurnQueueService` + Image Studio Burn Queue
      tab). Never replaces data on a rewritable disc, refuses audio `.bin` files and `.cue` sheets instead of burning
      them as data. 2026-11-08
- [x] After-burn actions: eject, play a sound, shut down (Windows' `shutdown /s /t 60`, cancellable with
      `shutdown /a`). They run after verification, and never after the user cancels a queue. 2026-11-08

### Blu-ray
- [ ] UDF 2.50 image builder (needed for BD-Video and the BD norm). *(med)*
- [ ] Test BD-R / BD-RE burning on real media before the site claims Blu-ray. *(low)* **[owner: hardware + media]**

### Platform and reach
- [ ] Windows on ARM64. Done: the app and CLI build for `win-arm64` in CI, and `Build-Installer.ps1 -Runtime
      win-arm64` packages them. Remaining: run it on an ARM64 PC before publishing an ARM64 download. *(low)*
      **[owner: ARM64 PC]**
- [x] Opt-in update check against GitHub Releases, disclosed on the site (`GitHubUpdateChecker`, default off in
      Settings, disclosed in `SECURITY.md`). 2026-11-08
- [x] Accessibility: screen-reader names (`AutomationProperties.Name`) on the controls of every studio view and
      `BurnTargetDialog`, checked by a visual-tree test. 2026-11-08
- [ ] Accessibility: check full keyboard navigation and a screen reader (Narrator) by hand. *(low)* **[owner]**
- [ ] Localization (resource files; start with the strings in dialogs and status messages). *(med)*
- [x] Opt-in, copy-paste drive compatibility report (model, firmware, capability probe, media ID / ATIP, results)
      for the drive-report issue form (`DriveCompatibilityReportBuilder`, Drive Utilities button,
      `ignisdisc-cli drive-report`). Leaves out the Windows device path, which can contain a USB serial number.
      2026-11-08
- [ ] Publish a compatibility list built from those reports. *(low)*

---

## P3 — Known technical debt (from the v1.08 audit)

- [x] WAV conversion streams through `RedBookPcmConverter` (band-limited, surround downmix) like FLAC.
      2026-11-08 (ships in v1.10).
- [x] TPDF dither when reducing to 16-bit; exact 16-bit audio and digital silence stay bit-exact, mono stays mono.
      2026-11-08 (ships in v1.10).
- [x] `SingleFileAlbumSource` keeps the decoded album (up to ~800 MB) in memory for as long as its tracks are in the
      compilation. Released automatically after burning (`ReleaseDecodedBuffer` / `ReleaseCachedAlbumBuffers`).
      2026-11-08
- [x] Audio preview decodes on a background thread and resets when the clip ends. 2026-11-08 (ships in v1.10).
- [x] `AudioCdTrackItem.DurationFrames` can decode a whole album synchronously when the duration is unknown (fixed via
      fast slice/header duration lookup in `AudioCdProject`). 2026-11-08
- [ ] Check the FLAC test encoder and the Nero pre-gap detection against files made by real tools (`flac.exe`, Nero).
      Our tests encode our own reading of those formats. *(low)*
- [x] 32-bit FLAC with stereo decorrelation is rejected with a clear message rather than decoded (`FlacFileCodec` throws
      descriptive `InvalidDataException` on mid-side / side stereo 32-bit frames). 2026-11-08

---

## Decisions log

| Date | Decision |
|---|---|
| 2026-11-08 | Sites state only verified facts; limitations are listed openly in the support table. |
| 2026-11-08 | No more slipstreaming: every rebuild gets a new version number. |
| 2026-11-08 | Direct drive writing will be opt-in and default off; IMAPIv2 stays the default and the fallback. |
| 2026-11-08 | v1.09 released as a new version rather than replacing v1.08's files. |
| 2026-11-08 | Installer shrink keeps the existing wizard (launcher + shared runtime) rather than switching to Inno Setup. |
| 2026-11-08 | Agent prompts, skills and loop scripts live in a private repository; the public repo holds only the product. |
| 2026-11-08 | Blu-ray is listed as not supported until the app recognises BD media and it has been tested on real discs. |
| 2026-11-08 | The native writer's first feature is audio CD Session-at-Once with CD-Text; its test write comes first. |
