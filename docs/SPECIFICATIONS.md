# CdBurner: Functional & Technical Specifications

## 1. Overview & Target Platform
**CdBurner** (`CdBurner.sln`) is a full-featured, native optical media authoring, burning, ripping, verification, and drive diagnostics suite for **Windows 10 (build 19041+)** and **Windows 11 (build 22000+)**.

- **Runtime & SDK**: `.NET 10.0` (`net10.0-windows10.0.22621.0`)
- **UI Framework**: WPF with Windows 11 Fluent styling (Mica/Acrylic dark & light themes, MVVM via `CommunityToolkit.Mvvm`)
- **CLI Companion**: `cdburner-cli.exe` for headless scripting, automation, and integration testing
- **Native Windows Subsystems**:
  - Windows Image Mastering API v2 (`imapi2.dll` & `imapi2fs.dll`)
  - Win32 SCSI Pass-Through Direct (`IOCTL_SCSI_PASS_THROUGH_DIRECT`, `IOCTL_CDROM_RAW_READ`, `IOCTL_CDROM_READ_TOC_EX` via `ntddcdrm.h` / `ntddscsi.h`)
  - Windows Media Foundation (`mfplat.dll`, `mfreadwrite.dll`) for multi-format audio decoding to Red Book PCM

---

## 2. Optical Media Geometry & Constants

### 2.1 Sector & Frame Geometry
| Metric | Value | Notes |
| :--- | :--- | :--- |
| **Data Sector Size (Mode 1 / DVD)** | `2,048 bytes` | Standard logical block size for ISO 9660, Joliet, UDF |
| **Raw Sector Size (CD-DA / BIN)** | `2,352 bytes` | Red Book audio frame or raw CD Mode 1 / Mode 2 sector |
| **CD Mode 1 Raw Layout** | `12B Sync + 4B Header + 2048B Data + 4B EDC + 8B Zero + 276B ECC = 2352B` | Sync pattern: `00 FF FF FF FF FF FF FF FF FF FF 00` |
| **CD-DA Audio Frame Rate** | `75 frames / second` | $1\text{ second} = 75\text{ LBA sectors} = 176,400\text{ bytes}$ (`44,100 Hz` $\times$ `2 ch` $\times$ `2 bytes`) |
| **MSF Addressing** | `Minutes:Seconds:Frames` | $\text{LBA} = (M \times 60 + S) \times 75 + F - 150$ (with standard 2-second / 150-frame Lead-In offset) |
| **DVD ECC Block Size** | `32,768 bytes` (`16 sectors`) | Physical error-correction block on DVD media (`16` $\times$ `2,048 bytes`) |

### 2.2 Media Capacity Standards
| Media Type | Nominal Capacity (Bytes) | Nominal Sectors (2048 B) | Audio Duration |
| :--- | :--- | :--- | :--- |
| **CD-R / CD-RW (74 min / 650 MB)** | `681,984,000 bytes` | `333,000 sectors` | `74:00:00` (`333,000` frames) |
| **CD-R / CD-RW (80 min / 700 MB)** | `737,280,000 bytes` | `360,000 sectors` | `80:00:00` (`360,000` frames) |
| **CD-R Overburn (90 min / 800 MB)** | `829,440,000 bytes` | `405,000 sectors` | `90:00:00` (`405,000` frames) |
| **DVD-R / DVD-RW (Single Layer DVD-5)** | `4,707,319,808 bytes` | `2,298,496 sectors` | N/A |
| **DVD+R / DVD+RW (Single Layer DVD-5)** | `4,700,372,992 bytes` | `2,295,104 sectors` | N/A |
| **DVD-R DL (Dual Layer DVD-9)** | `8,543,666,176 bytes` | `4,171,712 sectors` | N/A |
| **DVD+R DL (Dual Layer DVD-9)** | `8,547,991,552 bytes` | `4,173,824 sectors` | N/A |

