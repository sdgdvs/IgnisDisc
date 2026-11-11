# Native drive-command writer: design proposal

**Status:** partly built. What exists:

- the read-only capability probe (`MmcWriteCapabilityProbe`, `MmcMediaIdInspector`);
- audio CD Session-at-Once in `src/CdBurner.Hardware/NativeWriting/`: test write and real write
  (`NativeSaoAudioWriter`), the read-back check that follows every real write (`NativeSaoVerifier`), CD-Text in the
  lead-in, ISRC and catalog codes, pre-emphasis and copy bits, audio in pre-gaps (INDEX 00) and in front of track 1.
  Reached through `ignisdisc-cli burn-audio --native` and the "Direct drive commands" option on the app's Audio CD
  screen; both are off by default.
- data discs: one Mode 1 track Track-at-Once on a blank CD-R/CD-RW, and one data track on a blank single-layer
  DVD-R, DVD-RW (sequential) or DVD+R (`NativeTaoDataWriter`, DVD half in `NativeTaoDataWriter.Dvd.cs`), through
  `burn-iso` / `burn-data --native` and the Data Disc and Disc Image screens. The DVD path has run only against the
  virtual drive.
- Exclusive access opens the drive by its device interface path (`\\?\…{53f56308-…}`): the cdrom driver refuses
  the lock (ERROR_INVALID_HANDLE) on a handle opened by drive letter, because that handle belongs to the mounted
  file system.

**What has run where.** Everything above passes against the virtual drive (`VirtualMmcDrive`). On real hardware
(see Test hardware) the test write has run on CD-R and CD-RW, and two real audio writes have run and read back
identical: two tracks with CD-Text on a CD-RW, and four tracks on a CD-R with CD-Text, catalog and ISRC codes,
pre-emphasis and copy bits, audio in pre-gaps and audio in front of track 1. One drive model only; no disc has
been played in a stand-alone CD player yet.

Phases 5–6 below are still plans.

**First feature (decided 2026-11-08):** audio CD Session-at-Once with CD-Text.

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
3. **Test write on offer.** A test write (Test Write bit in mode page 0x05) can be run on its own, or first in the
   same burn (`--test-first`, "Test write first"), on drives that report it. It is not forced: see "After a test
   write" for why most drives cannot go straight on to the real write.
4. **Verify after every native burn.** Not optional in native mode: `NativeSaoVerifier` reads the table of contents
   (track starts, lead-out, pre-emphasis and copy bits), the CD-Text packs and all the audio back and compares them.
   A catalog number and ISRC codes are read with `READ SUB-CHANNEL` (formats 2 and 3) and compared; a code that is
   missing or different fails the check, and a drive that refuses the command is reported as "not checked".
   Audio reads back shifted by the drive's write and read offsets, so the check first finds that shift (searching
   up to 2,352 samples either way from a stretch of real signal) and then compares every sample under it.
5. **Stop before the laser starts.** Any error during setup (exclusive access, `MODE SELECT`, `SEND CUE SHEET`) aborts
   and offers the IMAPIv2 burn instead. Errors after writing has begun fail the burn; they are never retried silently.
6. **Log everything.** Every CDB, transfer length, status and sense key/ASC/ASCQ goes into a log the user can save and
   attach to a bug report.
7. **Simulator first.** The virtual drive gets the same write commands, so each phase passes its tests there before
   it runs on a real disc.

## Command sequences

**Common setup:** take exclusive access (`IOCTL_CDROM_EXCLUSIVE_ACCESS`, so Windows and IMAPIv2 keep out), `TEST UNIT
READY`, `GET CONFIGURATION` (current profile), `READ DISC INFORMATION` (blank? appendable?), `MODE SENSE` page 0x05.

