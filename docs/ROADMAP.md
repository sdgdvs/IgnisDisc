# IgnisDisc Roadmap

The goal: what ImgBurn would be today if it had never been abandoned — and the obvious choice on Windows for anyone
burning or ripping CDs and DVDs.

**How to use this file.** It is the single shared engineering roadmap and release checklist for IgnisDisc.

- Tick a box (`- [x]`) in the same commit that finishes the work, and add the date and short commit hash.
- Add new problems where they belong instead of starting another list.
- Anything that needs the owner (hardware, accounts, money, decisions) is marked **[owner]**.
- Risk: **low** = no chance of ruining discs or user data; **med** = new code paths users will hit; **high** = can
  turn discs into coasters or needs real-hardware testing to prove.

Last reviewed: 2026-11-08 (v1.08 pre-release audit, `f4dff8d`).

---

## P0 — Trust, correctness and release hygiene (low risk, do first)

### Releases
- [x] Ship the audited features (FLAC/CUE albums, disc copy, legacy images, playlists, crossfade, cover printing) as
      **v1.09**. Released 2026-11-08 (`4bd267e`, tag `v1.09`); installer smoke-tested, downloaded hashes match the site.
- [x] Stop slipstreaming: `Update-Version.ps1` and `Publish-ReleaseAndSite.ps1` refuse already-published versions;
      release notes come from `docs/release-notes/v<version>.md`. 2026-11-08 (`4bd267e`)
- [ ] Get `TetherWorks.IgnisDisc` into WinGet: microsoft/winget-pkgs#474307 now submits v1.10 (switched from 1.08 →
      1.09 → 1.10 on 2026-11-08); CLA signed, waiting on a community moderator. Once merged, later versions go in as
      ordinary update PRs. Put `winget install TetherWorks.IgnisDisc` back on
      the sites only once `winget show --id TetherWorks.IgnisDisc` works. *(low)* **[owner: watch the PR]**
- [x] Installer/WinGet publisher "Ignis Optical Engineering" → "tetherworks". 2026-11-08 (`4bd267e`)
- [x] WinGet `FileExtensions` removed: the installer registers no file types. 2026-11-08 (`4bd267e`)
- [ ] Code signing, so SmartScreen stops warning: SignPath Foundation (free for OSS; signs builds made by CI, which
      now exists) or Azure Artifact Signing (~$10/month; check eligibility). *(low)* **[owner: apply / account setup]**
- [x] Installer 149.2 MB → 71.2 MB with the same wizard: a .NET Framework 4.8 launcher (built into Windows) unpacks the
      app and runs the wizard on the app's own runtime; the installed uninstaller is 331 KB instead of a 149 MB copy.
      Uninstall now removes the folder completely and only touches its own shortcuts / Apps & Features entry.
      2026-11-08 (`cdefdea`), ships in v1.10.

### Single source of truth for facts
- [x] README lists the v1.09 features and the CLI accepts legacy images / `.cue` / playlists. 2026-11-08 (`4bd267e`)
- [ ] Generate volatile facts from the release script instead of typing them by hand. Done: version, sizes and
      SHA-256 hashes on the site and in WinGet (`Update-Version.ps1`). Remaining: the support table is still kept by
      hand in both the README and the site. *(low)*
- [x] Sites: aligned landing page strictly with v1.09 physical hardware test matrix, replaced static UI mockups with
      live build screenshots, self-hosted fonts, and enforced a strict Content-Security-Policy. Deployed 2026-11-08.

### GitHub (tether-works)
- [x] CI: `.github/workflows/ci.yml` builds Release (warnings as errors) and runs every test on `windows-latest` for
      each push and PR. 2026-11-08 (`673a27f`)
- [x] Dependabot alerts and security fixes, secret scanning with push protection, and private vulnerability
      reporting turned on. 2026-11-08
- [x] SECURITY.md, CONTRIBUTING.md, issue forms (bug report; drive compatibility report). 2026-11-08 (`673a27f`)

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

- [ ] **Phase 0 — Design gate.** Write the design (command sequence per write type, error/sense handling, buffer and
      underrun handling, exclusive-access coexistence with IMAPIv2). Pick the 80/20 first feature. List the drives
      and media available for testing. *(low)* **[owner: decision + drive list]**
- [ ] **Phase 1 — Capability probe (read-only).** `GET CONFIGURATION` feature descriptors (TAO 0x002D, CD Mastering
      0x002E, DVD-R/-RW Write 0x002F, Layer Jump 0x0033, BD Write 0x0041) and the Write Parameters mode page (0x05),
      shown per drive in Drive Utilities and `media-info --json`. Writes nothing. *(low)*
- [ ] **Phase 2 — Test write.** Full native write pipeline (`MODE SELECT` write parameters with the Test Write bit,
      `WRITE(10)`, `SYNCHRONIZE CACHE`, `CLOSE TRACK/SESSION`) with the laser off. Proves the pipeline end to end at
      zero risk to media. *(med)* **[owner: run on real drives]**