### 2.3 Write Speed Multipliers ($1\text{X}$ Reference)
- **CD Data $1\text{X}$**: `150 KB/s` (`153,600 bytes/sec` / `75` Mode-1 sectors/sec)
- **CD Audio / Raw $1\text{X}$**: `176.4 KB/s` (`176,400 bytes/sec` / `75` Raw 2352-byte sectors/sec)
- **DVD $1\text{X}$**: `1,385 KB/s` (`1,385,000 bytes/sec` $\approx 676$ sectors/sec)

---

## 3. Functional Requirements by Module

### 3.1 Module A: Data Disc & Bootable Compilation Engine
1. **Virtual Compilation Tree (`CompilationDirectory` & `CompilationFile`)**:
   - Users and CLI scripts can add local files and directories into a hierarchical virtual tree without mutating the source filesystem.
   - Supports renaming nodes in-place, creating virtual folders, removing nodes, setting file attributes (`ReadOnly`, `Hidden`, `System`, `Archive`), and overriding creation/modification UTC timestamps.
   - Detects duplicate paths and resolves collisions according to configurable policy (`Overwrite`, `RenameWithSuffix`, `Skip`, `ThrowError`).
2. **File System Formatting**:
   - **ISO 9660 (ECMA-119)**:
     - Level 1: `8.3` uppercase `[A-Z0-9_]`, max directory depth `8`.
     - Level 2: Up to `31` characters uppercase `[A-Z0-9_]`.
     - Level 3: Allows fragmented/multi-extent files $> 4\text{ GB}$.
   - **Joliet Extension**: Secondary Volume Descriptor (`Type 2` at LBA `17` or `18`) with UCS-2 Big Endian Unicode filenames up to `64` UTF-16 code units (`128 bytes`).
   - **UDF (Universal Disk Format)**: Revisions `1.02` (`0x0102`), `1.50` (`0x0150`), `2.00` (`0x0200`), `2.01` (`0x0201`), `2.50` (`0x0250`), `2.60` (`0x0260`).
   - **ISO 9660 + UDF Bridge**: Combines ISO 9660 Primary/Joliet descriptors in LBAs `16+` with UDF Anchor Volume Descriptor Pointer (`AVDP`) at LBA `256` and `LastLba`.
3. **El Torito Bootable Discs**:
   - Boot Record Volume Descriptor at LBA `17` (`\x00CD001\x01EL TORITO SPECIFICATION\x00...`) pointing to the Boot Catalog LBA.
   - **Boot Catalog Binary Structure (32-byte entries)**:
     - **Validation Entry (32 bytes)**: Header ID `0x01`, Platform ID (`0x00` = x86 BIOS, `0xEF` = EFI), Manufacturer ID string (24 bytes), 16-bit two's complement checksum at offset `28` such that the sum of all sixteen 16-bit little-endian words in the Validation Entry equals `0x0000`, Key bytes `0x55 0xAA` at offsets `30..31`.
     - **Initial/Default Entry (32 bytes)**: Boot Indicator (`0x88` = Bootable, `0x00` = Not bootable), Boot Media Type (`0x00` = No Emulation, `0x01` = 1.2M Floppy, `0x02` = 1.44M Floppy, `0x03` = 2.88M Floppy, `0x04` = Hard Disk), Load Segment (`uint16`, `0x0000` defaults to `0x07C0`), System Type (`byte`), Sector Count (`uint16`, in 512-byte virtual sectors), Load RBA (`uint32`, 2048-byte LBA of boot image).
     - **Section Header Entry (`0x90` or `0x91` for Final Header)** & **Section Entry (`0x88`)**: Supports dual BIOS (`0x00`) + UEFI (`0xEF`) multi-boot ISO images (used by Windows 10/11 and Linux installation media).
4. **Multi-Session Mastering**:
   - Reads prior sessions on writable/appendable media, imports existing directory/file nodes with their previously recorded LBA addresses, and writes only newly added/updated file extents into the new session while writing an updated Volume Descriptor / UDF Anchor pointing to both old and new extents.

