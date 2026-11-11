using System.Buffers.Binary;
using CdBurner.Hardware.NativeWriting;
using CdBurner.Hardware.Scsi;

namespace CdBurner.Hardware.Simulation;

/// <summary>
/// A CD recorder at the SCSI command level, for testing the native writer without hardware. It answers the MMC
/// commands a Session-at-Once audio write uses and enforces what a real drive enforces: write parameters before the
/// cue sheet, cue sheet before WRITE, every WRITE at the next address with the right block size (96 bytes in a
/// CD-Text lead-in, 2,352 in the program area), nothing past the lead-out. Faults can be switched on to test the
/// writer's error handling. A test write leaves the disc blank, as on a real drive. A real write leaves a recorded
/// disc that answers READ TOC, CD-Text read-back and READ CD, shifted by a drive offset the way real hardware shifts
/// audio, so the read-back check is tested against the same behaviour. With a DVD profile it also answers the data
/// writer's DVD commands (RESERVE TRACK, SET STREAMING, CLOSE TRACK / SESSION / finalize) with the rules from libburn's
/// cookbook: whole 32 KiB blocks per WRITE, a reservation before Disc-At-Once, no mode page for DVD+R.
/// </summary>
public sealed class VirtualMmcDrive : IScsiTransport
{
    public const int MaxTransferBytes = 65536;

    private byte[] _modePage05 = CreateDefaultModePage05();
    private CueSheetState? _cue;
    private int _nextLba;
    private int _writesSinceBufferFull;

    // ---- Disc and drive description ----

    public bool MediaPresent { get; set; } = true;
    public ushort Profile { get; set; } = NativeSaoAudioWriter.ProfileCdR;

    /// <summary>Disc status from READ DISC INFORMATION: 0 blank, 1 appendable, 2 complete.</summary>
    public int DiscStatus { get; set; }

    public Msf LeadInStart { get; set; } = new(97, 34, 23);
    public Msf LastLeadOutStart { get; set; } = new(79, 59, 74);

    public bool OffersSessionAtOnce { get; set; } = true;
    public bool OffersTestWrite { get; set; } = true;
    public bool OffersRwSubchannel { get; set; } = true;
    public bool OffersBufferUnderrunFree { get; set; } = true;

    /// <summary>DVD-R/-RW: the drive offers incremental writing (feature 0x0021).</summary>
    public bool OffersIncremental { get; set; } = true;

    /// <summary>Capacity in sectors for discs whose addresses go past CD MSF times (DVD); null uses <see cref="LastLeadOutStart"/>.</summary>
    public int? CapacitySectors { get; set; }

    private int Capacity => CapacitySectors ?? LastLeadOutStart.ToLba();

    /// <summary>True for every DVD profile.</summary>
    public bool IsDvd => NativeTaoDataWriter.IsDvdProfile(Profile);

    private bool IsDvdPlusR => Profile is NativeTaoDataWriter.ProfileDvdPlusR or NativeTaoDataWriter.ProfileDvdPlusRDualLayer;

    // ---- Faults ----

    /// <summary>TEST UNIT READY answers 2/04/01 (becoming ready) this many times first.</summary>
    public int NotReadyResponses { get; set; }

    /// <summary>MODE SELECT stores the page with the Test Write bit cleared, so the read-back shows it off.</summary>
    public bool DropsTestWriteBit { get; set; }

    /// <summary>The page reads back with Test Write set, but the drive records the disc anyway.</summary>
    public bool RecordsDuringTestWrite { get; set; }

    /// <summary>MODE SELECT with data block type 3 fails with 5/26/00.</summary>
    public bool RejectsDataBlockType3 { get; set; }

    /// <summary>A CD-Text lead-in cue entry with a time other than 00:00:00 fails with 5/26/00.</summary>
    public bool RejectsLeadInTimeInCueSheet { get; set; }

    /// <summary>WRITE covering this LBA fails with 3/0C/00 (write error).</summary>
    public int? WriteErrorAtLba { get; set; }

    /// <summary>Every Nth WRITE first answers 2/04/08 (long write in progress) once.</summary>
    public int BufferFullEveryNWrites { get; set; }

    /// <summary>
    /// After a test write, READ DISC INFORMATION shows the simulated session as open (status 1) until
    /// <see cref="Reload"/>, as the LG GP60NB50 does. Off: the disc reads as blank straight away.
    /// </summary>
    public bool ShowsSimulatedSessionAfterTestWrite { get; set; } = true;

    /// <summary>A recorded Session-at-Once disc is closed (status 2). Off: it reads as an open session (status 1).</summary>
    public bool ClosesDiscAfterWrite { get; set; } = true;

    /// <summary>Keep the bytes sent with WRITE so tests can inspect them and a recorded disc can be read back.</summary>
    public bool CaptureWrites { get; set; } = true;

    /// <summary>
    /// Audio reads back this many samples later than it was written (the combined write and read offset every real
    /// drive has). Samples shifted in from outside the recorded area read as silence.
    /// </summary>
    public int ReadOffsetSamples { get; set; }

    /// <summary>READ CD returns this sector with one sample changed, like a marginal burn.</summary>
    public int? ReadBackDifferenceAtLba { get; set; }

    /// <summary>READ CD of this sector fails with 3/11/00 (unrecovered read error).</summary>
    public int? ReadErrorAtLba { get; set; }

    /// <summary>The recorded disc returns no CD-Text although a CD-Text lead-in was sent.</summary>
    public bool LosesCdText { get; set; }

    /// <summary>The recorded table of contents places track 2 and later one sector late.</summary>
    public bool ShiftsTrackStartsInToc { get; set; }

    /// <summary>The drive ignores the pre-emphasis and copy-permitted bits of the cue sheet.</summary>
    public bool DropsControlBits { get; set; }

    /// <summary>SET CD SPEED fails with 5/24/00.</summary>
    public bool RejectsSetSpeed { get; set; }

    /// <summary>The recorded disc shows no catalog number and no ISRC codes (the drive dropped them while writing).</summary>
    public bool LosesCatalogAndIsrc { get; set; }

    /// <summary>The ISRC of this track reads back with its last character changed.</summary>
    public int? ChangesIsrcOfTrack { get; set; }

    /// <summary>The drive refuses READ SUB-CHANNEL (5/20/00), so the codes cannot be read back at all.</summary>
    public bool RejectsReadSubChannel { get; set; }

    // ---- Track-at-Once data writing ----

    /// <summary>READ TRACK INFORMATION fails with 5/20/00, as on drives that do not offer it.</summary>
    public bool RejectsReadTrackInformation { get; set; }

    /// <summary>READ TRACK INFORMATION of a blank disc reports this next writable address instead of 0.</summary>
    public int? NextWritableAddressOverride { get; set; }

    /// <summary>CLOSE TRACK/SESSION fails with 3/0C/00.</summary>
    public bool RejectsCloseSession { get; set; }

    /// <summary>With the Immed bit, TEST UNIT READY answers "not ready" this many times while the session is closed.</summary>
    public int CloseSessionBusyPolls { get; set; } = 3;

    /// <summary>The data track sent with WRITE (10) in Track-at-Once mode (2,048 bytes per sector).</summary>
    public MemoryStream DataTrackWritten { get; } = new();

    /// <summary>The last CLOSE TRACK/SESSION close function received.</summary>
    public byte? LastCloseFunction { get; private set; }

    private bool _taoArmed;        // MODE SELECT set Track-at-Once since the last track
    private int _taoNextLba;       // next writable address of the open track
    private int _taoClosedSectors; // sectors of a track flushed with SYNCHRONIZE CACHE, waiting for CLOSE SESSION
    private bool _taoTestWrite;