- [ ] **Phase 3 — Audio CD Session-at-Once with CD-Text.** `SEND CUE SHEET` + lead-in CD-Text packs. Test on CD-RW
      first, then CD-R. *(high)* **[owner: hardware test]**
- [ ] **Phase 4 — Audio extras.** Audio inside pre-gaps (INDEX 00) and hidden track one audio, pre-emphasis flag,
      ISRC/MCN. Also enables bit-exact burning of single-file CUE albums with their real gaps. *(high)*
- [ ] **Phase 5 — DVD.** DVD±R DL layer-break selection (wire the existing DVD-Video layer-break picker to real drives),
      overburn with explicit warnings, DVD+R book type (drive-vendor commands, per-drive allow-list). *(high)*
- [ ] **Phase 6 — Throughput.** Burn to several drives at once; burn queue (could also run on IMAPIv2 earlier — see P2).
      *(med)*

---

## P2 — Features competitors have

### Audio
- [ ] Rip to MP3 / Opus / AAC as well as WAV / FLAC (Windows has an AAC encoder; check licences before bundling MP3
      or Opus encoders). *(med)*
- [ ] Gap detection when ripping, writing INDEX 00 into the ripped `.cue`; hidden track one audio; pre-emphasis
      detection. *(med)*
- [ ] CUETools database (CTDB) verification and repair alongside AccurateRip. *(med)*
- [ ] ReplayGain tags and embedded cover art in ripped FLAC. *(low)*

### Images and copying
- [ ] Mixed-mode CUE burning: a data track plus audio tracks currently burns only the audio tracks. *(med)*
- [ ] Copying a mixed-mode / Enhanced CD keeps only the audio unless Raw Clone mode is on; detect it and say so, or
      copy both. *(med)*
- [ ] Convert between image formats (BIN/CUE ↔ ISO, legacy formats → BIN/CUE, not only → ISO). *(low)*
- [ ] DiscJuggler `.cdi` reading. *(low)*
- [ ] Real-hardware test of disc copy: single-drive swap prompt, multi-copy prompts, physical audio CD detection.
      *(low)* **[owner: hardware test]**

### Burning workflow
- [ ] Burn queue (several images one after another) on IMAPIv2. *(med)*
- [ ] After-burn actions: eject, play a sound, shut down. *(low)*

### Blu-ray
- [ ] UDF 2.50 image builder (needed for BD-Video and the BD norm). *(med)*
- [ ] Test BD-R / BD-RE burning on real media before the site claims Blu-ray. *(low)* **[owner: hardware + media]**

### Platform and reach
- [ ] Windows on ARM64 build (IMAPIv2 exists on ARM64). *(low)*
- [ ] Opt-in update check against GitHub Releases, disclosed on the site. *(low)*
- [ ] Accessibility: screen-reader names (`AutomationProperties`) and full keyboard navigation in the WPF app. *(low)*
- [ ] Localization (resource files; start with the strings in dialogs and status messages). *(med)*
- [ ] Opt-in, copy-paste drive compatibility report (model, firmware, capability probe, results) feeding a public
      compatibility list. *(low)*

---

## P3 — Known technical debt (from the v1.08 audit)

- [x] WAV conversion streams through `RedBookPcmConverter` (band-limited, surround downmix) like FLAC.
      2026-11-08 (`3d91df2`), ships in v1.10.
- [x] TPDF dither when reducing to 16-bit; exact 16-bit audio and digital silence stay bit-exact, mono stays mono.
      2026-11-08 (`3d91df2`), ships in v1.10.
- [ ] `SingleFileAlbumSource` keeps the decoded album (up to ~800 MB) in memory for as long as its tracks are in the
      compilation. Release it after burning, or decode slices on demand. *(med)*
- [x] Audio preview decodes on a background thread and resets when the clip ends. 2026-11-08 (`3d91df2`), ships in v1.10.
- [ ] `AudioCdTrackItem.DurationFrames` can decode a whole album synchronously when the duration is unknown. *(low)*
- [ ] Check the FLAC test encoder and the Nero pre-gap detection against files made by real tools (`flac.exe`, Nero).
      Our tests encode our own reading of those formats. *(low)*
- [ ] 32-bit FLAC with stereo decorrelation is rejected with a clear message rather than decoded. *(low)*

---

## Decisions log

| Date | Decision |
|---|---|
| 2026-11-08 | Sites state only verified facts; limitations are listed openly in the support table. |
| 2026-11-08 | No more slipstreaming: every rebuild gets a new version number. |
| 2026-11-08 | Direct drive writing will be opt-in and default off; IMAPIv2 stays the default and the fallback. |
| 2026-11-08 | v1.09 released as a new version rather than replacing v1.08's files. |
| 2026-11-08 | Installer shrink keeps the existing wizard (launcher + shared runtime) rather than switching to Inno Setup. |
