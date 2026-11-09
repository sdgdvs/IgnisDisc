using System.Buffers.Binary;
using CdBurner.Hardware.NativeWriting;
using CdBurner.Hardware.Scsi;

namespace CdBurner.Hardware.Simulation;

/// <summary>
/// A CD recorder at the SCSI command level, for testing the native writer without hardware. It answers the MMC
/// commands a Session-at-Once audio write uses and enforces what a real drive enforces: write parameters before the
/// cue sheet, cue sheet before WRITE, every WRITE at the next address with the right block size (96 bytes in a
/// CD-Text lead-in, 2,352 in the program area), nothing past the lead-out. Faults can be switched on to test the
/// writer's error handling. A test write leaves the disc blank, as on a real drive.
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

    /// <summary>Keep the bytes sent with WRITE so tests can inspect them.</summary>
    public bool CaptureWrites { get; set; } = true;

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

    /// <summary>Ejecting and reloading the disc: the drive forgets a simulated session.</summary>
    public void Reload()
    {
        if (!Recorded)
        {
            DiscStatus = 0;
        }
        _cue = null;
    }

    public ScsiExecutionResult ExecuteCommand(ReadOnlySpan<byte> cdb, Span<byte> dataBuffer, byte dataDirection, TimeSpan timeout)
    {
        byte op = cdb[0];
        CommandsReceived.Add(op);

        if (dataBuffer.Length > MaxTransferBytes)
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
            _ => Sense(0x05, 0x20, 0x00) // invalid command operation code
        };
    }

    public void Dispose()
    {
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
        Add(MmcCommandBuilder.FeatureTrackAtOnce, (byte)((OffersBufferUnderrunFree ? 0x40 : 0) | (OffersTestWrite ? 0x04 : 0)), cd);
        if (OffersSessionAtOnce)
        {
            byte flags = (byte)((OffersBufferUnderrunFree ? 0x40 : 0) | 0x20 | (OffersTestWrite ? 0x04 : 0) | 0x02 | (OffersRwSubchannel ? 0x01 : 0));
            Add(MmcCommandBuilder.FeatureCdMastering, flags, cd);
        }

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
        r[2] = (byte)(DiscStatus & 0x03 | (Profile == NativeSaoAudioWriter.ProfileCdRw ? 0x10 : 0));
        r[3] = 1; r[4] = 1; r[5] = 1; r[6] = 1;
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

        _testWriteRequested = (page[2] & 0x10) != 0;
        if (DropsTestWriteBit || RecordsDuringTestWrite)
        {
            page[2] &= unchecked((byte)~0x10);
        }

        _modePage05 = page;
        _cue = null; // new parameters need a new cue sheet
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
            return Sense(0x05, 0x2C, 0x00);
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

    /// <summary>READ CD of CD-DA user data. Unrecorded sectors cannot be read (5/21/00 here; real drives vary).</summary>
    private ScsiExecutionResult ReadCd(ReadOnlySpan<byte> cdb, Span<byte> data)
    {
        int lba = BinaryPrimitives.ReadInt32BigEndian(cdb.Slice(2, 4));
        int count = (cdb[6] << 16) | (cdb[7] << 8) | cdb[8];
        if (!Recorded || lba < 0 || count == 0 || data.Length < count * 2352)
        {
            return Sense(0x05, 0x21, 0x00);
        }

        long offset = (long)(lba + 150) * 2352;
        byte[] program = ProgramAreaWritten.ToArray();
        int n = count * 2352;
        if (offset + n <= program.Length)
        {
            program.AsSpan((int)offset, n).CopyTo(data);
        }
        else
        {
            data.Slice(0, n).Clear();
        }
        return Good((uint)n);
    }

    private ScsiExecutionResult ReadBufferCapacity(Span<byte> data)
    {
        byte[] r = new byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(r, 10);
        BinaryPrimitives.WriteUInt32BigEndian(r.AsSpan(4), 4 * 1024 * 1024);
        BinaryPrimitives.WriteUInt32BigEndian(r.AsSpan(8), 512 * 1024);
        return CopyOut(r, data);
    }

    private ScsiExecutionResult SynchronizeCache()
    {
        if (_cue != null && _nextLba == _cue.LeadOutLba)
        {
            bool recorded = !TestWriteActive || RecordsDuringTestWrite;
            if (recorded)
            {
                Recorded = true;
                DiscStatus = ClosesDiscAfterWrite ? 2 : 1;
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
                if (e[2] == 1) sawIndex01ForTrack = true;
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
                FirstWriteLba = cdText ? drive.LeadInStart.ToLba() : SaoAudioLayout.FirstWritableLba
            };
        }
    }
}