### 3.2 Module B: Disc Image Studio (ISO, BIN/CUE, Rip, Verify)
1. **CUE Sheet Parser & Generator (`.cue` + `.bin`)**:
   - Supports commands: `CATALOG`, `CDTEXTFILE`, `FILE "<path>" <BINARY|WAVE|MP3>`, `PERFORMER`, `SONGWRITER`, `TITLE`, `REM`, `TRACK <01..99> <AUDIO|MODE1/2048|MODE1/2352|MODE2/2336|MODE2/2352>`, `ISRC`, `FLAGS <DCP|4CH|PRE|SCMS>`, `PREGAP <MM:SS:FF>`, `INDEX <00..99> <MM:SS:FF>`, `POSTGAP <MM:SS:FF>`.
2. **Raw Sector Conversion & EDC/ECC**:
   - Converts `2048`-byte user sectors to `2352`-byte Mode 1 raw sectors by generating the 12-byte sync header, 4-byte MSF+Mode address header, 4-byte EDC (CRC-32 over bytes `0..2063` with polynomial `0xD8018001` reflected), 8 zero bytes, and 276-byte Reed-Solomon Product-like Code (RSPC) P-parity (`172B`) and Q-parity (`104B`).
   - Extracts `2048`-byte user data from `2352`-byte Mode 1 / Mode 2 XA raw sectors while verifying EDC integrity.
3. **Disc Ripping Engine**:
   - **Data Ripper**: Reads LBA `0..N` into `.iso` with configurable read speed, sector batch size (`32` sectors / `64 KB`), bad-sector retry count (default `3`), and optional zero-fill on unrecoverable LBA with a companion `.rip.log` defect map.
   - **Raw / Audio Ripper**: Extracts `2352`-byte raw sectors + TOC into `.bin` + `.cue`.
4. **Post-Burn & Standalone Verifier**:
   - Streams burned disc sectors and compares against the source `.iso`, `.bin`, or compilation file streams, computing overall **CRC32**, **MD5**, and **SHA-256** hashes and identifying any mismatched LBAs and the exact file path occupying that LBA range.

### 3.3 Module C: Audio CD Mastering, DSP & CD-Text
1. **Audio Ingestion & Decoding**:
   - Decodes `.wav`, `.mp3`, `.flac`, `.ogg`, `.aac`, `.m4a`, `.wma` via Windows Media Foundation (`IMFSourceReader`) or built-in WAV/PCM parser into canonical Red Book PCM (`44,100 Hz`, `16-bit signed LE`, `2 channels`).
   - Automatically pads the final partial sector of a track with silence (`0x00`) so every track is an exact integer multiple of `2,352 bytes` (`588` stereo samples / $\frac{1}{75}\text{ sec}$).
2. **PCM DSP Processing**:
   - **Peak Normalization**: Two-pass or buffered scan finding max absolute sample peak $\max(|s|)$ and scaling samples to target dBFS (default `-0.1 dBFS`) with clamping protection.
   - **Pre-gap Generation**: Injects configurable `INDEX 00` silence frames (e.g., `150` frames = `2.0s` before Track 1; `0` frames for gapless DAO or `150` frames for standard TAO).
