# Native drive-command writer: design proposal

**Status:** partly built. What exists:

- the read-only capability probe (`MmcWriteCapabilityProbe`, `MmcMediaIdInspector`);
- the audio CD Session-at-Once test write (Phase 2) in `src/CdBurner.Hardware/NativeWriting/`, reached only through
  `ignisdisc-cli burn-audio --native --test-write`. `NativeSaoAudioWriter` refuses a request without the test-write
  flag, so this version cannot burn a disc natively. It has run against the virtual drive (`VirtualMmcDrive`), not
  yet on a real drive.

Phases 3–6 below are still plans.

**First feature (decided 2026-11-08):** audio CD Session-at-Once with CD-Text. Phase 2 (test write) is built on that
path, so the first laser-off test write is an audio CD with CD-Text, not a data CD.

## Why

IgnisDisc burns through Windows' IMAPIv2 service. IMAPIv2 cannot:

- write CD-Text into the lead-in of an audio CD;
- do a test write (laser off);
- write audio inside pre-gaps (INDEX 00), hidden-track-one audio, the pre-emphasis flag, or ISRC / MCN in the subcode;
- set the layer break of a DVD±R DL disc, overburn, or change the DVD+R book type.

ImgBurn and cdrtools do these by sending SCSI MMC commands to the drive themselves. IgnisDisc already reads discs this
way (`Win32ScsiTransport`, `IOCTL_SCSI_PASS_THROUGH_DIRECT`); this proposal adds writing.

## Rules

1. **Off by default.** Native writing is chosen per burn (app option, CLI `--native`). IMAPIv2 stays the default.
2. **Probed drives only.** The drive must report the needed feature: CD Mastering (0x002E) with SAO for audio
   Session-at-Once, plus its R-W bit for CD-Text (`supportsNativeCdText` in `drives --json`); Track-at-Once (0x002D)
   for TAO; DVD-R/-RW Write (0x002F) or the DVD+R feature for DVD.
3. **Test write first.** For write-once media the app offers a test write (Test Write bit in mode page 0x05) before
   the real burn, on drives that report it.
4. **Verify after every native burn.** Not optional in native mode.
5. **Stop before the laser starts.** Any error during setup (exclusive access, `MODE SELECT`, `SEND CUE SHEET`) aborts
   and offers the IMAPIv2 burn instead. Errors after writing has begun fail the burn; they are never retried silently.
6. **Log everything.** Every CDB, transfer length, status and sense key/ASC/ASCQ goes into a log the user can save and
   attach to a bug report.
7. **Simulator first.** The virtual drive gets the same write commands, so each phase passes its tests there before
   it runs on a real disc.

## Command sequences

**Common setup:** take exclusive access (`IOCTL_CDROM_EXCLUSIVE_ACCESS`, so Windows and IMAPIv2 keep out), `TEST UNIT
READY`, `GET CONFIGURATION` (current profile), `READ DISC INFORMATION` (blank? appendable?), `MODE SENSE` page 0x05.

**Data CD, Track-at-Once:** `MODE SELECT` page 0x05 (write type TAO, data block type Mode 1, multisession and test
write as chosen) → `READ TRACK INFORMATION` (next writable address) → `WRITE(10)` in 32 KB–64 KB chunks →
`SYNCHRONIZE CACHE` → `CLOSE TRACK/SESSION` (close track, then close session or finalize).

**Audio CD, Session-at-Once with CD-Text:**

1. `MODE SELECT` page 0x05: write type 2 (SAO), Test Write bit as chosen, BUFE on when supported, multi-session 00
   (close the disc).
2. `SEND CUE SHEET` (0x5D). One 8-byte entry per index (CTL/ADR, track, index, data form, SCMS, absolute MSF): a
   lead-in entry (track 0), track 1 INDEX 00 at 00:00:00 and INDEX 01 at 00:02:00, then every track's INDEX 00 / 01,
   then the lead-out (track 0xAA). The CTL nibble carries pre-emphasis and copy-permit; ADR 2 / 3 entries carry MCN
   and ISRC. With CD-Text, the lead-in entry's data form says the host sends R-W subcode for the lead-in.
