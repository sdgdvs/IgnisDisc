# Property-based media-degradation tests

`test_media_degradation.py` damages encoded data the way failing optical media does (bit flips, bursts,
zero-filled sectors, dropped or shifted blocks, truncation) and checks two invariants against the shipped code:

1. **Exact recovery** - damage inside the parity bound gives back the bit-identical original.
2. **Zero silent corruption** - damage outside the bound gives an explicit error, never different bytes reported
   as good.

The C# code under test is reached through `CodecBridge.cs`, a single-file program that references
`CdBurner.Core` and answers one JSON request per line. Expected values come from models written from the
standards (ECMA-130, IEC 60908 Annex J, RFC 9639, PAR 2.0), not from the C# implementation.

This suite is not part of `CdBurner.sln` and does not run in CI.

## Run

Needs the .NET 10 SDK and Python 3.11 or newer. The PAR2 properties also need `par2` where the application looks
for it; without it they are skipped.

```
pip install hypothesis pytest
pytest tests/PropertyVerification -q
```

`IGNIS_PROPERTY_EXAMPLES` sets the number of examples per property (default 200). The first run builds the
bridge, which takes about half a minute.

## What is covered

| Area | Code | Properties |
| --- | --- | --- |
| CD-ROM raw sectors | `RawSectorCodec` | Layout, EDC and P/Q parity against ECMA-130; detection of bit flips, bursts, shifted and truncated sectors |
| Sector parity bound | reference decoder in the suite | Two byte errors or a flagged 172-byte burst are recovered from the parity the encoder wrote; anything else is an error |
| CD-Text | `CdTextPackBuilder`, `CdTextPackParser` | CRC against Annex J, burst detection, reordered and dropped packs |
| FLAC | `FlacFileCodec` | Bit-exact round trip, resynchronisation after inserted bytes, corrupt frames, truncation, STREAMINFO bit flips |
| Archive manifest | `ArchiveStager`, `ArchiveManifest`, `VERIFY.PS1` | Payload damage is always reported; manifest damage |
| Span planner | `ArchiveSpanPlanner` | Every file placed once within capacity; oversize files |
| PAR2 | `Par2Runner.BuildCreateArguments` and `Par2Runner.RepairAsync` with the real `par2` | Repair inside the recovery-block bound, explicit failure past it, lost recovery volumes, no file left under a backup name |

## Expected failures

A test marked `xfail(strict=True)` records a confirmed defect: the property states the wanted behaviour and fails
today. Fixing the defect turns the suite red until the marker is removed.

None at the moment.

## Fixed defects the suite now guards

| Id | Test | Defect, as found |
| --- | --- | --- |
| F1 | `test_production_decoder_corrects_a_single_byte_error` | `Extract2048From2352` checked the EDC only and never used the P/Q parity. It now repairs a damaged sector with it (`SectorParityCorrector`) and keeps the repair only if the EDC then matches |
| F2 | `test_mode2_zero_filled_body_is_rejected` | A Mode 2 sector with a zero-filled body passed as valid; it is now reported as damaged |
| F3 | `test_flac_corrupt_frame_is_reported`, `test_flac_truncated_stream_is_reported`, `test_flac_length_field_bit_flip_is_reported` | Damaged FLAC input decoded to altered or shorter audio without an error. `FlacFileCodec.CheckIntegrity` now reports concealed frames, a cut-short file, a wrong header length and an MD5 mismatch; audio burns stop on it unless the user accepts the damage, and a lost first frame no longer shifts the rest of the audio |
| F4 | `test_archive_manifest_damage_never_passes_with_fewer_files`, `test_standalone_verify_script_fails_on_zero_filled_manifest` | An empty, zero-filled or shortened manifest verified as intact |
| F5 | `test_span_plan_rejects_any_file_larger_than_one_disc` | Sector count wrapped for files of 4 TiB and larger |
| F6 | `test_cdtext_dropped_pack_is_rejected` | A missing CD-Text pack changed titles without an error |
| F7 | `test_archive_with_leading_space_file_name_verifies_intact` | File names with a leading space were reported missing |
| F8 | `test_par2_repairs_two_damaged_blocks_with_257_recovery_blocks_left`, `test_par2_with_lost_volumes_repairs_exactly_or_fails_explicitly` | A single `par2` run cannot repair some damage inside the bound once a recovery volume is lost, and left the file only as `<name>.1`. `Par2Runner.RepairAsync` now retries with one volume set aside and restores file names after a failed run |