**Data CD, Track-at-Once** (built 2026-11-10, `NativeTaoDataWriter`, `NativeDataBurnEngine`): `GET CONFIGURATION`
RT=2 feature 0x002D (profile CD-R/CD-RW, Track at Once current, test write offered) → `READ DISC INFORMATION`
(status must be blank) → `READ TRACK INFORMATION` track 1 (next writable address must be valid and 0; free blocks
must hold the image, padded to at least 300 sectors, plus 2 run-out blocks; without an answer the last possible
lead-out is used) → `MODE SELECT` page 0x05 (write type 1, track mode 4, data block type 8 = 2,048-byte Mode 1,
multi-session 00 to close the disc or 11 to leave it open, test write as chosen), read back with `MODE SENSE` →
`GET PERFORMANCE` type 3 (the drive's write speeds for this disc) and `SET CD SPEED` with the fastest, or the
user's choice rounded down to a listed speed → `WRITE(10)` of 31 sectors (63,488 bytes) at a time from the next
writable address → `SYNCHRONIZE CACHE` (the track closes) → `CLOSE TRACK/SESSION` close function 010b with Immed,
then `TEST UNIT READY` until the drive is ready → `READ TOC` format 0 (one data track from LBA 0). No `CLOSE TRACK`
is sent: on CD the flush closes a Track-at-Once track. The read-back check is `DataBurnService`'s: `READ (10)` with
FUA over every sector, compared with the image. A test write skips the close and must leave sector 0 unreadable. A
disc that is not blank is never erased by the writer: an image for a second session is built to follow the first
one, so those discs go to the Windows writer until multi-session is built.

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
SENSE, reports Session-at-Once and the requested test-write setting; the drive accepts a cue sheet.

**After a test write** the disc must be unwritten. A blank disc status settles it. Some drives (the GP60NB50) report
the simulated session as an open, incomplete session until the disc is reloaded; then `READ CD` of LBA 0 must fail.
A closed disc, or audio readable at LBA 0, means the drive recorded: the run fails and says so.

While it shows that simulated session, the GP60NB50 rejects the real cue sheet with 5/30/06 (measured 2026-11-10).
A spindle stop and start, a load command on the closed tray, a sleep and wake cycle, and a quick blank of a CD-RW
all leave the state unchanged; only reloading the disc clears it. So a burn with a test write first stops after
the test with a message to reload the disc and burn again without the test. Nothing is ejected automatically:
slim drives cannot close their tray. On drives with a motorized tray an automatic reload would be possible; that
is not built.

**Pre-gap audio and flags.** A track's cue sheet entries carry its control bits (0x10 pre-emphasis, 0x20 digital
copy permitted); the lead-in, catalog and lead-out entries carry plain audio control. A pre-gap may end in audio
from the track's own stream (its INDEX 00 area). Track 1's first 150 sectors (before LBA 0) are always silence;
audio after them and before INDEX 01 is hidden audio that players reach by rewinding. From a single-file `.cue`
album the bytes on the disc stay in file order; only the track marks move.

**Data DVD** (built 2026-11-11, simulator only so far). Sequences from libburn's cookbook (libburnia,
`doc/cookbook.txt`); `SET STREAMING` layout from libburn's `mmc.c`. Every case starts with `GET CONFIGURATION`
(current profile), `READ DISC INFORMATION` (status must be blank) and `READ TRACK INFORMATION` track 1 (next writable
address must be 0, free blocks must hold the image). The image is padded with zeros to whole 16-sector (32 KiB) ECC
blocks and sent 32 sectors (two blocks, 64 KiB) per `WRITE (10)`, or 16 where the adapter refuses 64 KiB. Speed: the drive's `GET PERFORMANCE` list for the disc, set with
`SET STREAMING` (one 28-byte performance descriptor: end LBA = last sector of the disc, read and write size = the
speed in kB over 1,000 ms), then `SET CD SPEED` if the drive refuses that.

- *DVD-R / DVD-RW (sequential), disc closed: Disc-At-Once.* Feature 0x002F current (its test-write and BUFE bits are
  used) → `MODE SELECT` page 0x05: write type 2, LS_V 0, link size 0, multi-session 00, FP 0, track mode 5, data block
  type 8, packet size 0; read back → `SET STREAMING` → `RESERVE TRACK` (ARSV 0, RMZ 0, size in bytes 5–8 = padded
  sectors) → `WRITE (10)` from LBA 0 → `SYNCHRONIZE CACHE`. No close command: the drive closes the disc. The
  reservation is recorded on the disc, so from `RESERVE TRACK` on a failure is reported as a damaged disc, not as
  untouched.
- *DVD-R / DVD-RW, left open (`--no-close`): incremental.* Features 0x002F and 0x0021 current → page 0x05: write
  type 0, LS_V 1, link size 16, multi-session 11b, FP 1, packet size 16, track mode from `READ TRACK INFORMATION`
  byte 5 (normally 5), data block type 8 → `WRITE (10)` → `SYNCHRONIZE CACHE` → `CLOSE TRACK` (001b, last track in
  last session from `READ DISC INFORMATION` bytes 6 and 11) → `CLOSE SESSION` (010b).