3. **CD-Text Binary Pack Format (IEC 60908 / Red Book Annex J)**:
   - Each CD-Text pack is **18 bytes**:
     - `Byte 0`: Pack Type Indicator (`0x80` = Title, `0x81` = Performer, `0x82` = Songwriter, `0x83` = Composer, `0x84` = Arranger, `0x85` = Message, `0x86` = Disc ID, `0x87` = Genre, `0x8E` = UPC/EAN & ISRC, `0x8F` = Size Information).
     - `Byte 1`: Track Number (`0x00` for Album-level, `0x01..0x63` for Tracks `1..99`; bit 7 = Extension Flag).
     - `Byte 2`: Sequence Number (`0x00..0xFF`, incrementing per pack within block).
     - `Byte 3`: Block Character Position (`bits 0..3` = char position `0..15`, `bits 4..6` = Block Number `0..7`, `bit 7` = DBCS flag).
     - `Bytes 4..15`: **12 bytes** of null-terminated ASCII / ISO-8859-1 text payload (strings span across consecutive packs as needed).
     - `Bytes 16..17`: **16-bit CRC** (`CRC-16-CCITT`, polynomial $x^{16} + x^{12} + x^5 + 1 = \text{0x1021}$, initial value `0x0000`, calculated over bytes `0..15`, big-endian, **bitwise inverted** `^ 0xFFFF`).
4. **TAO vs. DAO Burning**:
   - **Track-at-Once (TAO)**: Uses `IDiscFormat2TrackAtOnce` (`AddAudioTrack` per track).
   - **Disc-at-Once (DAO)**: Uses `IDiscFormat2RawCD` (or MMC `SEND CUE SHEET` + raw `2352` / `2448` byte frames) for true gapless transitions and Lead-In CD-Text recording.

### 3.4 Module D: DVD-Video Mastering (`VIDEO_TS`)
1. **Structure Validation**:
   - Verifies presence and uppercase naming of `VIDEO_TS/` (`VIDEO_TS.IFO`, `VIDEO_TS.BUP`, optional `VIDEO_TS.VOB`) and `VTS_01_0.IFO`, `VTS_01_0.BUP`, `VTS_01_1.VOB` .. `VTS_99_9.VOB`, plus `AUDIO_TS/`.
   - Validates `DVDVIDEO-VMG` header signature inside `VIDEO_TS.IFO` and `DVDVIDEO-VTS` header signature inside `VTS_xx_0.IFO`.
2. **32 KB ECC Block Alignment (IFO / BUP Separation)**:
   - DVD physical ECC blocks span `16` consecutive `2048`-byte sectors (`32,768 bytes`).
   - If `VTS_xx_0.VOB` is absent or $< 32\text{ KB}$, `VTS_xx_0.IFO` and `VTS_xx_0.BUP` would land in the same 16-sector ECC block. The DVD-Video layout engine calculates LBA placements so that the start LBA of every `.BUP` file is at least `16` sectors past the end LBA of its corresponding `.IFO` file.
3. **Dual-Layer (DVD-9) Layer Break Calculation**:
   - For compilations $> 2,295,104$ sectors (`4.7 GB`), identifies candidate cell boundaries where Layer 0 size $\ge$ Layer 1 size, Layer 0 size $\le 2,086,912$ sectors, and the Layer Break LBA is an exact multiple of `16` (ECC block boundary).

### 3.5 Module E: Drive Utilities, Erase, Surface Diagnostics & Virtual Simulator
1. **Rewritable Erase**: Quick and Full erase for CD-RW, DVD-RW, DVD+RW, DVD-RAM.
2. **Drive & Media Inspector**: Reports drive vendor/model/firmware, buffer size, supported read/write media profiles, current disc type, media manufacturer ID, sessions, free/used capacity, and supported speed descriptors.
3. **Surface Scan & Read Speed Benchmark**: Reads disc sequentially in configurable LBA chunks, measuring transfer speed (`KB/s` and `X`) and classifying each sector block as `Good`, `Slow` ($< 25\%$ expected speed), or `Damaged/Unreadable`.
4. **Virtual Optical Drive Simulator (`VirtualDiscBackend`)**:
   - Software-defined virtual recorder (`VDRV-01: Ignis Virtual DVD/CD-RW`) supporting programmable media insertion (`Empty`, `BlankCdR700`, `BlankCdRw700`, `BlankDvdR47`, `BlankDvdRDL85`, or mounting an existing `.iso`/`.cue` file).
   - Persists burned sectors to a backing file in a configurable directory so tests and users can inspect burned discs byte-for-byte.
