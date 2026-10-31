using System.Buffers.Binary;
using System.Text;
using CdBurner.Core.Primitives;

namespace CdBurner.Core.DvdVideo;

/// <summary>
/// Generates DVD-Video binary structure files (.IFO and .BUP) and performs low-level NAV-pack
/// patching and file splitting for compliant Video Manager (VMG) and Video Title Set (VTS) streams.
/// </summary>
public static class DvdIfoGenerator
{
    private const int SectorSize = OpticalConstants.DataSectorSize; // 2048 bytes
    private const int IfoSectorCount = 6; // 6 sectors = 12,288 bytes

    /// <summary>
    /// Patches in-memory VOB bytes ensuring sector 0 (and subsequent NAV packs) contains valid DVD NAV pack headers,
    /// LBN addresses, VOBU end addresses, VOB ID, Cell ID, and menu button highlight commands.
    /// </summary>
    public static void PatchVobNavPackets(byte[] vobBytes, int vobId = 1, int cellId = 1, bool isMenu = false, int buttonCount = 1)
    {
        ArgumentNullException.ThrowIfNull(vobBytes);
        if (vobBytes.Length < SectorSize) return;

        int totalSectors = vobBytes.Length / SectorSize;

        // Ensure sector 0 is a NAV pack
        if (!IsNavPack(vobBytes, 0))
        {
            FormatNavPack(vobBytes, 0, vobId, cellId, isMenu, buttonCount, sectorLbn: 0, vobuEaSectors: totalSectors - 1);
        }

        // Find all NAV packs
        var navSectors = new List<int>();
        for (int s = 0; s < totalSectors; s++)
        {
            if (IsNavPack(vobBytes, s * SectorSize))
            {
                navSectors.Add(s);
            }
        }

        if (navSectors.Count == 0)
        {
            navSectors.Add(0);
        }

        // Patch each NAV pack
        for (int i = 0; i < navSectors.Count; i++)
        {
            int s = navSectors[i];
            int nextS = (i + 1 < navSectors.Count) ? navSectors[i + 1] : totalSectors;
            int vobuEa = Math.Max(0, nextS - s - 1);

            PatchNavPackData(vobBytes, s * SectorSize, s, vobuEa, vobId, cellId, isMenu, buttonCount);
        }
    }

    /// <summary>
    /// Patches NAV packets throughout a VOB file in-place using a stream to avoid loading large files into RAM.
    /// </summary>
    public static void PatchVobNavPacketsInFile(string vobFilePath, int vobId = 1, int cellId = 1, bool isMenu = false, int buttonCount = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vobFilePath);
        if (!File.Exists(vobFilePath)) return;

        using var fs = new FileStream(vobFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, bufferSize: 65536);
        long length = fs.Length;
        if (length < SectorSize) return;

        int totalSectors = (int)(length / SectorSize);
        byte[] sectorBuf = new byte[SectorSize];

        // 1. Ensure sector 0 is a valid NAV pack
        fs.Position = 0;
        int read = fs.Read(sectorBuf, 0, SectorSize);
        if (read == SectorSize && !IsNavPack(sectorBuf, 0))
        {
            FormatNavPack(sectorBuf, 0, vobId, cellId, isMenu, buttonCount, sectorLbn: 0, vobuEaSectors: totalSectors - 1);
            fs.Position = 0;
            fs.Write(sectorBuf, 0, SectorSize);
        }

        // 2. Discover all NAV pack sector indices using 1 MB buffered chunks
        var navSectors = new List<int>();
        const int chunkSectors = 512;
        byte[] chunkBuf = new byte[chunkSectors * SectorSize];
        fs.Position = 0;
        int currentSector = 0;

        while (currentSector < totalSectors)
        {
            int toReadSectors = Math.Min(chunkSectors, totalSectors - currentSector);
            int bytesToRead = toReadSectors * SectorSize;
            int bytesRead = 0;
            while (bytesRead < bytesToRead)
            {
                int r = fs.Read(chunkBuf, bytesRead, bytesToRead - bytesRead);
                if (r <= 0) break;
                bytesRead += r;
            }

            int sectorsRead = bytesRead / SectorSize;
            if (sectorsRead <= 0) break;

            for (int i = 0; i < sectorsRead; i++)
            {
                if (IsNavPack(chunkBuf, i * SectorSize))
                {
                    navSectors.Add(currentSector + i);
                }
            }

            currentSector += sectorsRead;
        }

        if (navSectors.Count == 0)
        {
            navSectors.Add(0);
        }