- *DVD+R.* No mode page (the cookbook: none is sent for DVD+R) and no test write → `WRITE (10)` → `SYNCHRONIZE
  CACHE` → `CLOSE TRACK` (001b) → finalize (101b, "minimal radius") for a closed disc, or `CLOSE SESSION` (010b) to
  leave it appendable. The cookbook reports drives that refuse 101b; then the session is closed with 010b and the
  result says the disc is still appendable.
- A drive that answers 2/04/xx to a close command is waited out and asked again (up to three times), as growisofs
  does for some drives.
- *DVD+RW* (built 2026-11-11, simulator only so far; `NativeTaoDataWriter.DvdPlusRw.cs`). No mode page, no test
  write, nothing to reserve. `READ DISC INFORMATION` byte 7 bits 0–1 give the background format status. A disc never
  formatted (0) gets `FORMAT UNIT` (CDB byte 1 0x11; list: byte 1 FOV | Immed = 0x82, byte 3 = 8, blocks
  0xFFFFFFFF, byte 8 = 26h << 2; libburn's `mmc_format_unit`) and is written while the drive formats in the
  background. Size from `READ FORMAT CAPACITIES`. A disc with a volume descriptor at sector 16 (`CD001`, `BEA01`,
  `NSR02/03`) is left alone unless the user allowed replacing its data. `WRITE (10)` from LBA 0, 64 KiB at a time
  (2 KiB alignment is enough here) → `SYNCHRONIZE CACHE` → `CLOSE SESSION` (010b), which stops a background format
  and makes the disc readable elsewhere; a refusal is logged, not fatal. Sectors past a shorter new image keep their
  old contents, as with any overwrite; the new file system ends before them. `DataBurnService` now verifies from
  sector 0 whenever an overwrite-only disc's data is replaced.
- *DVD-RW in restricted overwrite mode and DVD-RAM* (built 2026-11-11, simulator only; shared with DVD+RW in
  `NativeTaoDataWriter.Overwritable.cs`). DVD-RW: 32 KiB alignment, so the image is padded to whole 16-sector
  blocks. `READ FORMAT CAPACITIES` decides the state (cookbook): formatted with format 13h or 11h still offering
  blocks = partly formatted, expanded first with `FORMAT UNIT` type 13h (list byte 1 0x82, byte 3 = 8, blocks 0,
  byte 8 = 13h << 2, byte 11 = 16, the packet size libburn sets for types 10h–15h), which leaves the intermediate
  state; no current capacity = intermediate already. `CLOSE SESSION` (010b) after writing ends the intermediate
  state. DVD-RAM: must be formatted (an unformatted one is refused rather than formatted with defect management
  here); 2 KiB alignment; no close command. Both: no test write, same leave-to-replace rule. The size is the format
  capacity; the track size is used only when the drive reports no format information.
- Dual-layer DVD±R goes to the Windows writer with a stated reason. Dual layer will add `SEND DISC STRUCTURE` with the layer-break address before writing.

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
- Real hardware, in order: test write on CD-RW (done), real write on CD-RW (done), then CD-R (done); DVD after CD:
  a DVD-R test write, then a real DVD-R, DVD+R and DVD-RW (not run yet).

## Test hardware

| Drive | Firmware | Connection | Probe result (`drives --json`, 2026-11-08) |
|---|---|---|---|
| HL-DT-ST DVDRAM GP60NB50 | RF02 | USB, slim tray | TAO, SAO, raw DAO, CD-Text (R-W), test write, BURN-Free, layer jump |

Results:

| Date | Drive | Disc | Run | Result |
|---|---|---|---|---|
| 2026-11-08 | GP60NB50 | CD-R, FORNET (ATIP 97:26:07) | Test write, 2 tracks, CD-Text | Write parameters, cue sheet and all 64 WRITEs accepted. Drive showed the simulated session until reload; after reload the disc read as blank with all 359,844 sectors free. |
| 2026-11-10 | GP60NB50 | CD-RW, Mitsubishi (ATIP 97:34:23), quick-erased | Test write, 2 tracks of 20 s, CD-Text | All WRITEs accepted, 14,077 sectors in 46 s. LBA 0 unreadable afterwards: nothing recorded. Drive showed the simulated session (status 1, lead-out 00:42:00). |
| 2026-11-10 | GP60NB50 | same CD-RW, not reloaded | Attempts to clear the simulated session | Stop/start, load, sleep/wake and BLANK (minimal) all left status 1. |
| 2026-11-10 | GP60NB50 | same CD-RW, not reloaded | Real write attempted over the simulated session | Mode page accepted (SAO, test write off, block type 3); SEND CUE SHEET rejected twice with 5/30/06. Stopped before any WRITE; disc unchanged. |
| 2026-11-10 | GP60NB50 | same CD-RW, reloaded (blank again, status 0) | Real write, 2 tracks of 20 s, CD-Text, no test write | Cue sheet accepted, 135 WRITEs, 14,077 sectors with the CD-Text lead-in; closing the session took 20 s. Read-back check passed: table of contents, 9 CD-Text packs and 1,764,000 audio samples identical, combined drive offset 0 samples. 67 s in all. A separate `rip-audio` of the disc gave files identical to the sources and took its names from CD-Text. Disc reports one closed session of 3,000 sectors. |
| 2026-11-10 | GP60NB50 | same CD-RW, quick-erased | Data, Track-at-Once, 24 MB ISO (12,237 sectors), no test write | Feature 0x002D current, track 1 NWA 0 with 359,847 free; mode page TAO / track mode 4 / block type 8 confirmed; GET PERFORMANCE listed 1,764 kB/s and SET CD SPEED 10x was accepted. WRITEs 24 s, SYNCHRONIZE CACHE 5 s, CLOSE SESSION with Immed about 30 s of 2/04/07. TOC: one data track, lead-out at 12,239 (2 run-out blocks). FUA read-back of every sector passed. 61 s in all. |
| 2026-11-10 | GP60NB50 | same CD-RW, quick-erased | Data, Track-at-Once, 690 MB ISO (352,787 sectors) | Data written in 477 s (9.9x average), with close 8 min 52 s; FUA read-back of every sector passed (7 min 15 s). The same image through IMAPI on the same disc and drive the same day took 12 min 42 s to write. |
| 2026-11-11 | GP60NB50 | blank Verbatim DVD-R (MCC 03RG20) | Data, Disc-At-Once, test write only, 372 MB (181,648 sectors) | Features 0x002D/0x002F current; mode page write type 2 / track mode 5 / block type 8 with the test-write bit confirmed; SET STREAMING 8x (11,080 kB/s) and RESERVE TRACK accepted. The first WRITEs answered 2/04/08 for about 30 s, then 11,353 WRITEs of 16 sectors at about 6.3 ms each (2.5x overall with the start-up). SYNCHRONIZE CACHE 122 s. Afterwards the drive reported a closed disc (status 2) with one 181,648-sector track, but sectors 0, 16 and 100,000 all failed with 3/11/00: nothing was recorded. The writer then judged the disc recorded, because it expected the simulated open session (status 1) this drive shows after a CD test write; fixed to read the sectors in both cases. WRITEs are now 32 sectors (64 KiB). |
| 2026-11-11 | GP60NB50 | same DVD-R, after a reload | Same test write, 32 sectors (64 KiB) per WRITE | After the reload the disc read blank again: status 0, track 1 blank with next writable address 0, reads 5/21/00. Free blocks reported 2,298,496 against 2,297,888 before the first test write (608 more; not explained). The test write passed and the writer now reports "the disc was not written" with the reload message. 5,677 WRITEs at about 12.7 ms each: twice the data per command took twice as long, so the drive paces the transfer to the disc, not the bus. Throughput rose with the radius from 3.46x (LBA 9,312) to 3.92x (LBA 148,960), the shape of a constant-angular-velocity write that reaches 8x only near the outer edge; one 3.4 s pause in the middle. 2/04/08 for 33 s at the start, SYNCHRONIZE CACHE 137 s, 249 s in all. |
| 2026-11-11 | GP60NB50 | same DVD-R, after a reload | Data, Disc-At-Once, **real write**, 372 MB (181,648 sectors, 1,005 files), 8x setting, disc closed | Disc blank before (status 0, NWA 0). Same sequence as the test writes without the test-write bit: RESERVE TRACK, 5,677 WRITEs of 64 KiB, SYNCHRONIZE CACHE 122 s; the drive closed the disc itself. Data sent in 117 s with the drive's start-up; 239 s for the writer in all. Afterwards: status 2, one track of 181,648 sectors, TOC one data track; `READ (10)` of LBA 16 gives the ISO 9660 descriptor. The FUA read-back of every sector passed (75 s). Independent check: Windows mounted the disc as UDF, label IGNISDVDR, and all 1,005 files hashed (SHA-256) identical to the source, none missing or extra. `media-info` then named the disc "CD-ROM": its fallback for discs IMAPI will not describe guessed the type from the size. Fixed the same day: it now takes the drive's current profile, which for this closed DVD-R is DVD-ROM (0x0010, the only current profile, read with GET CONFIGURATION), so it shows "DVD-ROM". |
| 2026-11-11 | GP60NB50 | used Verbatim DVD+RW (MKM-A02-00), formatted, holding a 31,055-sector test volume | Data, overwrite, real write, 372 MB (181,648 sectors, 1,005 files), 4x setting | First a burn without `--overwrite`: refused before any drive command that writes, the old volume intact. Then with `--overwrite`: background format status 3, so no FORMAT UNIT; SET STREAMING 4x accepted; 5,677 WRITEs of 64 KiB at about 19.6 ms each with no busy answers, a flat 2.1–2.4x (the drive's other listed speed is 2.4x; the Windows writer ran this disc at about 2.3x the day before); SYNCHRONIZE CACHE 0.2 s; CLOSE SESSION accepted in 12 ms. 122 s in all. FUA read-back of every sector passed (79 s). Windows remounted the disc by itself as UDF "IGNISDVDRW", and all 1,005 files hashed identical, no old files visible. |
| 2026-11-11 | GP60NB50 | blank Verbatim DVD-R (MCC 03RG20) | Data, incremental, disc left open (`--no-close`), 74 MB (36,030 sectors, 184 files); then a second session by the Windows writer | Features 0x002F and 0x0021 current; mode page write type 0, FP, packet 16, multi-session 3 confirmed; SET STREAMING 8x; WRITEs without a busy answer; SYNCHRONIZE CACHE 1.8 s, CLOSE TRACK about 2 s, CLOSE SESSION 53 s. TOC: one data track, lead-out 65,264 (the border after the session); the disc then reported status 1 with the next session at 93,952, the same 28,688-sector gap the Windows writer left on this disc type on 2026-11-10. FUA read-back passed; Windows mounted it as CDFS (no UDF on an open disc) and all 184 files were identical. The Windows writer then imported this session, added 189 files (39,188 sectors, without rewriting the first session) and closed the disc; its read-back passed. Windows showed only session 1 until a reload, as recorded for this drive before; after it both folders, all 373 files identical to their sources. |
| 2026-11-11 | GP60NB50 | new Verbatim DVD+RW (MKM-A02-00), never formatted | Data, real write with FORMAT UNIT, 372 MB (181,648 sectors, 1,005 files), 4x setting | Before: background format status 0, READ FORMAT CAPACITIES "unformatted", status 0. FORMAT UNIT type 26h (0xFFFFFFFF blocks, FOV and Immed) accepted at once; the drive then answered 2/04/04 (format in progress) for 28.5 s before taking WRITEs. Data written in 117 s with no busy answer; SYNCHRONIZE CACHE 4 s; CLOSE SESSION accepted, then 2/04/07 for about 32 s; 183 s in all. FUA read-back passed. Afterwards background format status 1 (stopped by the close, as libburn's cookbook describes), status 2; Windows mounted it as UDF "NEWDVDRW" and all 1,005 files hashed identical. |
| 2026-11-10 | GP60NB50 | CD-R, FORNET (ATIP 97:26:07) | Real audio write from a single-file cue sheet: 4 tracks (85 s), CD-Text, catalog number, 4 ISRC codes, pre-emphasis on track 2, copy permitted on track 3, 2 s of gap audio before tracks 2 and 3, 6 s of audio in front of track 1; no test write | Cue sheet (19 entries) accepted in the standard layout, 18,068 sectors with the CD-Text lead-in, session closed in 10 s, 73 s in all. Read-back check passed: table of contents, 21 CD-Text packs, 3,748,500 samples identical, offset 0. Read separately from the disc: control bits 0/1/2/0 in the table of contents, catalog number and all four ISRC codes as written, and the Q sub-channel shows INDEX 00 from the exact frame of each gap, counting down to INDEX 01. A `rip-audio` of the disc gave four files identical to the source. |

Media on hand: CD-RW, CD-R, DVD-R and DVD+RW (Verbatim); DVD+R ordered. No dual-layer, DVD-RW, DVD-RAM or Blu-ray media yet.

The GP60NB50 has a spring-loaded slim tray that cannot close itself: automated runs never eject it, and the owner
swaps discs by hand. Drives added later get a row here with their probe result.