3. With CD-Text, write the lead-in: CD-Text packs are 18 bytes (4 header, 12 text, 2 CRC); four packs fill one
   sector's 96 bytes of raw R-W subcode, 6 bits per byte. The pack set repeats from the lead-in start address in
   `READ DISC INFORMATION` (bytes 17–19, as cdrdao reads it) up to LBA −151. Start MSF 9x:ss:ff converts to LBA as
   `(m × 60 + s) × 75 + f − 450,150`, which is negative. Mode page 0x05 asks for data block type 3 (raw with raw
   P-W) while a CD-Text lead-in is written; if the drive refuses that, block type 0 is tried.
4. `WRITE(10)` of 2,352-byte audio sectors from LBA −150 (the 2-second pre-gap of track 1) to the end of the last
   track.
5. `SYNCHRONIZE CACHE`. The drive writes the lead-out and closes the session itself.

The cue sheet data form values (0x00 audio, 0x01 drive-generated lead-in/lead-out, 0x41 lead-in with host R-W),
the pack-to-subcode packing and the lead-in addressing were checked against cdrdao's generic MMC driver. Drives that
reject the standard cue sheet get cdrdao's fallbacks: ISRC entries without control bits, and a CD-Text lead-in entry
without its start time.

**Safety checks before the first WRITE** (any failure stops with nothing written): disc profile is CD-R or CD-RW;
the CD Mastering feature is current and offers SAO, test write and (for CD-Text) R-W; the disc is blank; the audio
ends before the disc's last possible lead-out; the drive accepts the write parameters and, read back with MODE
SENSE, reports Session-at-Once and the requested test-write setting; the drive accepts a cue sheet. After a test
write the disc must still read as blank, or the drive is reported as not honouring test writes.

**DVD±R:** `RESERVE TRACK` (DVD-R DAO) or sequential writing (DVD+R) → `WRITE(10)` → `SYNCHRONIZE CACHE` →
`CLOSE TRACK/SESSION`. Dual layer adds `SEND DISC STRUCTURE` with the layer-break address before writing.

## Buffer and underrun handling

Poll `READ BUFFER CAPACITY` (0x5C) while writing to show the drive buffer and keep the host-side queue full. Turn on
BURN-Free / buffer-underrun protection (BUFE bit in page 0x05) when the drive supports it.

## Errors

| Sense | Meaning | Action |
|---|---|---|
| 2/04/xx not ready | still spinning up | wait and retry `TEST UNIT READY`, then give up |
| 5/xx/xx illegal request during setup | drive rejects the parameters | abort before writing; offer IMAPIv2 |
| 3/0C/xx write error | media or drive fault | fail the burn, keep the log |
| 6/28/00 media changed | disc swapped | abort |
| anything else after writing started | unknown | fail the burn, keep the log |

## Testing

- Virtual drive: each command is implemented in `VirtualDiscBackend`, including failure injection (sense codes at
  chosen LBAs), so the error paths are tested.
- Real hardware, in order: test write on CD-RW, real write on CD-RW, then CD-R; DVD after CD.

## Test hardware

| Drive | Firmware | Connection | Probe result (`drives --json`, 2026-11-08) |
|---|---|---|---|
| HL-DT-ST DVDRAM GP60NB50 | RF02 | USB, slim tray | TAO, SAO, raw DAO, CD-Text (R-W), test write, BURN-Free, layer jump |

Media on hand: CD-RW, CD-R, DVD±R / DVD±RW, DVD±R DL.

The GP60NB50 has a spring-loaded slim tray that cannot close itself: automated runs never eject it, and the owner
swaps discs by hand. Drives added later get a row here with their probe result.