    // ---- DVD writing ----

    /// <summary>RESERVE TRACK fails with 5/24/00.</summary>
    public bool RejectsReserveTrack { get; set; }

    /// <summary>SET STREAMING fails with 5/20/00, as on drives that only take SET CD SPEED.</summary>
    public bool RejectsSetStreaming { get; set; }

    /// <summary>The DVD+R finalize function (101b) fails with 5/24/00; libburn's cookbook reports drives that refuse it.</summary>
    public bool RejectsDvdPlusRFinalize { get; set; }

    /// <summary>CLOSE SESSION answers 2/04/07 (operation in progress) this many times first, as some drives do.</summary>
    public int CloseSessionBusyResponses { get; set; }

    /// <summary>Sectors reserved with RESERVE TRACK for a DVD-R Disc-At-Once track; 0 when none.</summary>
    public int ReservedSectors { get; private set; }

    /// <summary>The last SET STREAMING performance descriptor received (28 bytes).</summary>
    public byte[]? LastPerformanceDescriptor { get; private set; }

    // ---- DVD+RW ----

    /// <summary>READ DISC INFORMATION byte 7 for a DVD+RW: 0 never formatted, 1 partly (stopped), 2 formatting, 3 formatted.</summary>
    public int BackgroundFormatStatus { get; set; } = 3;

    /// <summary>FORMAT UNIT fails with 5/24/00.</summary>
    public bool RejectsFormatUnit { get; set; }

    /// <summary>FORMAT UNIT commands accepted.</summary>
    public int FormatUnitCount { get; private set; }

    /// <summary>The last FORMAT UNIT parameter list accepted (12 bytes).</summary>
    public byte[]? LastFormatParameters { get; private set; }

    private bool IsDvdPlusRw => Profile == NativeTaoDataWriter.ProfileDvdPlusRw;

    /// <summary>
    /// A READ FORMAT CAPACITIES reply for a DVD+RW: the current/maximum descriptor (formatted or not, 2,048-byte
    /// blocks) and one formattable descriptor of type 26h.
    /// </summary>
    public static byte[] BuildDvdPlusRwFormatCapacities(int blocks, bool formatted)
    {
        byte[] r = new byte[4 + 16];
        r[3] = 16;
        BinaryPrimitives.WriteInt32BigEndian(r.AsSpan(4), blocks);
        r[8] = (byte)(formatted ? 2 : 1);
        r[10] = 0x08; // block length 2,048
        BinaryPrimitives.WriteInt32BigEndian(r.AsSpan(12), blocks);
        r[16] = NativeWriteCommands.FormatTypeDvdPlusRw << 2;
        return r;
    }

    private bool _dvdModeSet;     // MODE SELECT chose Disc-At-Once or incremental for a DVD-R/-RW
    private bool _dvdTrackClosed; // CLOSE TRACK received for the flushed DVD track

    // ---- Data discs (READ (10)) ----

    /// <summary>
    /// The 2,048-byte user data of a recorded data disc, from LBA 0. READ (10) reads it; null means no data disc.
    /// <see cref="ReadErrorAtLba"/> and <see cref="ReadBackDifferenceAtLba"/> apply to it too.
    /// </summary>
    public byte[]? DataDiscImage { get; set; }

    /// <summary>READ (10) with the FUA bit set fails with 5/24/00 (invalid field in CDB), as some drives do.</summary>
    public bool RejectsFua { get; set; }

    /// <summary>
    /// What the drive's memory still holds, by LBA: a READ (10) without FUA returns these bytes instead of the disc's,
    /// like a drive that kept the written data in its buffer. The entries are dropped once
    /// <see cref="ReadCacheCapacitySectors"/> other sectors have been read without FUA, or on <see cref="Reload"/>.
    /// </summary>
    public Dictionary<int, byte[]> StaleReadCache { get; } = new();

    /// <summary>How many other sectors must be read before <see cref="StaleReadCache"/> is pushed out.</summary>
    public int ReadCacheCapacitySectors { get; set; } = 1024;

    /// <summary>An adapter that passes less per command than the usual 64 KiB.</summary>
    public int? MaxTransferBytesOverride { get; set; }

    /// <summary>The buffer size the drive reports with READ BUFFER CAPACITY.</summary>
    public int BufferCapacityBytes { get; set; } = 4 * 1024 * 1024;

    /// <summary>Every READ (10) received: start, count and whether FUA was set.</summary>
    public List<(int Lba, int Count, bool Fua)> Read10Log { get; } = new();

    private int _cacheMissesSinceFill;

    // ---- What happened ----

    public List<byte> CommandsReceived { get; } = new();
    public MemoryStream LeadInWritten { get; } = new();
    public MemoryStream ProgramAreaWritten { get; } = new();
    public byte[]? AcceptedCueSheet => _cue?.Raw;
    public int WriteCommandCount { get; private set; }
    public bool SessionClosed { get; private set; }
    public ReadOnlySpan<byte> CurrentModePage05 => _modePage05;

    /// <summary>True once audio has really been recorded (not just test-written).</summary>
    public bool Recorded { get; private set; }

    /// <summary>The last write speed set with SET CD SPEED, in kB/s.</summary>
    public int? WriteSpeedSet { get; private set; }

    /// <summary>Tracks of the recorded disc: number, INDEX 01 address and cue sheet control bits.</summary>
    public IReadOnlyList<(byte TrackNumber, int Index01Lba, byte CueControl)> RecordedTracks => _recordedTracks;

    public int RecordedLeadOutLba { get; private set; }

    /// <summary>The cue sheet the recorded disc was written from.</summary>
    public byte[]? RecordedCueSheet { get; private set; }

    private List<(byte TrackNumber, int Index01Lba, byte CueControl)> _recordedTracks = new();
    private bool _recordedCdText;

    /// <summary>Ejecting and reloading the disc: the drive forgets a simulated session.</summary>
    public void Reload()
    {
        if (!Recorded)
        {
            DiscStatus = 0;
        }
        _cue = null;
        StaleReadCache.Clear();
    }

    public ScsiExecutionResult ExecuteCommand(ReadOnlySpan<byte> cdb, Span<byte> dataBuffer, byte dataDirection, TimeSpan timeout)
    {
        byte op = cdb[0];
        CommandsReceived.Add(op);

        if (dataBuffer.Length > (MaxTransferBytesOverride ?? MaxTransferBytes))
        {
            return ScsiExecutionResult.Failed(87, "The parameter is incorrect (transfer larger than the adapter allows).");
        }

        if (!MediaPresent && op is not (0x12 or 0x46 or 0x5A or 0x55 or 0x5C))
        {
            return Sense(0x02, 0x3A, 0x00);
        }

        return op switch
        {
            0x00 => TestUnitReady(),
            0x46 => GetConfiguration(cdb, dataBuffer),
            0x51 => ReadDiscInformation(dataBuffer),
            0x5A => ModeSense(cdb, dataBuffer),
            NativeWriteCommands.OpCodeModeSelect10 => ModeSelect(dataBuffer),
            NativeWriteCommands.OpCodeSendCueSheet => SendCueSheet(cdb, dataBuffer),
            NativeWriteCommands.OpCodeWrite10 => Write(cdb, dataBuffer),
            NativeWriteCommands.OpCodeReadBufferCapacity => ReadBufferCapacity(dataBuffer),
            0x35 => SynchronizeCache(),
            0xBE => ReadCd(cdb, dataBuffer),
            0x28 => Read10(cdb, dataBuffer),
            0x43 => ReadToc(cdb, dataBuffer),
            0x42 => ReadSubChannel(cdb, dataBuffer),
            MmcMediaReport.OpCodeReadFormatCapacities => CannedResponse(FormatCapacitiesResponse, dataBuffer),
            MmcMediaReport.OpCodeGetPerformance when cdb.Length >= 11 && cdb[10] == MmcMediaReport.PerformanceTypeWriteSpeed
                => CannedResponse(WriteSpeedPerformanceResponse, dataBuffer),
            0xBB => SetCdSpeed(cdb),
            MmcCommandBuilder.OpCodeReadTrackInformation => ReadTrackInformation(cdb, dataBuffer),
            NativeWriteCommands.OpCodeCloseTrackSession => CloseTrackSession(cdb),
            NativeWriteCommands.OpCodeReserveTrack => ReserveTrack(cdb),
            NativeWriteCommands.OpCodeFormatUnit => FormatUnit(cdb, dataBuffer),
            NativeWriteCommands.OpCodeSetStreaming => SetStreaming(cdb, dataBuffer),
            _ => Sense(0x05, 0x20, 0x00) // invalid command operation code
        };
    }