        // 3. Patch each NAV pack in-place
        for (int i = 0; i < navSectors.Count; i++)
        {
            int s = navSectors[i];
            int nextS = (i + 1 < navSectors.Count) ? navSectors[i + 1] : totalSectors;
            int vobuEa = Math.Max(0, nextS - s - 1);

            fs.Position = (long)s * SectorSize;
            fs.ReadExactly(sectorBuf, 0, SectorSize);

            PatchNavPackData(sectorBuf, 0, s, vobuEa, vobId, cellId, isMenu, buttonCount);

            fs.Position = (long)s * SectorSize;
            fs.Write(sectorBuf, 0, SectorSize);
        }
    }

    /// <summary>
    /// Scans a VOB file on disk and returns the sector indices of all NAV packs (always including sector 0).
    /// </summary>
    public static IReadOnlyList<int> ExtractNavPackSectors(string vobFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vobFilePath);
        var result = new List<int> { 0 };
        if (!File.Exists(vobFilePath)) return result;

        using var fs = new FileStream(vobFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 65536);
        long length = fs.Length;
        int totalSectors = (int)(length / SectorSize);

        const int chunkSectors = 512;
        byte[] chunkBuf = new byte[chunkSectors * SectorSize];
        fs.Position = 0;
        int currentSector = 0;

        while (currentSector < totalSectors)
        {
            int toReadSectors = Math.Min(chunkSectors, totalSectors - currentSector);
            int bytesToRead = toReadSectors * SectorSize;
            int bytesRead = 0;
            while (bytesRead < bytesToRead)
            {
                int r = fs.Read(chunkBuf, bytesRead, bytesToRead - bytesRead);
                if (r <= 0) break;
                bytesRead += r;
            }

            int sectorsRead = bytesRead / SectorSize;
            if (sectorsRead <= 0) break;

            for (int i = 0; i < sectorsRead; i++)
            {
                int sectorIdx = currentSector + i;
                if (sectorIdx > 0 && IsNavPack(chunkBuf, i * SectorSize))
                {
                    result.Add(sectorIdx);
                }
            }

            currentSector += sectorsRead;
        }

        return result;
    }

    /// <summary>
    /// Builds a compliant 6-sector (12,288 byte) Video Manager Information (VIDEO_TS.IFO) buffer.
    /// </summary>
    public static byte[] BuildVmgIfo(
        int titleSetCount,
        DvdVideoStandard standard,
        DvdAspectRatio aspect,
        int menuVobSectors,
        IReadOnlyList<int>? vtsStartSectorOffsets = null,
        bool autoPlayMenu = true)
    {
        titleSetCount = Math.Clamp(titleSetCount, 1, 99);
        int vtsAtrtBytes = 8 + titleSetCount * 4 + titleSetCount * 772;
        int vtsAtrtSectors = Math.Max(1, (vtsAtrtBytes + SectorSize - 1) / SectorSize);
        int pgciUtSector = 2 + vtsAtrtSectors;
        int cAdtSector = pgciUtSector + 1;
        int vobuAdmapSector = cAdtSector + 1;
        int totalIfoSectors = vobuAdmapSector + 1;

        byte[] ifo = new byte[totalIfoSectors * SectorSize];

        // --- Sector 0: VMGI_MAT ---
        int s0 = 0;
        // 0x00..0x0B: Magic signature
        Encoding.ASCII.GetBytes(DvdVideoValidator.VmgMagicSignature).CopyTo(ifo, s0);
        // 0x0C..0x0F: VMGI end sector
        BinaryPrimitives.WriteUInt32BigEndian(ifo.AsSpan(s0 + 0x0C), (uint)(totalIfoSectors - 1));
        // 0x20..0x21: Version 0x0011 (DVD-Video 1.1)
        BinaryPrimitives.WriteUInt16BigEndian(ifo.AsSpan(s0 + 0x20), 0x0011);
        // 0x26..0x27: Volume count = 1
        BinaryPrimitives.WriteUInt16BigEndian(ifo.AsSpan(s0 + 0x26), 1);
        // 0x28..0x29: Volume number = 1
        BinaryPrimitives.WriteUInt16BigEndian(ifo.AsSpan(s0 + 0x28), 1);
        // 0x2A: Disc side = 1
        ifo[s0 + 0x2A] = 1;
        // 0x3E..0x3F: Number of Title Sets
        BinaryPrimitives.WriteUInt16BigEndian(ifo.AsSpan(s0 + 0x3E), (ushort)titleSetCount);
        // 0x40..0x5F: Provider ID "IGNISDISC STUDIO" (padded with spaces)
        byte[] provider = Encoding.ASCII.GetBytes("IGNISDISC STUDIO                ");
        Array.Copy(provider, 0, ifo, s0 + 0x40, 32);

        // 0x80..0x83: VMGI_MAT end byte (0x07FF = 2047)
        BinaryPrimitives.WriteUInt32BigEndian(ifo.AsSpan(s0 + 0x80), 0x000007FF);
        // 0x84..0x87: FP_PGC start byte offset (0x0400 = 1024)
        BinaryPrimitives.WriteUInt32BigEndian(ifo.AsSpan(s0 + 0x84), 0x00000400);

        // Table start sector pointers
        // 0xC0..0xC3: VMGM_VOBS start sector (totalIfoSectors if menu exists, else 0)
        BinaryPrimitives.WriteUInt32BigEndian(ifo.AsSpan(s0 + 0xC0), (uint)(menuVobSectors > 0 ? totalIfoSectors : 0));
        // 0xC4..0xC7: TT_SRPT sector = 1
        BinaryPrimitives.WriteUInt32BigEndian(ifo.AsSpan(s0 + 0xC4), 1);
        // 0xC8..0xCB: VMGM_PGCI_UT sector
        BinaryPrimitives.WriteUInt32BigEndian(ifo.AsSpan(s0 + 0xC8), (uint)pgciUtSector);
        // 0xD4..0xD7: VTS_ATRT sector = 2
        BinaryPrimitives.WriteUInt32BigEndian(ifo.AsSpan(s0 + 0xD4), 2);
        // 0xDC..0xDF: VMGM_C_ADT sector
        BinaryPrimitives.WriteUInt32BigEndian(ifo.AsSpan(s0 + 0xDC), (uint)cAdtSector);
        // 0xE0..0xE3: VMGM_VOBU_ADMAP sector
        BinaryPrimitives.WriteUInt32BigEndian(ifo.AsSpan(s0 + 0xE0), (uint)vobuAdmapSector);

        // Video attributes (0x100..0x101)
        byte videoAttr0 = EncodeVideoAttribute(standard, aspect);
        ifo[s0 + 0x100] = videoAttr0;
        ifo[s0 + 0x101] = 0x00;

        // Audio attributes (0x102..0x10B)
        BinaryPrimitives.WriteUInt16BigEndian(ifo.AsSpan(s0 + 0x102), 1); // 1 audio stream
        ifo[s0 + 0x104] = 0x01; // AC-3 2ch
        ifo[s0 + 0x105] = 0x00; // 48kHz
        ifo[s0 + 0x106] = (byte)'e';
        ifo[s0 + 0x107] = (byte)'n';

        // First-Play PGC (FP_PGC) at offset 0x400
        int fpPgc = s0 + 0x400;
        // PGC Command Table pointer at byte 160..161 of PGC (0x00EC = 236)
        BinaryPrimitives.WriteUInt16BigEndian(ifo.AsSpan(fpPgc + 160), 0x00EC);
        int fpCmd = fpPgc + 0xEC;
        // 1 Pre-command
        BinaryPrimitives.WriteUInt16BigEndian(ifo.AsSpan(fpCmd + 0), 1);
        BinaryPrimitives.WriteUInt16BigEndian(ifo.AsSpan(fpCmd + 2), 0); // 0 post
        BinaryPrimitives.WriteUInt16BigEndian(ifo.AsSpan(fpCmd + 4), 0); // 0 cell
        BinaryPrimitives.WriteUInt16BigEndian(ifo.AsSpan(fpCmd + 6), 0x000F); // end offset

        if (menuVobSectors > 0 && autoPlayMenu)
        {
            // JumpSS VMGM_MENU Title (30 06 00 00 00 42 00 00)
            byte[] jumpMenu = [0x30, 0x06, 0x00, 0x00, 0x00, 0x42, 0x00, 0x00];
            jumpMenu.CopyTo(ifo, fpCmd + 8);
        }
        else
        {
            // JumpTT 1 (30 02 00 00 00 01 00 00)
            byte[] jumpTt1 = [0x30, 0x02, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00];
            jumpTt1.CopyTo(ifo, fpCmd + 8);
        }

        // --- Sector 1: TT_SRPT (Title Search Pointer Table) ---
        int s1 = 1 * SectorSize;
        BinaryPrimitives.WriteUInt16BigEndian(ifo.AsSpan(s1 + 0), (ushort)titleSetCount);
        BinaryPrimitives.WriteUInt32BigEndian(ifo.AsSpan(s1 + 4), (uint)(titleSetCount * 12 + 7));

        for (int i = 1; i <= titleSetCount; i++)
        {
            int tOff = s1 + 8 + (i - 1) * 12;
            ifo[tOff + 0] = 0x01; // 1 sequential PGC
            ifo[tOff + 1] = 0x01; // 1 angle
            BinaryPrimitives.WriteUInt16BigEndian(ifo.AsSpan(tOff + 2), 1); // 1 chapter
            ifo[tOff + 6] = (byte)i; // VTS Number
            ifo[tOff + 7] = 0x01; // Title number within VTS (1)

            uint vtsStart = (vtsStartSectorOffsets != null && i <= vtsStartSectorOffsets.Count)
                ? (uint)vtsStartSectorOffsets[i - 1]
                : (uint)(totalIfoSectors + (menuVobSectors > 0 ? menuVobSectors : 0) + (i - 1) * 32);

            BinaryPrimitives.WriteUInt32BigEndian(ifo.AsSpan(tOff + 8), vtsStart);
        }

        // --- Sector 2..: VTS_ATRT (Video Title Set Attribute Table) ---
        int s2 = 2 * SectorSize;
        BinaryPrimitives.WriteUInt16BigEndian(ifo.AsSpan(s2 + 0), (ushort)titleSetCount);
        BinaryPrimitives.WriteUInt32BigEndian(ifo.AsSpan(s2 + 4), (uint)(vtsAtrtBytes - 1));

        for (int i = 1; i <= titleSetCount; i++)
        {
            int ptrOff = s2 + 8 + (i - 1) * 4;
            int entryRelOff = 8 + titleSetCount * 4 + (i - 1) * 772;
            BinaryPrimitives.WriteUInt32BigEndian(ifo.AsSpan(ptrOff), (uint)entryRelOff);

            int entryOff = s2 + entryRelOff;
            BinaryPrimitives.WriteUInt32BigEndian(ifo.AsSpan(entryOff), 771); // length
            ifo[entryOff + 4] = videoAttr0;
            ifo[entryOff + 5] = 0x00;
            BinaryPrimitives.WriteUInt16BigEndian(ifo.AsSpan(entryOff + 6), 1); // 1 audio stream
            ifo[entryOff + 8] = 0x01; // AC-3
            ifo[entryOff + 9] = 0x00;
            ifo[entryOff + 10] = (byte)'e';
            ifo[entryOff + 11] = (byte)'n';
        }

        // --- VMGM_PGCI_UT (Menu PGC Information Unit Table) ---
        int s3 = pgciUtSector * SectorSize;
        BinaryPrimitives.WriteUInt16BigEndian(ifo.AsSpan(s3 + 0), 1); // 1 Language Unit ("en")
        BinaryPrimitives.WriteUInt32BigEndian(ifo.AsSpan(s3 + 4), 0x000001FF);
        // Language unit entry (offset 8)
        ifo[s3 + 8] = (byte)'e';
        ifo[s3 + 9] = (byte)'n';
        ifo[s3 + 11] = 0x01; // existence flag
        BinaryPrimitives.WriteUInt32BigEndian(ifo.AsSpan(s3 + 12), 16); // LU offset = 16

        // VMGM_LU at s3 + 16
        int lu = s3 + 16;
        BinaryPrimitives.WriteUInt16BigEndian(ifo.AsSpan(lu + 0), 1); // 1 PGC
        BinaryPrimitives.WriteUInt32BigEndian(ifo.AsSpan(lu + 4), 0x000001EF);
        // Menu PGC Search Pointer at lu + 8
        ifo[lu + 8] = 0x82; // Title Menu / Root Menu
        BinaryPrimitives.WriteUInt32BigEndian(ifo.AsSpan(lu + 12), 16); // PGC offset relative to LU (offset 32 in s3)

        // Menu PGC at lu + 16 (s3 + 32)
        int mPgc = lu + 16;
        ifo[mPgc + 2] = (byte)(menuVobSectors > 0 ? 1 : 0); // 1 program if menu
        ifo[mPgc + 3] = (byte)(menuVobSectors > 0 ? 1 : 0); // 1 cell if menu
        ifo[mPgc + 6] = 0x02; // 2 seconds playback time

        // PGC Command Table at mPgc + 0x00EC
        BinaryPrimitives.WriteUInt16BigEndian(ifo.AsSpan(mPgc + 160), 0x00EC);
        int mCmd = mPgc + 0xEC;
        BinaryPrimitives.WriteUInt16BigEndian(ifo.AsSpan(mCmd + 0), 0); // 0 pre
        BinaryPrimitives.WriteUInt16BigEndian(ifo.AsSpan(mCmd + 2), 1); // 1 post
        BinaryPrimitives.WriteUInt16BigEndian(ifo.AsSpan(mCmd + 4), 0); // 0 cell
        BinaryPrimitives.WriteUInt16BigEndian(ifo.AsSpan(mCmd + 6), 0x000F);
        // Post-command: JumpTT 1 (30 02 00 00 00 01 00 00)
        byte[] menuPostJump = [0x30, 0x02, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00];
        menuPostJump.CopyTo(ifo, mCmd + 8);

        if (menuVobSectors > 0)
        {
            // Program Map Table at mPgc + 0x0100: Program 1 -> Cell 1
            BinaryPrimitives.WriteUInt16BigEndian(ifo.AsSpan(mPgc + 162), 0x0100);
            ifo[mPgc + 0x100] = 0x01;

            // Cell Playback Info Table at mPgc + 0x0108
            BinaryPrimitives.WriteUInt16BigEndian(ifo.AsSpan(mPgc + 164), 0x0108);
            int mCell = mPgc + 0x0108;
            ifo[mCell + 0] = 0x01; // normal playback
            ifo[mCell + 6] = 0x02; // 2 sec
            BinaryPrimitives.WriteUInt32BigEndian(ifo.AsSpan(mCell + 12), 0); // start sector 0
            BinaryPrimitives.WriteUInt32BigEndian(ifo.AsSpan(mCell + 20), (uint)Math.Max(0, menuVobSectors - 1));

            // Cell Position Info Table at mPgc + 0x0124
            BinaryPrimitives.WriteUInt16BigEndian(ifo.AsSpan(mPgc + 166), 0x0124);
            int mPos = mPgc + 0x0124;
            BinaryPrimitives.WriteUInt16BigEndian(ifo.AsSpan(mPos + 0), 1); // VOB ID 1
            ifo[mPos + 3] = 1; // Cell ID 1
        }

        // --- VMGM_C_ADT (Menu Cell Address Table) ---
        int s4 = cAdtSector * SectorSize;
        if (menuVobSectors > 0)
        {
            BinaryPrimitives.WriteUInt16BigEndian(ifo.AsSpan(s4 + 0), 1); // 1 VOB
            BinaryPrimitives.WriteUInt32BigEndian(ifo.AsSpan(s4 + 4), 19); // End byte (8-byte header + 12-byte entry - 1)
            BinaryPrimitives.WriteUInt16BigEndian(ifo.AsSpan(s4 + 8), 1); // VOB ID 1
            ifo[s4 + 10] = 1; // Cell ID 1
            BinaryPrimitives.WriteUInt32BigEndian(ifo.AsSpan(s4 + 12), 0); // start
            BinaryPrimitives.WriteUInt32BigEndian(ifo.AsSpan(s4 + 16), (uint)Math.Max(0, menuVobSectors - 1));
        }

        // --- VMGM_VOBU_ADMAP (Menu VOBU Address Map) ---
        int s5 = vobuAdmapSector * SectorSize;
        if (menuVobSectors > 0)
        {
            BinaryPrimitives.WriteUInt32BigEndian(ifo.AsSpan(s5 + 0), 7); // end byte
            BinaryPrimitives.WriteUInt32BigEndian(ifo.AsSpan(s5 + 4), 0); // VOBU 0 at sector 0
        }

        return ifo;
    }

    /// <summary>
    /// Builds a compliant 6-sector (12,288 byte) Video Title Set Information (VTS_xx_0.IFO) buffer.
    /// </summary>
    public static byte[] BuildVtsIfo(
        int titleSetNumber,
        DvdVideoStandard standard,
        DvdAspectRatio aspect,
        int titleVobTotalSectors,
        int durationSeconds = 60,
        int menuVobSectors = 0,
        IReadOnlyList<int>? navPackSectors = null,
        int nextTitleNumber = 0)
    {
        titleSetNumber = Math.Clamp(titleSetNumber, 1, 99);
        durationSeconds = Math.Max(1, durationSeconds);
        titleVobTotalSectors = Math.Max(16, titleVobTotalSectors);

        byte[] ifo = new byte[IfoSectorCount * SectorSize];

        // --- Sector 0: VTSI_MAT ---
        int s0 = 0;
        Encoding.ASCII.GetBytes(DvdVideoValidator.VtsMagicSignature).CopyTo(ifo, s0);
        BinaryPrimitives.WriteUInt32BigEndian(ifo.AsSpan(s0 + 0x0C), 5); // VTSI end sector (5)
        BinaryPrimitives.WriteUInt16BigEndian(ifo.AsSpan(s0 + 0x20), 0x0011); // Version 1.1
        BinaryPrimitives.WriteUInt32BigEndian(ifo.AsSpan(s0 + 0x80), 0x000007FF); // VTSI_MAT end byte

        // Table start sector pointers
        BinaryPrimitives.WriteUInt32BigEndian(ifo.AsSpan(s0 + 0xC0), (uint)(menuVobSectors > 0 ? 6 : 0)); // VTSM_VOBS
        BinaryPrimitives.WriteUInt32BigEndian(ifo.AsSpan(s0 + 0xC4), (uint)(6 + menuVobSectors)); // VTSTT_VOBS
        BinaryPrimitives.WriteUInt32BigEndian(ifo.AsSpan(s0 + 0xC8), 1); // VTS_PTT_SRPT
        BinaryPrimitives.WriteUInt32BigEndian(ifo.AsSpan(s0 + 0xCC), 2); // VTS_PGCIT
        BinaryPrimitives.WriteUInt32BigEndian(ifo.AsSpan(s0 + 0xD4), 3); // VTS_TMAPT
        BinaryPrimitives.WriteUInt32BigEndian(ifo.AsSpan(s0 + 0xE0), 4); // VTS_C_ADT
        BinaryPrimitives.WriteUInt32BigEndian(ifo.AsSpan(s0 + 0xE4), 5); // VTS_VOBU_ADMAP

        // Video attributes (0x200..0x201)
        byte videoAttr0 = EncodeVideoAttribute(standard, aspect);
        ifo[s0 + 0x200] = videoAttr0;
        ifo[s0 + 0x201] = 0x00;

        // Audio attributes (0x202..0x20B)
        BinaryPrimitives.WriteUInt16BigEndian(ifo.AsSpan(s0 + 0x202), 1); // 1 audio stream
        ifo[s0 + 0x204] = 0x01; // AC-3 2ch
        ifo[s0 + 0x205] = 0x00; // 48kHz
        ifo[s0 + 0x206] = (byte)'e';
        ifo[s0 + 0x207] = (byte)'n';

        // --- Sector 1: VTS_PTT_SRPT (Part of Title / Chapters) ---
        int s1 = 1 * SectorSize;
        BinaryPrimitives.WriteUInt16BigEndian(ifo.AsSpan(s1 + 0), 1); // 1 Title in VTS
        BinaryPrimitives.WriteUInt32BigEndian(ifo.AsSpan(s1 + 4), 11); // end offset
        BinaryPrimitives.WriteUInt16BigEndian(ifo.AsSpan(s1 + 8), 1); // 1 Chapter
        ifo[s1 + 10] = 0x01; // PGC 1
        ifo[s1 + 11] = 0x01; // Program 1

        // --- Sector 2: VTS_PGCIT (PGC Information Table) ---
        int s2 = 2 * SectorSize;
        BinaryPrimitives.WriteUInt16BigEndian(ifo.AsSpan(s2 + 0), 1); // 1 PGC
        BinaryPrimitives.WriteUInt32BigEndian(ifo.AsSpan(s2 + 4), 0x000001FF);
        // PGC 1 search pointer at s2 + 8
        ifo[s2 + 8] = 0x81; // Title 1 Entry PGC
        BinaryPrimitives.WriteUInt32BigEndian(ifo.AsSpan(s2 + 12), 16); // PGC offset 16 (s2 + 24)

        int pgc = s2 + 24;
        ifo[pgc + 2] = 1; // 1 program
        ifo[pgc + 3] = 1; // 1 cell
        EncodeBcdTime(durationSeconds, ifo.AsSpan(pgc + 4, 4));

        // PGC Command Table at pgc + 0x00EC
        BinaryPrimitives.WriteUInt16BigEndian(ifo.AsSpan(pgc + 160), 0x00EC);
        int cmd = pgc + 0xEC;
        BinaryPrimitives.WriteUInt16BigEndian(ifo.AsSpan(cmd + 0), 0); // 0 pre
        BinaryPrimitives.WriteUInt16BigEndian(ifo.AsSpan(cmd + 2), 1); // 1 post
        BinaryPrimitives.WriteUInt16BigEndian(ifo.AsSpan(cmd + 4), 0); // 0 cell
        BinaryPrimitives.WriteUInt16BigEndian(ifo.AsSpan(cmd + 6), 0x000F);

        if (nextTitleNumber > 0)
        {
            // JumpTT nextTitleNumber (30 02 00 00 00 {nextTitleNumber} 00 00)
            byte[] jumpNext = [0x30, 0x02, 0x00, 0x00, 0x00, (byte)nextTitleNumber, 0x00, 0x00];
            jumpNext.CopyTo(ifo, cmd + 8);
        }
        else
        {
            // CallSS VMGM_MENU Title (30 08 00 00 01 42 00 00)
            byte[] callMenu = [0x30, 0x08, 0x00, 0x00, 0x01, 0x42, 0x00, 0x00];
            callMenu.CopyTo(ifo, cmd + 8);
        }

        // Program Map Table at pgc + 0x0100: Program 1 -> Cell 1
        BinaryPrimitives.WriteUInt16BigEndian(ifo.AsSpan(pgc + 162), 0x0100);
        ifo[pgc + 0x100] = 0x01;

        // Cell Playback Info Table at pgc + 0x0108
        BinaryPrimitives.WriteUInt16BigEndian(ifo.AsSpan(pgc + 164), 0x0108);
        int cell = pgc + 0x0108;
        ifo[cell + 0] = 0x01; // normal playback
        EncodeBcdTime(durationSeconds, ifo.AsSpan(cell + 4, 4));
        BinaryPrimitives.WriteUInt32BigEndian(ifo.AsSpan(cell + 12), 0); // start sector 0
        BinaryPrimitives.WriteUInt32BigEndian(ifo.AsSpan(cell + 20), (uint)Math.Max(0, titleVobTotalSectors - 1));

        // Cell Position Info Table at pgc + 0x0124
        BinaryPrimitives.WriteUInt16BigEndian(ifo.AsSpan(pgc + 166), 0x0124);
        int pos = pgc + 0x0124;
        BinaryPrimitives.WriteUInt16BigEndian(ifo.AsSpan(pos + 0), 1); // VOB ID 1
        ifo[pos + 3] = 1; // Cell ID 1

        // --- Sector 3: VTS_TMAPT (Time Map Table) ---
        int s3 = 3 * SectorSize;
        BinaryPrimitives.WriteUInt16BigEndian(ifo.AsSpan(s3 + 0), 1); // 1 title
        BinaryPrimitives.WriteUInt32BigEndian(ifo.AsSpan(s3 + 4), 11);

        // --- Sector 4: VTS_C_ADT (Cell Address Table) ---
        int s4 = 4 * SectorSize;
        BinaryPrimitives.WriteUInt16BigEndian(ifo.AsSpan(s4 + 0), 1); // 1 VOB
        BinaryPrimitives.WriteUInt32BigEndian(ifo.AsSpan(s4 + 4), 19); // End byte (8-byte header + 12-byte entry - 1)
        BinaryPrimitives.WriteUInt16BigEndian(ifo.AsSpan(s4 + 8), 1); // VOB ID 1
        ifo[s4 + 10] = 1; // Cell ID 1
        BinaryPrimitives.WriteUInt32BigEndian(ifo.AsSpan(s4 + 12), 0);
        BinaryPrimitives.WriteUInt32BigEndian(ifo.AsSpan(s4 + 16), (uint)Math.Max(0, titleVobTotalSectors - 1));

        // --- Sector 5: VTS_VOBU_ADMAP (VOBU Address Map) ---
        int s5 = 5 * SectorSize;
        var sectors = (navPackSectors != null && navPackSectors.Count > 0)
            ? navPackSectors
            : new[] { 0 };

        int maxEntriesInSector = (SectorSize - 4) / 4; // 511 entries max in single 2048-byte sector
        int entryCount = Math.Min(sectors.Count, maxEntriesInSector);
        BinaryPrimitives.WriteUInt32BigEndian(ifo.AsSpan(s5 + 0), (uint)(entryCount * 4 + 3));
        for (int i = 0; i < entryCount; i++)
        {
            int idx = sectors.Count <= maxEntriesInSector
                ? i
                : (int)((long)i * (sectors.Count - 1) / Math.Max(1, entryCount - 1));
            BinaryPrimitives.WriteUInt32BigEndian(ifo.AsSpan(s5 + 4 + i * 4), (uint)sectors[idx]);
        }

        return ifo;
    }

    /// <summary>
    /// Splits an oversized VOB file (> 1 GB) on 2048-byte sector boundaries into VTS_xx_1.VOB, VTS_xx_2.VOB, ...
    /// If the file is smaller than or equal to maxVobBytes, moves or copies it to target single file.
    /// </summary>
    public static IReadOnlyList<string> SplitVobFileIfOversized(
        string sourceVobPath,
        string targetDirectory,
        int titleSetNumber,
        long maxVobBytes = 1_048_576_000L)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceVobPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);
        titleSetNumber = Math.Clamp(titleSetNumber, 1, 99);

        Directory.CreateDirectory(targetDirectory);
        long sectorAlignedMaxBytes = (maxVobBytes / SectorSize) * SectorSize;
        if (sectorAlignedMaxBytes <= 0) sectorAlignedMaxBytes = 1_048_576_000L;

        var sourceFi = new FileInfo(sourceVobPath);
        if (!sourceFi.Exists)
        {
            throw new FileNotFoundException($"Source VOB file '{sourceVobPath}' was not found.", sourceVobPath);
        }

        string singleVobName = $"VTS_{titleSetNumber:D2}_1.VOB";
        string singleVobPath = Path.Combine(targetDirectory, singleVobName);

        if (sourceFi.Length <= sectorAlignedMaxBytes)
        {
            if (!string.Equals(Path.GetFullPath(sourceVobPath), Path.GetFullPath(singleVobPath), StringComparison.OrdinalIgnoreCase))
            {
                if (Path.GetFileName(sourceVobPath).StartsWith("ignis_transcode_", StringComparison.OrdinalIgnoreCase) ||
                    Path.GetFileName(sourceVobPath).StartsWith("_temp_transcode_", StringComparison.OrdinalIgnoreCase))
                {
                    File.Move(sourceVobPath, singleVobPath, overwrite: true);
                }
                else
                {
                    File.Copy(sourceVobPath, singleVobPath, overwrite: true);
                }
            }
            return new[] { singleVobPath };
        }

        // Split across multiple VOB parts (_1.VOB, _2.VOB, ...)
        var generatedFiles = new List<string>();
        byte[] buffer = new byte[1024 * 1024]; // 1 MB sector-aligned buffer

        using var srcStream = new FileStream(sourceVobPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1024 * 1024);
        int partIndex = 1;
        long totalRead = 0;

        while (totalRead < sourceFi.Length)
        {
            string partName = $"VTS_{titleSetNumber:D2}_{partIndex}.VOB";
            string partPath = Path.Combine(targetDirectory, partName);
            generatedFiles.Add(partPath);

            long currentPartWritten = 0;
            using (var dstStream = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 1024 * 1024))
            {
                while (currentPartWritten < sectorAlignedMaxBytes && totalRead < sourceFi.Length)
                {
                    int toRead = (int)Math.Min(buffer.Length, sectorAlignedMaxBytes - currentPartWritten);
                    toRead = (int)Math.Min(toRead, sourceFi.Length - totalRead);

                    int read = srcStream.Read(buffer, 0, toRead);
                    if (read <= 0) break;

                    dstStream.Write(buffer, 0, read);
                    currentPartWritten += read;
                    totalRead += read;
                }
            }

            partIndex++;
            if (partIndex > 9)
            {
                // Maximum 9 VOB files per Title Set in DVD-Video specification
                break;
            }
        }

        return generatedFiles;
    }

    private static bool IsNavPack(byte[] buffer, int offset)
    {
        if (offset + 45 > buffer.Length) return false;
        // Pack start code (00 00 01 BA) and Private Stream 2 (00 00 01 BF) with substream 00 (PCI)
        return buffer[offset + 0] == 0x00 &&
               buffer[offset + 1] == 0x00 &&
               buffer[offset + 2] == 0x01 &&
               buffer[offset + 3] == 0xBA &&
               buffer[offset + 38] == 0x00 &&
               buffer[offset + 39] == 0x00 &&
               buffer[offset + 40] == 0x01 &&
               buffer[offset + 41] == 0xBF &&
               buffer[offset + 44] == 0x00;
    }

    private static void FormatNavPack(byte[] buffer, int offset, int vobId, int cellId, bool isMenu, int buttonCount, int sectorLbn, int vobuEaSectors)
    {
        // 1. Pack Header (14 bytes)
        byte[] packHeader = [0x00, 0x00, 0x01, 0xBA, 0x44, 0x00, 0x04, 0x00, 0x04, 0x01, 0x01, 0x89, 0xC3, 0xF8];
        packHeader.CopyTo(buffer, offset);

        // 2. System Header (24 bytes)
        byte[] sysHeader = [0x00, 0x00, 0x01, 0xBB, 0x00, 0x12, 0x80, 0xC4, 0xE1, 0x04, 0xE1, 0x7F, 0xE0, 0xE0, 0xE8, 0xBD, 0xE0, 0x3A, 0xBF, 0xE0, 0x02];
        sysHeader.CopyTo(buffer, offset + 14);

        // 3. PCI Packet (offset 38)
        buffer[offset + 38] = 0x00;
        buffer[offset + 39] = 0x00;
        buffer[offset + 40] = 0x01;
        buffer[offset + 41] = 0xBF; // Private stream 2
        buffer[offset + 42] = 0x03; // length 0x03D4 = 980
        buffer[offset + 43] = 0xD4;
        buffer[offset + 44] = 0x00; // Substream ID 00 (PCI)

        // 4. DSI Packet (offset 1024)
        buffer[offset + 1024] = 0x00;
        buffer[offset + 1025] = 0x00;
        buffer[offset + 1026] = 0x01;
        buffer[offset + 1027] = 0xBF; // Private stream 2
        buffer[offset + 1028] = 0x03; // length 0x03FA = 1018
        buffer[offset + 1029] = 0xFA;
        buffer[offset + 1030] = 0x01; // Substream ID 01 (DSI)

        PatchNavPackData(buffer, offset, sectorLbn, vobuEaSectors, vobId, cellId, isMenu, buttonCount);
    }

    private static void PatchNavPackData(byte[] buffer, int offset, int sectorLbn, int vobuEaSectors, int vobId, int cellId, bool isMenu, int buttonCount)
    {
        // PCI: nv_pck_lbn at offset 45
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(offset + 45), (uint)sectorLbn);

        // DSI_GI starts at offset 1031 (after 6-byte PES header at 1024..1029 and 1-byte substream ID 0x01 at 1030):
        // +4 (1035): nv_pck_lbn, +8 (1039): vobu_ea, +24 (1055): vobu_vob_idn, +27 (1058): vobu_c_idn
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(offset + 1035), (uint)sectorLbn);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(offset + 1039), (uint)vobuEaSectors);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset + 1055), (ushort)vobId);
        buffer[offset + 1058] = (byte)cellId;

        if (isMenu)
        {
            // PCI Highlight Information (hl_gi):
            // hli_ss at offset 140 (0x8C)
            BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset + 140), 0x0001);
            BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(offset + 142), 0); // hli_s_ptm
            BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(offset + 146), 0xFFFFFFFF); // hli_e_ptm
            BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset + 154), 1); // btn_s_num
            buffer[offset + 156] = (byte)Math.Clamp(buttonCount, 1, 36); // btn_ns
            buffer[offset + 158] = 1; // fosl_btnn (forced selected button 1)

            // Button 1 VM command at offset 196..203: JumpTT 1 (30 02 00 00 00 01 00 00)
            byte[] jumpTt1 = [0x30, 0x02, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00];
            jumpTt1.CopyTo(buffer, offset + 196);
        }
    }

    private static byte EncodeVideoAttribute(DvdVideoStandard standard, DvdAspectRatio aspect)
    {
        // Bits 7-6: 01 = MPEG-2 (0x40)
        byte b = 0x40;

        // Bits 5-4: 00 = NTSC, 01 = PAL (0x10)
        if (standard == DvdVideoStandard.Pal)
        {
            b |= 0x10;
        }

        // Bits 3-2: 00 = 4:3, 11 = 16:9 (0x0C)
        if (aspect == DvdAspectRatio.Widescreen16x9)
        {
            b |= 0x0C;
        }

        return b;
    }

    private static void EncodeBcdTime(int durationSeconds, Span<byte> target)
    {
        int hours = Math.Clamp(durationSeconds / 3600, 0, 99);
        int rem = durationSeconds % 3600;
        int mins = rem / 60;
        int secs = rem % 60;

        target[0] = ToBcd(hours);
        target[1] = ToBcd(mins);
        target[2] = ToBcd(secs);
        target[3] = 0x00; // frames
    }

    private static byte ToBcd(int val)
    {
        int tens = val / 10;
        int units = val % 10;
        return (byte)((tens << 4) | units);
    }
}
