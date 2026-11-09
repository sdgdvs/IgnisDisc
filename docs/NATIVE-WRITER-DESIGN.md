# Native drive-command writer: design proposal

**Status:** proposal, not implemented. The only part that exists is the read-only capability probe
(`MmcWriteCapabilityProbe`, `MmcMediaIdInspector`): it sends `GET CONFIGURATION`, `MODE SENSE`, `READ TOC/PMA/ATIP`
and `READ DISC STRUCTURE`, and writes nothing. Everything below is the plan for roadmap item P1, phases 2–6.

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
   Session-at-Once, Track-at-Once (0x002D) for TAO, DVD-R/-RW Write (0x002F) or the DVD+R feature for DVD.
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

**Audio CD, Session-at-Once with CD-Text:** `MODE SELECT` page 0x05 (write type SAO) → `SEND CUE SHEET` (0x5D) built
from the track list, pre-gaps and ISRC/MCN; with CD-Text, the cue sheet's lead-in entry announces R-W subcode and the
CD-Text packs are written into the lead-in → `WRITE(10)` of 2,352-byte audio sectors from the first pre-gap
(LBA −150) → `SYNCHRONIZE CACHE`. The drive closes the session itself.

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
- Real hardware, in order: test write on CD-RW, real write on CD-RW, then CD-R; DVD after CD. The drives and media
  used are listed in the roadmap.

## Open decisions (owner)

- Which feature ships first. Suggested: audio CD with CD-Text (Session-at-Once), since it is the most requested
  thing IMAPIv2 cannot do.
- Which drives and media are available for testing.