    public void Dispose()
    {
    }

    /// <summary>Raw READ FORMAT CAPACITIES reply; null makes the drive refuse the command (5/20/00), as many do for write-once discs.</summary>
    public byte[]? FormatCapacitiesResponse { get; set; }

    /// <summary>Raw GET PERFORMANCE type 03h reply; null makes the drive refuse it.</summary>
    public byte[]? WriteSpeedPerformanceResponse { get; set; }

    private ScsiExecutionResult CannedResponse(byte[]? response, Span<byte> data)
    {
        if (response == null)
        {
            return Sense(0x05, 0x20, 0x00);
        }
        int length = Math.Min(response.Length, data.Length);
        response.AsSpan(0, length).CopyTo(data);
        return Good((uint)length);
    }

    private ScsiExecutionResult TestUnitReady()
    {
        if (NotReadyResponses > 0)
        {
            NotReadyResponses--;
            return Sense(0x02, 0x04, 0x01);
        }
        return Good(0);
    }

    private ScsiExecutionResult GetConfiguration(ReadOnlySpan<byte> cdb, Span<byte> data)
    {
        int rt = cdb[1] & 0x03;
        ushort start = BinaryPrimitives.ReadUInt16BigEndian(cdb.Slice(2, 2));
        var descriptors = new List<byte[]>();

        void Add(ushort code, byte flags, bool current)
        {
            if (rt == 2 ? code != start : code < start) return;
            descriptors.Add(new byte[] { (byte)(code >> 8), (byte)code, (byte)(0x04 | (current ? 0x01 : 0x00)), 4, flags, 0, 0, 0 });
        }

        bool cd = Profile is NativeSaoAudioWriter.ProfileCdR or NativeSaoAudioWriter.ProfileCdRw;
        bool dvdMinus = Profile is NativeTaoDataWriter.ProfileDvdMinusR or NativeTaoDataWriter.ProfileDvdMinusRwSequential
            or NativeTaoDataWriter.ProfileDvdMinusRDualLayer or NativeTaoDataWriter.ProfileDvdMinusRDualLayerJump;
        Add(NativeTaoDataWriter.FeatureIncrementalStreaming, 0, dvdMinus && OffersIncremental);
        Add(MmcCommandBuilder.FeatureTrackAtOnce, (byte)((OffersBufferUnderrunFree ? 0x40 : 0) | (OffersTestWrite ? 0x04 : 0)), cd);
        if (OffersSessionAtOnce)
        {
            byte flags = (byte)((OffersBufferUnderrunFree ? 0x40 : 0) | 0x20 | (OffersTestWrite ? 0x04 : 0) | 0x02 | (OffersRwSubchannel ? 0x01 : 0));
            Add(MmcCommandBuilder.FeatureCdMastering, flags, cd);
        }
        Add(MmcCommandBuilder.FeatureDvdRecordableWrite,
            (byte)((OffersBufferUnderrunFree ? 0x40 : 0) | (OffersTestWrite ? 0x04 : 0) | (Profile == NativeTaoDataWriter.ProfileDvdMinusRwSequential ? 0x02 : 0)),
            dvdMinus);

        int length = 8 + descriptors.Sum(d => d.Length);
        byte[] response = new byte[length];
        BinaryPrimitives.WriteUInt32BigEndian(response, (uint)(length - 4));
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(6), Profile);
        int o = 8;
        foreach (var d in descriptors)
        {
            d.CopyTo(response, o);
            o += d.Length;
        }
        return CopyOut(response, data);
    }

    private ScsiExecutionResult ReadDiscInformation(Span<byte> data)
    {
        byte[] r = new byte[34];
        BinaryPrimitives.WriteUInt16BigEndian(r, 32);
        r[2] = (byte)(DiscStatus & 0x03 | (Profile is NativeSaoAudioWriter.ProfileCdRw or NativeTaoDataWriter.ProfileDvdMinusRwSequential ? 0x10 : 0));
        r[3] = 1; r[4] = 1; r[5] = 1; r[6] = 1;
        if (IsDvdPlusRw)
        {
            r[7] = (byte)(BackgroundFormatStatus & 0x03);
        }
        r[17] = LeadInStart.Minute; r[18] = LeadInStart.Second; r[19] = LeadInStart.Frame;
        r[21] = LastLeadOutStart.Minute; r[22] = LastLeadOutStart.Second; r[23] = LastLeadOutStart.Frame;
        return CopyOut(r, data);
    }

    private ScsiExecutionResult ModeSense(ReadOnlySpan<byte> cdb, Span<byte> data)
    {
        if ((cdb[2] & 0x3F) != WriteParametersPage.PageCode)
        {
            return Sense(0x05, 0x24, 0x00);
        }

        byte[] page = (byte[])_modePage05.Clone();
        page[0] |= 0x80; // PS: the page can be saved
        if (RecordsDuringTestWrite && _testWriteRequested)
        {
            page[2] |= 0x10;
        }

        byte[] r = new byte[8 + page.Length];
        BinaryPrimitives.WriteUInt16BigEndian(r, (ushort)(r.Length - 2));
        page.CopyTo(r, 8);
        return CopyOut(r, data);
    }

    private bool _testWriteRequested;

    private ScsiExecutionResult ModeSelect(ReadOnlySpan<byte> list)
    {
        if (list.Length < 8 + 2)
        {
            return Sense(0x05, 0x1A, 0x00); // parameter list length error
        }
        if (IsDvdPlusR || IsDvdPlusRw)
        {
            // libburn's cookbook: no mode page 05h is sent for DVD+R (nor needed for DVD+RW). Stricter than most
            // drives, to catch one being sent.
            return Sense(0x05, 0x26, 0x00);
        }

        int blockDescriptorLength = BinaryPrimitives.ReadUInt16BigEndian(list.Slice(6, 2));
        int offset = 8 + blockDescriptorLength;
        if (offset + 2 > list.Length || (list[offset] & 0x3F) != WriteParametersPage.PageCode)
        {
            return Sense(0x05, 0x26, 0x00);
        }
        if ((list[offset] & 0x80) != 0)
        {
            return Sense(0x05, 0x26, 0x00); // PS must be zero in MODE SELECT
        }

        int pageLength = list[offset + 1] + 2;
        if (pageLength != _modePage05.Length || offset + pageLength > list.Length)
        {
            return Sense(0x05, 0x26, 0x00);
        }

        byte[] page = list.Slice(offset, pageLength).ToArray();
        byte writeType = (byte)(page[2] & 0x0F);
        byte blockType = (byte)(page[4] & 0x0F);
        if (writeType > 3 || blockType is > 3 and < 8 || (blockType == 3 && RejectsDataBlockType3))
        {
            return Sense(0x05, 0x26, 0x00);
        }
        if ((page[2] & 0x10) != 0 && !OffersTestWrite)
        {
            return Sense(0x05, 0x26, 0x00);
        }

        if (writeType == WriteParametersPage.WriteTypeTrackAtOnce && blockType == WriteParametersPage.DataBlockTypeMode1 &&
            (page[3] & 0x0F) != WriteParametersPage.TrackModeData)
        {
            return Sense(0x05, 0x26, 0x00); // Mode 1 blocks need a data track mode
        }
        if (IsDvd)
        {
            // DVD-R/-RW: Disc-At-Once (2) or incremental (0), track mode 5, data block type 8; fixed packets of 16
            // blocks for incremental, none for Disc-At-Once (libburn's cookbook).
            bool dao = writeType == WriteParametersPage.WriteTypeSessionAtOnce;
            bool incremental = writeType == WriteParametersPage.WriteTypeIncremental;
            bool fixedPackets = (page[3] & 0x20) != 0;
            int packetSize = BinaryPrimitives.ReadInt32BigEndian(page.AsSpan(10, 4));
            if (!(dao || incremental) || blockType != WriteParametersPage.DataBlockTypeMode1 || (page[3] & 0x0F) != WriteParametersPage.TrackModeDvd ||
                (dao && fixedPackets) || (incremental && (!fixedPackets || packetSize != WriteParametersPage.DvdPacketBlocks || !OffersIncremental)))
            {
                return Sense(0x05, 0x26, 0x00);
            }
        }

        _testWriteRequested = (page[2] & 0x10) != 0;
        if (DropsTestWriteBit || RecordsDuringTestWrite)
        {
            page[2] &= unchecked((byte)~0x10);
        }

        _modePage05 = page;
        _dvdModeSet = IsDvd;
        ReservedSectors = 0;
        _cue = null; // new parameters need a new cue sheet
        _taoArmed = writeType == WriteParametersPage.WriteTypeTrackAtOnce;
        return Good(0);
    }

    private bool TestWriteActive => (_modePage05[2] & 0x10) != 0;

    private ScsiExecutionResult SendCueSheet(ReadOnlySpan<byte> cdb, ReadOnlySpan<byte> sheet)
    {
        int length = (cdb[6] << 16) | (cdb[7] << 8) | cdb[8];
        if (length != sheet.Length || length == 0 || length % 8 != 0)
        {
            return Sense(0x05, 0x24, 0x00);
        }
        if ((_modePage05[2] & 0x0F) != WriteParametersPage.WriteTypeSessionAtOnce)
        {
            return Sense(0x05, 0x2C, 0x00); // command sequence error: not in SAO mode
        }
        if (DiscStatus != 0)
        {
            // The GP60NB50 answers 5/30/06 here while it shows the simulated session of a test write.
            return Sense(0x05, 0x30, 0x06);
        }

        var parsed = CueSheetState.Parse(sheet, this);
        if (parsed == null)
        {
            return Sense(0x05, 0x26, 0x00);
        }
        if (parsed.LeadOutLba > LastLeadOutStart.ToLba())
        {
            return Sense(0x05, 0x21, 0x00);
        }

        _cue = parsed;
        _nextLba = parsed.CdText ? LeadInStart.ToLba() : SaoAudioLayout.FirstWritableLba;
        SessionClosed = false;
        return Good(0);
    }

    private ScsiExecutionResult Write(ReadOnlySpan<byte> cdb, ReadOnlySpan<byte> data)
    {
        WriteCommandCount++;
        if (_cue == null && IsDvdPlusRw)
        {
            return WriteOverwrite(cdb, data);
        }
        if (_cue == null && IsDvd)
        {
            return WriteDvd(cdb, data);
        }
        if (_cue == null && _taoArmed)
        {
            return WriteTrackAtOnce(cdb, data);
        }
        if (_cue == null)
        {
            return Sense(0x05, 0x2C, 0x00);
        }

        int lba = BinaryPrimitives.ReadInt32BigEndian(cdb.Slice(2, 4));
        int count = BinaryPrimitives.ReadUInt16BigEndian(cdb.Slice(7, 2));
        if (lba != _nextLba)
        {
            return Sense(0x05, 0x21, 0x02); // invalid address for write
        }
        if (count == 0)
        {
            return Good(0);
        }

        bool leadIn = lba < SaoAudioLayout.FirstWritableLba;
        int blockSize = leadIn ? CdTextLeadInEncoder.SubChannelBytesPerSector : 2352;
        if (leadIn && lba + count > SaoAudioLayout.FirstWritableLba)
        {
            return Sense(0x05, 0x24, 0x00); // one WRITE may not cross from the lead-in into the program area
        }
        if (data.Length != count * blockSize)
        {
            return Sense(0x05, 0x24, 0x00);
        }
        if (!leadIn && lba + count > _cue.LeadOutLba)
        {
            return Sense(0x05, 0x21, 0x00);
        }

        if (BufferFullEveryNWrites > 0 && ++_writesSinceBufferFull >= BufferFullEveryNWrites)
        {
            _writesSinceBufferFull = 0;
            WriteCommandCount--;
            return Sense(0x02, 0x04, 0x08);
        }
        if (WriteErrorAtLba is int bad && bad >= lba && bad < lba + count)
        {
            return Sense(0x03, 0x0C, 0x00);
        }

        if (CaptureWrites)
        {
            (leadIn ? LeadInWritten : ProgramAreaWritten).Write(data);
        }
        _nextLba = lba + count;
        return Good((uint)data.Length);
    }

    /// <summary>
    /// READ CD of CD-DA user data. Unrecorded sectors cannot be read (5/21/00 here; real drives vary), and neither
    /// can anything from the lead-out on.
    /// </summary>
    private ScsiExecutionResult ReadCd(ReadOnlySpan<byte> cdb, Span<byte> data)
    {
        int lba = BinaryPrimitives.ReadInt32BigEndian(cdb.Slice(2, 4));
        int count = (cdb[6] << 16) | (cdb[7] << 8) | cdb[8];
        if (!Recorded || lba < 0 || count == 0 || data.Length < count * 2352 || lba + count > RecordedLeadOutLba)
        {
            return Sense(0x05, 0x21, 0x00);
        }
        if (ReadErrorAtLba is int bad && bad >= lba && bad < lba + count)
        {
            return Sense(0x03, 0x11, 0x00);
        }

        // The program area was captured from LBA -150. Sample i read back is sample i - offset as written.
        ReadOnlySpan<byte> program = ProgramAreaWritten.GetBuffer().AsSpan(0, (int)ProgramAreaWritten.Length);
        int n = count * 2352;
        long source = (long)(lba + 150) * 2352 - (long)ReadOffsetSamples * 4;
        Span<byte> destination = data.Slice(0, n);
        destination.Clear();
        long from = Math.Max(source, 0);
        long to = Math.Min(source + n, program.Length);
        if (to > from)
        {
            program.Slice((int)from, (int)(to - from)).CopyTo(destination.Slice((int)(from - source)));
        }

        if (ReadBackDifferenceAtLba is int changed && changed >= lba && changed < lba + count)
        {
            destination[(changed - lba) * 2352 + 100] ^= 0x01;
        }
        return Good((uint)n);
    }

    /// <summary>
    /// READ (10) of a data disc (<see cref="DataDiscImage"/>). Without FUA, sectors held in
    /// <see cref="StaleReadCache"/> come from there instead of the disc.
    /// </summary>
    private ScsiExecutionResult Read10(ReadOnlySpan<byte> cdb, Span<byte> data)
    {
        int lba = BinaryPrimitives.ReadInt32BigEndian(cdb.Slice(2, 4));
        int count = (cdb[7] << 8) | cdb[8];
        bool fua = (cdb[1] & MmcCommandBuilder.Read10ForceUnitAccess) != 0;
        Read10Log.Add((lba, count, fua));

        if (fua && RejectsFua)
        {
            return Sense(0x05, 0x24, 0x00);
        }
        byte[]? image = DataDiscImage;
        if (image == null && IsDvd && DiscStatus == 2)
        {
            return Sense(0x03, 0x11, 0x00); // the simulated closed disc after a test write: nothing to read
        }
        int sectors = image == null ? 0 : image.Length / 2048;
        if (image == null || lba < 0 || count == 0 || data.Length < count * 2048 || lba + count > sectors)
        {
            return Sense(0x05, 0x21, 0x00);
        }
        if (ReadErrorAtLba is int bad && bad >= lba && bad < lba + count)
        {
            return Sense(0x03, 0x11, 0x00);
        }

        for (int i = 0; i < count; i++)
        {
            Span<byte> destination = data.Slice(i * 2048, 2048);
            if (!fua && StaleReadCache.TryGetValue(lba + i, out byte[]? cached))
            {
                cached.AsSpan(0, 2048).CopyTo(destination);
                continue;
            }

            image.AsSpan((lba + i) * 2048, 2048).CopyTo(destination);
            if (ReadBackDifferenceAtLba == lba + i)
            {
                destination[100] ^= 0x01;
            }
            if (!fua && StaleReadCache.Count > 0 && ++_cacheMissesSinceFill >= ReadCacheCapacitySectors)
            {
                StaleReadCache.Clear();
                _cacheMissesSinceFill = 0;
            }
        }
        return Good((uint)(count * 2048));
    }

    /// <summary>READ TOC/PMA/ATIP: format 0 (track list) and format 5 (CD-Text) of a recorded disc.</summary>
    private ScsiExecutionResult ReadToc(ReadOnlySpan<byte> cdb, Span<byte> data)
    {
        if (!Recorded)
        {
            return Sense(0x05, 0x24, 0x00);
        }

        int format = cdb[2] & 0x0F;
        if (format == 0x00)
        {
            byte[] r = new byte[4 + 8 * (_recordedTracks.Count + 1)];
            BinaryPrimitives.WriteUInt16BigEndian(r, (ushort)(r.Length - 2));
            r[2] = _recordedTracks[0].TrackNumber;
            r[3] = _recordedTracks[^1].TrackNumber;
            for (int i = 0; i <= _recordedTracks.Count; i++)
            {
                bool leadOut = i == _recordedTracks.Count;
                var entry = r.AsSpan(4 + 8 * i, 8);
                int control = leadOut || DropsControlBits ? 0 : _recordedTracks[i].CueControl >> 4;
                entry[1] = (byte)(0x10 | control); // ADR 1, then the control bits
                entry[2] = leadOut ? SaoCueSheetBuilder.LeadOutTrackNumber : _recordedTracks[i].TrackNumber;
                int lba = leadOut ? RecordedLeadOutLba : _recordedTracks[i].Index01Lba + (ShiftsTrackStartsInToc && i > 0 ? 1 : 0);
                BinaryPrimitives.WriteInt32BigEndian(entry.Slice(4, 4), lba);
            }
            return CopyOut(r, data);
        }

        if (format == 0x05)
        {
            byte[] packs = _recordedCdText && !LosesCdText ? DecodeLeadInPacks() : [];
            byte[] r = new byte[4 + packs.Length];
            BinaryPrimitives.WriteUInt16BigEndian(r, (ushort)(r.Length - 2));
            packs.CopyTo(r, 4);
            return CopyOut(r, data);
        }

        return Sense(0x05, 0x24, 0x00);
    }

    /// <summary>
    /// Recovers the CD-Text packs from the R-W sub-channel bytes that were written to the lead-in, the way a drive
    /// reads them: sector by sector until the first pack comes round again.
    /// </summary>
    private byte[] DecodeLeadInPacks()
    {
        ReadOnlySpan<byte> leadIn = LeadInWritten.GetBuffer().AsSpan(0, (int)LeadInWritten.Length);
        var packs = new List<byte>();
        Span<byte> group = stackalloc byte[72];
        for (int sector = 0; (sector + 1) * 96 <= leadIn.Length; sector++)
        {
            CdTextLeadInEncoder.GatherFromRw(leadIn.Slice(sector * 96, 96), group);
            for (int p = 0; p < 4; p++)
            {
                var pack = group.Slice(p * 18, 18);
                if (packs.Count >= 18 && pack.Slice(0, 4).SequenceEqual(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(packs).Slice(0, 4)))
                {
                    return packs.ToArray();
                }
                packs.AddRange(pack);
            }
        }
        return packs.ToArray();
    }

    /// <summary>
    /// READ SUB-CHANNEL formats 2 (catalog number) and 3 (ISRC): answered from the cue sheet of the recorded disc,
    /// the way a drive answers from the Q sub-channel it wrote.
    /// </summary>
    private ScsiExecutionResult ReadSubChannel(ReadOnlySpan<byte> cdb, Span<byte> data)
    {
        if (RejectsReadSubChannel)
        {
            return Sense(0x05, 0x20, 0x00);
        }
        if (!Recorded || RecordedCueSheet is not { } sheet)
        {
            return Sense(0x05, 0x64, 0x00); // illegal mode for this track: nothing recorded
        }

        byte format = cdb[3];
        byte track = cdb[6];
        if (format is not (0x02 or 0x03))
        {
            return Sense(0x05, 0x24, 0x00);
        }

        string code = string.Empty;
        for (int i = 0; i + 16 <= sheet.Length; i += 8)
        {
            int adr = sheet[i] & 0x0F;
            if (format == 0x02 && adr == 0x02 && (sheet[i + 8] & 0x0F) == 0x02)
            {
                code = System.Text.Encoding.ASCII.GetString(sheet, i + 1, 7) + System.Text.Encoding.ASCII.GetString(sheet, i + 9, 6);
                break;
            }
            if (format == 0x03 && adr == 0x03 && sheet[i + 1] == track && (sheet[i + 8] & 0x0F) == 0x03)
            {
                code = System.Text.Encoding.ASCII.GetString(sheet, i + 2, 6) + System.Text.Encoding.ASCII.GetString(sheet, i + 10, 6);
                break;
            }
        }

        if (LosesCatalogAndIsrc)
        {
            code = string.Empty;
        }
        if (format == 0x03 && ChangesIsrcOfTrack == track && code.Length == 12)
        {
            code = code[..11] + (code[11] == '9' ? '0' : '9');
        }

        byte[] response = new byte[24];
        response[1] = 0x15; // audio status: no current status to return
        response[3] = 20;
        response[4] = format;
        if (format == 0x03)
        {
            response[5] = 0x10; // ADR 1, no control bits
            response[6] = track;
        }
        if (code.Length > 0)
        {
            response[8] = 0x80; // MCVal / TCVal
            System.Text.Encoding.ASCII.GetBytes(code, response.AsSpan(9));
        }
        return CopyOut(response, data);
    }

    private ScsiExecutionResult SetCdSpeed(ReadOnlySpan<byte> cdb)
    {
        if (RejectsSetSpeed)
        {
            return Sense(0x05, 0x24, 0x00);
        }
        WriteSpeedSet = BinaryPrimitives.ReadUInt16BigEndian(cdb.Slice(4, 2));
        return Good(0);
    }

    private ScsiExecutionResult ReadBufferCapacity(Span<byte> data)
    {
        byte[] r = new byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(r, 10);
        BinaryPrimitives.WriteUInt32BigEndian(r.AsSpan(4), (uint)BufferCapacityBytes);
        BinaryPrimitives.WriteUInt32BigEndian(r.AsSpan(8), 512 * 1024);
        return CopyOut(r, data);
    }

    private ScsiExecutionResult SynchronizeCache()
    {
        if ((_taoArmed || IsDvd) && _taoNextLba > 0 && _taoClosedSectors == 0)
        {
            // The drive empties its buffer and writes the run-out; the track is complete.
            if (_taoTestWrite)
            {
                if (ShowsSimulatedSessionAfterTestWrite)
                {
                    // The GP60NB50 shows an open session after a CD test write and a closed disc after a DVD-R
                    // Disc-At-Once one, whose sectors then fail with 3/11/00 (measured 2026-11-11).
                    DiscStatus = IsDvd && ReservedSectors > 0 ? 2 : 1;
                }
                DataTrackWritten.SetLength(0);
            }
            else if (IsDvd && ReservedSectors > 0)
            {
                // DVD-R Disc-At-Once: the drive closes the disc itself; a short track is padded to the reservation.
                RecordDataDisc(Math.Max(_taoNextLba, ReservedSectors), runOutSectors: 0, discStatus: 2);
            }
            else
            {
                _taoClosedSectors = _taoNextLba;
                DiscStatus = 1; // an open session until it is closed
                DataDiscImage = DataTrackWritten.ToArray(); // the recorded track reads back even before the close
            }
            ReservedSectors = 0;
            _taoNextLba = 0;
            _taoArmed = false;
            return Good(0);
        }

        if (_cue != null && _nextLba == _cue.LeadOutLba)
        {
            bool recorded = !TestWriteActive || RecordsDuringTestWrite;
            if (recorded)
            {
                Recorded = true;
                DiscStatus = ClosesDiscAfterWrite ? 2 : 1;
                _recordedTracks = _cue.Tracks;
                _recordedCdText = _cue.CdText;
                RecordedLeadOutLba = _cue.LeadOutLba;
                RecordedCueSheet = _cue.Raw;
            }
            else if (ShowsSimulatedSessionAfterTestWrite)
            {
                DiscStatus = 1;
            }
            SessionClosed = true;
            _cue = null;
        }
        else if (_cue != null && _nextLba > _cue.FirstWriteLba)
        {
            // Stopped part way. A real write would leave a ruined CD-R; a test write leaves it blank.
            if (!TestWriteActive || RecordsDuringTestWrite)
            {
                Recorded = true;
                DiscStatus = 2;
            }
            _cue = null;
        }
        return Good(0);
    }

    private ScsiExecutionResult WriteTrackAtOnce(ReadOnlySpan<byte> cdb, ReadOnlySpan<byte> data)
    {
        int lba = BinaryPrimitives.ReadInt32BigEndian(cdb.Slice(2, 4));
        int count = BinaryPrimitives.ReadUInt16BigEndian(cdb.Slice(7, 2));
        if (_taoNextLba == 0 && DiscStatus != 0)
        {
            return Sense(0x05, 0x21, 0x02); // not blank: no writable address
        }
        if (lba != _taoNextLba)
        {
            return Sense(0x05, 0x21, 0x02);
        }
        if (count == 0)
        {
            return Good(0);
        }
        int blockSize = (_modePage05[4] & 0x0F) == WriteParametersPage.DataBlockTypeMode1 ? 2048 : 2352;
        if (data.Length != count * blockSize)
        {
            return Sense(0x05, 0x24, 0x00);
        }
        if (lba + count > Capacity)
        {
            return Sense(0x05, 0x21, 0x00);
        }
        if (BufferFullEveryNWrites > 0 && ++_writesSinceBufferFull >= BufferFullEveryNWrites)
        {
            _writesSinceBufferFull = 0;
            WriteCommandCount--;
            return Sense(0x02, 0x04, 0x08);
        }
        if (WriteErrorAtLba is int bad && bad >= lba && bad < lba + count)
        {
            return Sense(0x03, 0x0C, 0x00);
        }

        if (lba == 0)
        {
            DataTrackWritten.SetLength(0);
            // A drive that ignores the test-write bit (RecordsDuringTestWrite) records for real.
            _taoTestWrite = TestWriteActive && !RecordsDuringTestWrite;
        }
        if (CaptureWrites)
        {
            DataTrackWritten.Write(data);
        }
        _taoNextLba = lba + count;
        return Good((uint)data.Length);
    }

    /// <summary>READ TRACK INFORMATION (36 bytes): the invisible track of a blank disc, or track 1 once written.</summary>
    private ScsiExecutionResult ReadTrackInformation(ReadOnlySpan<byte> cdb, Span<byte> data)
    {
        if (RejectsReadTrackInformation)
        {
            return Sense(0x05, 0x20, 0x00);
        }
        int number = BinaryPrimitives.ReadInt32BigEndian(cdb.Slice(2, 4));
        if ((cdb[1] & 0x03) != 0x01 || number is not (1 or 0xFF))
        {
            return Sense(0x05, 0x24, 0x00);
        }

        int capacity = Capacity;
        byte[] r = new byte[36];
        BinaryPrimitives.WriteUInt16BigEndian(r, 34);
        r[2] = 1; r[3] = 1; // track 1, session 1
        if (Recorded || _taoClosedSectors > 0 || DiscStatus != 0)
        {
            int size = Recorded ? RecordedLeadOutLba : _taoClosedSectors + (IsDvd ? 0 : 2);
            r[5] = 0x04; // data track
            r[6] = 0x01; // data mode 1, not blank, no next writable address
            BinaryPrimitives.WriteInt32BigEndian(r.AsSpan(24), size);
        }
        else
        {
            int nwa = NextWritableAddressOverride ?? _taoNextLba;
            r[6] = 0x40 | 0x01; // blank
            r[7] = 0x01;        // next writable address valid
            BinaryPrimitives.WriteInt32BigEndian(r.AsSpan(12), nwa);
            BinaryPrimitives.WriteInt32BigEndian(r.AsSpan(16), Math.Max(0, capacity - nwa));
            BinaryPrimitives.WriteInt32BigEndian(r.AsSpan(24), capacity);
        }
        return CopyOut(r, data);
    }

    /// <summary>
    /// CLOSE TRACK/SESSION. Closing the session of a flushed Track-at-Once track records the disc: READ (10) then reads
    /// the data back and READ TOC lists one data track. Multi-session 11b in the write parameters leaves it appendable.
    /// </summary>
    private ScsiExecutionResult CloseTrackSession(ReadOnlySpan<byte> cdb)
    {
        byte function = (byte)(cdb[2] & 0x07);
        LastCloseFunction = function;
        if (RejectsCloseSession)
        {
            return Sense(0x03, 0x0C, 0x00);
        }
        if (IsDvd)
        {
            return CloseDvd(cdb, function);
        }
        if (function != NativeWriteCommands.CloseFunctionSession || _taoClosedSectors == 0)
        {
            return Sense(0x05, 0x2C, 0x00); // command sequence error: nothing to close
        }

        RecordDataDisc(_taoClosedSectors, runOutSectors: 2, discStatus: (_modePage05[3] >> 6) == 3 ? 1 : 2);
        if ((cdb[1] & 0x01) != 0)
        {
            NotReadyResponses += CloseSessionBusyPolls;
        }
        return Good(0);
    }

    /// <summary>One data track from LBA 0 is now on the disc: READ (10) reads it and READ TOC lists it.</summary>
    private void RecordDataDisc(int sectors, int runOutSectors, int discStatus)
    {
        byte[] image = DataTrackWritten.ToArray();
        if (image.Length < sectors * 2048)
        {
            Array.Resize(ref image, sectors * 2048);
        }
        DataDiscImage = image;
        Recorded = true;
        _recordedTracks = new() { (1, 0, 0x40) };
        _recordedCdText = false;
        RecordedLeadOutLba = sectors + runOutSectors; // CD Track-at-Once ends with two run-out blocks; DVD with none
        DiscStatus = discStatus;
        _taoClosedSectors = 0;
        SessionClosed = true;
    }

    /// <summary>
    /// WRITE (10) on a DVD: DVD-R/-RW need write parameters first (and a reservation for Disc-At-Once, which the write
    /// may not pass); DVD+R takes data without either. Every WRITE carries whole 16-block (32 KiB) packets.
    /// </summary>
    private ScsiExecutionResult WriteDvd(ReadOnlySpan<byte> cdb, ReadOnlySpan<byte> data)
    {
        if (!IsDvdPlusR && !_dvdModeSet)
        {
            return Sense(0x05, 0x2C, 0x00);
        }
        bool dao = !IsDvdPlusR && (_modePage05[2] & 0x0F) == WriteParametersPage.WriteTypeSessionAtOnce;
        if (dao && ReservedSectors == 0)
        {
            return Sense(0x05, 0x2C, 0x00); // Disc-At-Once needs RESERVE TRACK first
        }
        int lba = BinaryPrimitives.ReadInt32BigEndian(cdb.Slice(2, 4));
        int count = BinaryPrimitives.ReadUInt16BigEndian(cdb.Slice(7, 2));
        if (count % WriteParametersPage.DvdPacketBlocks != 0)
        {
            return Sense(0x05, 0x24, 0x00);
        }
        if (dao && lba + count > ReservedSectors)
        {
            return Sense(0x05, 0x21, 0x00);
        }
        return WriteTrackAtOnce(cdb, data);
    }

    /// <summary>
    /// CLOSE TRACK/SESSION on a DVD: close the flushed track (001b), then close the session (010b; appendable on DVD+R,
    /// and on DVD-R with multi-session 11b) or, on DVD+R only, finalize (101b).
    /// </summary>
    private ScsiExecutionResult CloseDvd(ReadOnlySpan<byte> cdb, byte function)
    {
        if (IsDvdPlusRw)
        {
            // CLOSE SESSION on a DVD+RW stops a background format (libburn's cookbook); nothing else is accepted.
            if (function != NativeWriteCommands.CloseFunctionSession)
            {
                return Sense(0x05, 0x24, 0x00);
            }
            if (BackgroundFormatStatus == 2)
            {
                BackgroundFormatStatus = 1;
            }
            SessionClosed = true;
            return Good(0);
        }

        if (function == NativeWriteCommands.CloseFunctionTrack)
        {
            int track = BinaryPrimitives.ReadUInt16BigEndian(cdb.Slice(4, 2));
            if (_taoClosedSectors == 0)
            {
                return Sense(0x05, 0x2C, 0x00);
            }
            if (track != 1)
            {
                return Sense(0x05, 0x24, 0x00);
            }
            _dvdTrackClosed = true;
            return Good(0);
        }

        bool finalize = function == NativeWriteCommands.CloseFunctionFinalizeDvdPlusR;
        if (function != NativeWriteCommands.CloseFunctionSession && !finalize)
        {
            return Sense(0x05, 0x24, 0x00);
        }
        if (!_dvdTrackClosed)
        {
            return Sense(0x05, 0x2C, 0x00); // the track has to be closed first
        }
        if (finalize && (!IsDvdPlusR || RejectsDvdPlusRFinalize))
        {
            return Sense(0x05, 0x24, 0x00);
        }
        if (!finalize && CloseSessionBusyResponses > 0)
        {
            CloseSessionBusyResponses--;
            return Sense(0x02, 0x04, 0x07);
        }

        int status = finalize ? 2 : IsDvdPlusR ? 1 : (_modePage05[3] >> 6) == 3 ? 1 : 2;
        RecordDataDisc(_taoClosedSectors, runOutSectors: 0, discStatus: status);
        _dvdTrackClosed = false;
        if ((cdb[1] & 0x01) != 0)
        {
            NotReadyResponses += CloseSessionBusyPolls;
        }
        return Good(0);
    }

    /// <summary>
    /// WRITE (10) on a DVD+RW: anywhere on the formatted disc, 2 KiB aligned; refused (5/30/10, medium not formatted)
    /// on a disc that was never formatted. What is written replaces what was there and reads back at once.
    /// </summary>
    private ScsiExecutionResult WriteOverwrite(ReadOnlySpan<byte> cdb, ReadOnlySpan<byte> data)
    {
        if (BackgroundFormatStatus == 0)
        {
            return Sense(0x05, 0x30, 0x10);
        }
        int lba = BinaryPrimitives.ReadInt32BigEndian(cdb.Slice(2, 4));
        int count = BinaryPrimitives.ReadUInt16BigEndian(cdb.Slice(7, 2));
        if (count == 0)
        {
            return Good(0);
        }
        if (data.Length != count * 2048)
        {
            return Sense(0x05, 0x24, 0x00);
        }
        if (lba < 0 || lba + count > Capacity)
        {
            return Sense(0x05, 0x21, 0x00);
        }
        if (BufferFullEveryNWrites > 0 && ++_writesSinceBufferFull >= BufferFullEveryNWrites)
        {
            _writesSinceBufferFull = 0;
            WriteCommandCount--;
            return Sense(0x02, 0x04, 0x08);
        }
        if (WriteErrorAtLba is int bad && bad >= lba && bad < lba + count)
        {
            return Sense(0x03, 0x0C, 0x00);
        }

        if (CaptureWrites)
        {
            byte[] image = DataDiscImage ?? Array.Empty<byte>();
            int end = (lba + count) * 2048;
            if (image.Length < end)
            {
                Array.Resize(ref image, end);
            }
            data.CopyTo(image.AsSpan(lba * 2048));
            DataDiscImage = image;
        }
        Recorded = true;
        _recordedTracks = new() { (1, 0, 0x40) };
        _recordedCdText = false;
        RecordedLeadOutLba = Math.Max(RecordedLeadOutLba, lba + count);
        return Good((uint)data.Length);
    }

    /// <summary>
    /// FORMAT UNIT on a DVD+RW: format type 26h with the libburn layout (CDB byte 1 0x11, list byte 3 = 8, type in
    /// byte 8 bits 7–2). Starts a background format; with Immed the drive is briefly not ready.
    /// </summary>
    private ScsiExecutionResult FormatUnit(ReadOnlySpan<byte> cdb, ReadOnlySpan<byte> list)
    {
        if (!IsDvdPlusRw)
        {
            return Sense(0x05, 0x20, 0x00); // only DVD+RW formatting is simulated
        }
        if (RejectsFormatUnit)
        {
            return Sense(0x05, 0x24, 0x00);
        }
        if (cdb[1] != 0x11 || list.Length < 12 || list[3] != 8 || (list[8] >> 2) != NativeWriteCommands.FormatTypeDvdPlusRw)
        {
            return Sense(0x05, 0x26, 0x00);
        }
        uint blocks = BinaryPrimitives.ReadUInt32BigEndian(list.Slice(4, 4));
        if (blocks != 0xFFFFFFFF && blocks > (uint)Capacity)
        {
            return Sense(0x05, 0x26, 0x00);
        }
        FormatUnitCount++;
        LastFormatParameters = list[..12].ToArray();
        BackgroundFormatStatus = 2;
        if ((list[1] & 0x02) != 0)
        {
            NotReadyResponses += 1;
        }
        return Good(0);
    }

    /// <summary>RESERVE TRACK (ARSV 0, RMZ 0) on a blank DVD-R/-RW set to Disc-At-Once: the size in CDB bytes 5–8.</summary>
    private ScsiExecutionResult ReserveTrack(ReadOnlySpan<byte> cdb)
    {
        if (RejectsReserveTrack)
        {
            return Sense(0x05, 0x24, 0x00);
        }
        bool dao = IsDvd && _dvdModeSet && (_modePage05[2] & 0x0F) == WriteParametersPage.WriteTypeSessionAtOnce;
        if (!dao || DiscStatus != 0 || ReservedSectors != 0 || _taoNextLba != 0)
        {
            return Sense(0x05, 0x2C, 0x00);
        }
        int size = BinaryPrimitives.ReadInt32BigEndian(cdb.Slice(5, 4));
        if ((cdb[1] & 0x03) != 0 || size <= 0 || size > Capacity)
        {
            return Sense(0x05, 0x24, 0x00);
        }
        ReservedSectors = size;
        return Good(0);
    }

    /// <summary>SET STREAMING type 0: one 28-byte performance descriptor; the write speed is write size (kB) over time (ms).</summary>
    private ScsiExecutionResult SetStreaming(ReadOnlySpan<byte> cdb, ReadOnlySpan<byte> data)
    {
        if (RejectsSetStreaming)
        {
            return Sense(0x05, 0x20, 0x00);
        }
        int length = BinaryPrimitives.ReadUInt16BigEndian(cdb.Slice(9, 2));
        if (cdb[8] != 0 || length != NativeWriteCommands.PerformanceDescriptorLength || data.Length < length)
        {
            return Sense(0x05, 0x24, 0x00);
        }
        int writeSize = BinaryPrimitives.ReadInt32BigEndian(data.Slice(20, 4));
        int writeTime = BinaryPrimitives.ReadInt32BigEndian(data.Slice(24, 4));
        if (writeSize <= 0 || writeTime <= 0)
        {
            return Sense(0x05, 0x26, 0x00);
        }
        LastPerformanceDescriptor = data[..length].ToArray();
        WriteSpeedSet = (int)((long)writeSize * 1000 / writeTime);
        return Good(0);
    }

    private static byte[] CreateDefaultModePage05()
    {
        byte[] p = new byte[0x34];
        p[0] = WriteParametersPage.PageCode;
        p[1] = 0x32;
        p[2] = 0x01; // write type TAO, as drives usually come up
        p[3] = 0x04; // track mode: data
        p[4] = 0x08; // data block type: Mode 1
        p[14] = 0x00; p[15] = 0x96; // audio pause length 150
        return p;
    }

    private static ScsiExecutionResult CopyOut(ReadOnlySpan<byte> response, Span<byte> data)
    {
        int n = Math.Min(response.Length, data.Length);
        response.Slice(0, n).CopyTo(data);
        return Good((uint)n);
    }

    private static ScsiExecutionResult Good(uint transferred) => ScsiExecutionResult.Success(transferred);

    private static ScsiExecutionResult Sense(byte key, byte asc, byte ascq)
    {
        byte[] sense = new byte[18];
        sense[0] = 0x70;
        sense[2] = key;
        sense[7] = 10;
        sense[12] = asc;
        sense[13] = ascq;
        return ScsiExecutionResult.CheckCondition(0x02, 0, sense);
    }

    /// <summary>A cue sheet checked the way a drive checks it.</summary>
    private sealed class CueSheetState
    {
        public required byte[] Raw { get; init; }
        public bool CdText { get; init; }
        public int LeadOutLba { get; init; }
        public int FirstWriteLba { get; init; }
        public List<(byte TrackNumber, int Index01Lba, byte CueControl)> Tracks { get; init; } = new();

        public static CueSheetState? Parse(ReadOnlySpan<byte> sheet, VirtualMmcDrive drive)
        {
            int entries = sheet.Length / 8;
            int i = 0;

            // Optional media catalog number: two ADR 2 entries of ASCII digits.
            if ((sheet[0] & 0x0F) == 0x02)
            {
                if (entries < 2 || (sheet[8] & 0x0F) != 0x02) return null;
                for (int k = 1; k < 8; k++) if (!char.IsAsciiDigit((char)sheet[k])) return null;
                for (int k = 9; k < 15; k++) if (!char.IsAsciiDigit((char)sheet[k])) return null;
                i = 2;
            }

            // Lead-in.
            if (i >= entries) return null;
            var leadIn = sheet.Slice(i * 8, 8);
            if ((leadIn[0] & 0x0F) != 0x01 || leadIn[1] != 0 || leadIn[2] != 0) return null;
            bool cdText;
            if (leadIn[3] == SaoCueSheetBuilder.DataFormLeadInWithRw)
            {
                if (!drive.OffersRwSubchannel) return null;
                var t = new Msf(leadIn[5], leadIn[6], leadIn[7]);
                bool zero = t == default;
                if (!zero && (t != drive.LeadInStart || drive.RejectsLeadInTimeInCueSheet)) return null;
                cdText = true;
            }
            else if (leadIn[3] == SaoCueSheetBuilder.DataFormGenerated)
            {
                cdText = false;
            }
            else
            {
                return null;
            }
            i++;

            // Tracks: ISRC pairs, INDEX 00 / 01 entries, times strictly increasing.
            var tracks = new List<(byte TrackNumber, int Index01Lba, byte CueControl)>();
            int expectedTrack = 1;
            int lastLba = int.MinValue;
            bool sawIndex01ForTrack = false;
            bool firstEntry = true;
            for (; i < entries - 1; i++)
            {
                var e = sheet.Slice(i * 8, 8);
                int adr = e[0] & 0x0F;
                if (adr == 0x03)
                {
                    if (e[1] != expectedTrack + (sawIndex01ForTrack ? 1 : 0) && e[1] != expectedTrack) return null;
                    for (int k = 2; k < 8; k++) if (!char.IsAsciiLetterOrDigit((char)e[k])) return null;
                    continue;
                }
                if (adr != 0x01 || e[3] != SaoCueSheetBuilder.DataFormAudio) return null;

                int tno = e[1];
                if (sawIndex01ForTrack && tno == expectedTrack + 1)
                {
                    expectedTrack++;
                    sawIndex01ForTrack = false;
                }
                if (tno != expectedTrack) return null;

                int lba = new Msf(e[5], e[6], e[7]).ToLba();
                if (firstEntry && (e[2] != 0 || lba != SaoAudioLayout.FirstWritableLba)) return null; // track 1 INDEX 00 at 00:00:00
                if (lba <= lastLba) return null;
                if (e[2] == 0 && sawIndex01ForTrack) return null;
                if (e[2] == 1)
                {
                    sawIndex01ForTrack = true;
                    tracks.Add(((byte)tno, lba, (byte)(e[0] & 0xF0)));
                }
                else if (e[2] != 0) return null;
                lastLba = lba;
                firstEntry = false;
            }
            if (!sawIndex01ForTrack) return null;

            var leadOut = sheet.Slice((entries - 1) * 8, 8);
            if ((leadOut[0] & 0x0F) != 0x01 || leadOut[1] != SaoCueSheetBuilder.LeadOutTrackNumber || leadOut[2] != 0x01 ||
                leadOut[3] != SaoCueSheetBuilder.DataFormGenerated)
            {
                return null;
            }
            int leadOutLba = new Msf(leadOut[5], leadOut[6], leadOut[7]).ToLba();
            if (leadOutLba <= lastLba) return null;

            return new CueSheetState
            {
                Raw = sheet.ToArray(),
                CdText = cdText,
                LeadOutLba = leadOutLba,
                Tracks = tracks,
                FirstWriteLba = cdText ? drive.LeadInStart.ToLba() : SaoAudioLayout.FirstWritableLba
            };
        }
    }
}
