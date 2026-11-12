using System.Buffers.Binary;
using CdBurner.Hardware.Scsi;

namespace CdBurner.Hardware.NativeWriting;

/// <summary>Outcome of the read-back check that follows every real native write.</summary>
/// <param name="Passed">True when the table of contents, the CD-Text, the catalog number and ISRC codes, and every
/// compared audio sample matched.</param>
/// <param name="Message">What was checked, or the first thing that did not match.</param>
/// <param name="OffsetSamples">How many samples later than written the audio reads back (the drive's combined
/// write and read offset); null when the audio was not compared.</param>
/// <param name="SamplesCompared">Stereo samples compared.</param>
/// <param name="HardToReadSectors">Sectors that read wrong in the fast pass but right on a careful re-read: the disc
/// holds the right audio there, but it is hard to read.</param>
/// <param name="WrittenWrongSectors">Sectors that read back differently every time while the drive reported no
/// error in them (C2 pointers clean): the disc was written wrong there.</param>
/// <param name="UnreadableSectors">Sectors that never read right and never read without errors.</param>
/// <param name="Warning">For a passed check with hard-to-read sectors: what that means for playback.</param>
public sealed record NativeVerifyResult(
    bool Passed,
    string Message,
    int? OffsetSamples = null,
    long SamplesCompared = 0,
    int HardToReadSectors = 0,
    int WrittenWrongSectors = 0,
    int UnreadableSectors = 0,
    string? Warning = null);

/// <summary>
/// Reads a freshly written Session-at-Once audio disc back and compares it with what was sent: the table of contents
/// (track starts, lead-out, pre-emphasis and copy bits), the CD-Text packs, the catalog number and ISRC codes, and
/// the audio itself.
/// <para>
/// Audio never reads back at exactly the position it was written to: a drive writes and reads a fixed number of
/// samples early or late (its offsets). The check finds that shift from a short stretch of the audio and then
/// compares every sample under it. The few samples the shift pushes past either end of the disc are not compared.
/// </para>
/// </summary>
public sealed class NativeSaoVerifier
{
    public const int SamplesPerSector = 588;
    public const int ReadSectorsPerCommand = 27;

    /// <summary>Largest shift searched for, in samples (four sectors either way).</summary>
    public const int MaxOffsetSamples = 4 * SamplesPerSector;

    private const int SectorSize = ProgramAreaReader.SectorSize;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private readonly IScsiTransport _transport;
    private readonly Action<string> _note;
    private readonly Action<TimeSpan> _sleep;
    private int _shiftSamples; // the write-offset shift the writer applied: compare with what was really sent
    private int _baseLba;      // first audio sector compared (0, or after a mixed-mode CD's data track)

    public NativeSaoVerifier(IScsiTransport transport, Action<string>? note = null, Action<TimeSpan>? sleep = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _note = note ?? (_ => { });
        _sleep = sleep ?? Thread.Sleep;
    }

    /// <param name="layout">Where each track was placed.</param>
    /// <param name="openTrackAudio">Opens the audio that was written for track <c>i</c>, as the writer received it.</param>
    /// <param name="cdTextPacks">The CD-Text packs that were written, or null.</param>
    /// <param name="progress">Receives (program sectors compared, total) while the audio is compared.</param>
    /// <param name="mediaCatalogNumber">The catalog number that was written, or null.</param>
    /// <param name="shiftSamples">The write-offset shift the writer applied (<see cref="NativeSaoWriteRequest.ShiftSamples"/>).</param>
    public NativeVerifyResult Verify(
        SaoAudioLayout layout,
        Func<int, Stream> openTrackAudio,
        byte[]? cdTextPacks,
        Action<int, int>? progress = null,
        CancellationToken cancellationToken = default,
        string? mediaCatalogNumber = null,
        int shiftSamples = 0)
    {
        _shiftSamples = shiftSamples;
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(openTrackAudio);

        string? problem = CheckToc(layout);
        if (problem != null)
        {
            return Fail(problem);
        }
        _note($"Table of contents matches: {layout.Tracks.Count} track(s), lead-out at LBA {layout.LeadOutLba}.");

        if (cdTextPacks is { Length: > 0 })
        {
            problem = CheckCdText(cdTextPacks);
            if (problem != null)
            {
                return Fail(problem);
            }
            _note($"CD-Text matches: {cdTextPacks.Length / 18} pack(s).");
        }

        problem = CheckCodes(layout, mediaCatalogNumber, out string codesSummary);
        if (problem != null)
        {
            return Fail(problem);
        }

        if (layout.HasDataTrack)
        {
            problem = CheckDataTrack(layout.Tracks[0], openTrackAudio, cancellationToken);
            if (problem != null)
            {
                return Fail(problem);
            }
            if (layout.AudioSectors == 0)
            {
                return new NativeVerifyResult(true, "Read-back check passed: the data track matches.");
            }
        }

        try
        {
            var audio = CompareAudio(layout, openTrackAudio, progress, cancellationToken);
            return audio.Passed && codesSummary.Length > 0 ? audio with { Message = audio.Message + " " + codesSummary } : audio;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return Fail($"The source audio could not be read again for the check: {ex.Message}");
        }
    }

    private NativeVerifyResult Fail(string message)
    {
        _note($"Read-back check failed: {message}");
        return new NativeVerifyResult(false, message);
    }

    // ---- Table of contents ----

    private string? CheckToc(SaoAudioLayout layout)
    {
        byte[] buffer = new byte[4 + 8 * 100];
        var r = ExecuteWhenReady(MmcCommandBuilder.BuildReadTocPmaAtipCdb(0x00, 0, (ushort)buffer.Length), buffer);
        if (!r.IsSuccess || r.DataTransferLength < 4)
        {
            return $"The drive could not read the table of contents of the new disc: {Describe(r)}";
        }

        int available = (int)Math.Min(r.DataTransferLength, (uint)buffer.Length);
        var toc = MmcResponseParser.ParseToc(buffer.AsSpan(0, available));
        if (toc.Count != layout.Tracks.Count + 1)
        {
            return $"The disc shows {Math.Max(0, toc.Count - 1)} track(s); {layout.Tracks.Count} were written.";
        }

        for (int i = 0; i < layout.Tracks.Count; i++)
        {
            var expected = layout.Tracks[i];
            var actual = toc[i];
            if (actual.TrackNumber != expected.TrackNumber)
            {
                return $"The disc lists track {actual.TrackNumber} where track {expected.TrackNumber} was written.";
            }
            if (actual.StartLba != expected.Index01Lba)
            {
                return $"Track {expected.TrackNumber} starts at LBA {actual.StartLba} on the disc; it was written to start at {expected.Index01Lba}.";
            }

            int control = actual.AdrControl & 0x0F;
            if (((control & 0x04) != 0) != expected.IsData)
            {
                return expected.IsData
                    ? $"Track {expected.TrackNumber} was written as a data track but is marked as audio on the disc."
                    : $"Track {expected.TrackNumber} is marked as a data track on the disc.";
            }
            if (!expected.IsData && (control & 0x03) != expected.TocControl)
            {
                return $"Track {expected.TrackNumber} has control bits 0x{control:X} on the disc; pre-emphasis {(expected.PreEmphasis ? "on" : "off")} and copy permitted {(expected.CopyPermitted ? "on" : "off")} were written.";
            }
        }

        var leadOut = toc[^1];
        if (leadOut.TrackNumber != SaoCueSheetBuilder.LeadOutTrackNumber || leadOut.StartLba != layout.LeadOutLba)
        {
            return $"The lead-out is at LBA {leadOut.StartLba} on the disc; it was written at {layout.LeadOutLba}.";
        }

        return null;
    }

    // ---- CD-Text ----

    private string? CheckCdText(byte[] written)
    {
        byte[] buffer = new byte[4 + 18 * 2048];
        var r = ExecuteWhenReady(MmcCommandBuilder.BuildReadTocPmaAtipCdb(0x05, 0, (ushort)buffer.Length), buffer);
        if (!r.IsSuccess || r.DataTransferLength < 4)
        {
            return $"The drive could not read CD-Text back from the new disc: {Describe(r)}";
        }

        int length = Math.Min(BinaryPrimitives.ReadUInt16BigEndian(buffer) + 2, (int)Math.Min(r.DataTransferLength, (uint)buffer.Length));
        int count = Math.Max(0, (length - 4) / 18);
        if (count == 0)
        {
            return "The new disc has no CD-Text, although CD-Text was written.";
        }

        // Keyed by the four header bytes (type, track, sequence number, block and character position).
        var onDisc = new Dictionary<uint, byte[]>();
        for (int i = 0; i < count; i++)
        {
            var pack = buffer.AsSpan(4 + i * 18, 18);
            onDisc[BinaryPrimitives.ReadUInt32BigEndian(pack)] = pack.Slice(4, 12).ToArray();
        }

        for (int i = 0; i + 18 <= written.Length; i += 18)
        {
            var pack = written.AsSpan(i, 18);
            if (!onDisc.TryGetValue(BinaryPrimitives.ReadUInt32BigEndian(pack), out byte[]? text))
            {
                return $"CD-Text pack {i / 18} is missing on the disc ({count} pack(s) read back, {written.Length / 18} written).";
            }
            if (!pack.Slice(4, 12).SequenceEqual(text))
            {
                return $"CD-Text pack {i / 18} reads back with different text.";
            }
        }

        return null;
    }

    // ---- Catalog number and ISRC codes ----

    private enum CodeState
    {
        Read,
        NotOnDisc,
        DriveCannotReport
    }

    /// <summary>
    /// Compares the catalog number and the ISRC codes with what the drive finds in the Q sub-channel. A code the
    /// drive reads differently, or does not find, fails the check. A drive that refuses the command altogether
    /// cannot tell either way: that is said in the summary and does not fail the burn.
    /// </summary>
    private string? CheckCodes(SaoAudioLayout layout, string? mediaCatalogNumber, out string summary)
    {
        summary = string.Empty;
        string? catalog = SaoAudioLayout.NormalizeMcn(mediaCatalogNumber);
        var withIsrc = layout.Tracks.Where(t => t.Isrc != null).ToList();
        if (catalog == null && withIsrc.Count == 0)
        {
            return null;
        }

        const string cannotReport = "The catalog number and ISRC codes could not be checked: this drive does not report them.";

        if (catalog != null)
        {
            var state = ReadCode(format: 0x02, track: 0, length: 13, out string onDisc, out string detail);
            if (state == CodeState.DriveCannotReport)
            {
                _note($"{cannotReport} ({detail})");
                summary = cannotReport;
                return null;
            }
            if (state == CodeState.NotOnDisc)
            {
                return $"The catalog number {catalog} was written, but the disc does not show one.";
            }
            if (!string.Equals(onDisc, catalog, StringComparison.Ordinal))
            {
                return $"The catalog number reads back as {onDisc}; {catalog} was written.";
            }
        }

        foreach (var t in withIsrc)
        {
            var state = ReadCode(format: 0x03, track: (byte)t.TrackNumber, length: 12, out string onDisc, out string detail);
            if (state == CodeState.DriveCannotReport)
            {
                _note($"{cannotReport} ({detail})");
                summary = cannotReport;
                return null;
            }
            if (state == CodeState.NotOnDisc)
            {
                return $"The ISRC {t.Isrc} was written for track {t.TrackNumber}, but the disc does not show one.";
            }
            if (!string.Equals(onDisc, t.Isrc, StringComparison.OrdinalIgnoreCase))
            {
                return $"The ISRC of track {t.TrackNumber} reads back as {onDisc}; {t.Isrc} was written.";
            }
        }

        summary = (catalog != null, withIsrc.Count) switch
        {
            (true, 0) => "Catalog number matches.",
            (false, _) => $"{withIsrc.Count} ISRC code(s) match.",
            _ => $"Catalog number and {withIsrc.Count} ISRC code(s) match."
        };
        _note(summary);
        return null;
    }

    /// <summary>
    /// READ SUB-CHANNEL (0x42) with format 2 (catalog number) or 3 (ISRC of one track). The drive looks for a Q frame
    /// that carries the code; those come only every hundred frames or so, which is why "not found" is asked again.
    /// </summary>
    private CodeState ReadCode(byte format, byte track, int length, out string value, out string detail)
    {
        value = string.Empty;
        detail = string.Empty;
        byte[] buffer = new byte[24];
        byte[] cdb = [0x42, 0x00, 0x40, format, 0x00, 0x00, track, 0x00, (byte)buffer.Length, 0x00];

        for (int attempt = 0; attempt < 5; attempt++)
        {
            Array.Clear(buffer);
            var r = ExecuteWhenReady(cdb, buffer);
            if (!r.IsSuccess || r.DataTransferLength < 9 + (uint)length)
            {
                detail = r.IsSuccess ? "the answer was too short" : Describe(r);
                return CodeState.DriveCannotReport;
            }

            if ((buffer[8] & 0x80) != 0)
            {
                value = System.Text.Encoding.ASCII.GetString(buffer, 9, length).TrimEnd('\0', ' ');
                return CodeState.Read;
            }
            _sleep(TimeSpan.FromMilliseconds(200));
        }

        return CodeState.NotOnDisc;
    }

    // ---- Data track (mixed-mode CD) ----

    /// <summary>"the data track's N sectors and " for a mixed-mode CD's pass message; empty otherwise.</summary>
    private static string DataPart(SaoAudioLayout layout) =>
        layout.HasDataTrack ? $"the data track's {layout.Tracks[0].SectorCount - layout.Tracks[0].PostGapSectors:N0} sectors and " : string.Empty;

    /// <summary>
    /// Reads a mixed-mode CD's data track back as Mode 1 user data (READ CD, sector type 2, 2,048 bytes a sector) and
    /// compares it with the image, then checks that the post-gap reads as zeros. Data sectors carry their own address,
    /// so there is no offset to find.
    /// </summary>
    private string? CheckDataTrack(SaoTrackPlacement track, Func<int, Stream> openTrackAudio, CancellationToken ct)
    {
        const int dataSize = 2048;
        const int perRead = 32; // 64 KiB
        int dataSectors = track.SectorCount - track.PostGapSectors;
        byte[] read = new byte[perRead * dataSize];
        byte[] expected = new byte[perRead * dataSize];
        int different = 0;
        int firstDifferent = -1;
        bool imageEnded = false;

        using var image = openTrackAudio(0);
        for (int lba = track.Index01Lba; lba < track.EndLba; lba += perRead)
        {
            ct.ThrowIfCancellationRequested();
            int count = Math.Min(perRead, track.EndLba - lba);
            var r = _transport.ExecuteCommand(
                MmcCommandBuilder.BuildReadCdCdb(lba, count, expectedSectorType: 2, mainChannelSelectionBits: 0x10),
                read.AsSpan(0, count * dataSize), ScsiPassThroughNative.SCSI_IOCTL_DATA_IN, Timeout);
            if (!r.IsSuccess || r.DataTransferLength < (uint)(count * dataSize))
            {
                return $"The data track could not be read back at LBA {lba}: {Describe(r)}";
            }

            Array.Clear(expected);
            for (int i = 0; i < count; i++)
            {
                int index = lba + i - track.Index01Lba;
                if (index < dataSectors && !imageEnded)
                {
                    int got = 0;
                    while (got < dataSize)
                    {
                        int n = image.Read(expected, i * dataSize + got, dataSize - got);
                        if (n <= 0) { imageEnded = true; break; }
                        got += n;
                    }
                }
                if (!read.AsSpan(i * dataSize, dataSize).SequenceEqual(expected.AsSpan(i * dataSize, dataSize)))
                {
                    different++;
                    if (firstDifferent < 0) firstDifferent = lba + i;
                }
            }
        }

        if (different > 0)
        {
            return $"{different:N0} of {track.SectorCount:N0} data track sectors read back differently, the first at LBA {firstDifferent}.";
        }
        _note($"Data track matches: {dataSectors:N0} sectors and a {track.PostGapSectors}-sector post-gap.");
        return null;
    }

    // ---- Audio ----

    private NativeVerifyResult CompareAudio(SaoAudioLayout layout, Func<int, Stream> openTrackAudio, Action<int, int>? progress, CancellationToken ct)
    {
        // The audio is read from its first readable sector: LBA 0 on an all-audio disc (the two seconds before it cannot
        // be read), or the first audio track's pre-gap after a mixed-mode CD's data track. Inside this method LBAs are
        // counted from there; _baseLba turns them back into disc addresses.
        _baseLba = Math.Max(0, layout.AudioStartLba);
        int skip = _baseLba - layout.AudioStartLba;
        int totalSectors = layout.LeadOutLba - _baseLba;
        if (totalSectors <= 0)
        {
            return Fail("There is no audio to compare.");
        }

        int offset;
        using (var probeSource = new ProgramAreaReader(layout, openTrackAudio, shiftSamples: _shiftSamples))
        {
            probeSource.Skip(skip);
            var found = FindOffset(probeSource, totalSectors, out string? offsetProblem);
            if (found is null)
            {
                return Fail(offsetProblem!);
            }
            offset = found.Value;
        }
        _note($"Audio reads back {offset} sample(s) {(offset >= 0 ? "later" : "earlier")} than written (drive offset).");

        using var expectedSource = new ProgramAreaReader(layout, openTrackAudio, shiftSamples: _shiftSamples);
        expectedSource.Skip(skip);

        int nextReadLba = 0;
        string? readProblem = null;
        var expected = new ChunkReader(buffer =>
        {
            int sectors = 0;
            while (sectors < ReadSectorsPerCommand && expectedSource.ReadSector(buffer.AsSpan(sectors * SectorSize, SectorSize)))
            {
                sectors++;
            }
            return sectors * SectorSize;
        });
        var actual = new ChunkReader(buffer =>
        {
            int sectors = Math.Min(ReadSectorsPerCommand, totalSectors - nextReadLba);
            if (sectors <= 0 || readProblem != null)
            {
                return 0;
            }
            readProblem = ReadAudio(_baseLba + nextReadLba, sectors, buffer.AsSpan(0, sectors * SectorSize));
            if (readProblem != null)
            {
                return 0;
            }
            nextReadLba += sectors;
            return sectors * SectorSize;
        });

        // Sample i of what was written is sample i + offset of what reads back.
        long expectedPosition = 0; // samples from LBA 0 in the written audio
        if (offset > 0)
        {
            actual.Skip(offset * 4);
        }
        else if (offset < 0)
        {
            expected.Skip(-offset * 4);
            expectedPosition = -offset;
        }

        byte[] a = new byte[ReadSectorsPerCommand * SectorSize];
        byte[] b = new byte[ReadSectorsPerCommand * SectorSize];
        long compared = 0;
        long different = 0;
        long firstDifferent = -1;
        var suspects = new SortedSet<int>(); // disc sectors holding a differing sample, for the careful re-read
        bool tooManyToRecheck = false;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            int gotExpected = expected.Read(a);
            int gotActual = actual.Read(b);
            if (readProblem != null)
            {
                return Fail(readProblem);
            }

            int n = Math.Min(gotExpected, gotActual) & ~3;
            if (n == 0)
            {
                break;
            }

            if (!a.AsSpan(0, n).SequenceEqual(b.AsSpan(0, n)))
            {
                for (int i = 0; i < n; i += 4)
                {
                    if (BinaryPrimitives.ReadUInt32LittleEndian(a.AsSpan(i)) != BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(i)))
                    {
                        different++;
                        if (firstDifferent < 0)
                        {
                            firstDifferent = expectedPosition + i / 4;
                        }

                        // Written sample p reads back as disc sample p + offset.
                        int discLba = _baseLba + (int)Math.Clamp((expectedPosition + i / 4 + offset) / SamplesPerSector, 0, totalSectors - 1);
                        if (!tooManyToRecheck && suspects.Add(discLba) && suspects.Count > MaxRecheckSectors)
                        {
                            tooManyToRecheck = true;
                        }
                    }
                }
            }

            compared += n / 4;
            expectedPosition += n / 4;
            progress?.Invoke((int)Math.Min(totalSectors, expectedPosition / SamplesPerSector), totalSectors);

            if (gotExpected < a.Length || gotActual < b.Length)
            {
                break;
            }
        }

        if (different > 0)
        {
            int lba = _baseLba + (int)(firstDifferent / SamplesPerSector);
            string fastPass = $"{different:N0} of {compared:N0} audio samples read back differently in the fast pass, the first in track {TrackAt(layout, lba)} near LBA {lba}.";
            if (tooManyToRecheck)
            {
                return Fail($"{fastPass} That is more than {MaxRecheckSectors:N0} sectors, too many to re-read one by one.", offset, compared);
            }

            _note($"{fastPass} Re-reading the {suspects.Count:N0} sector(s) involved with C2 error pointers.");
            var recheck = Recheck(layout, openTrackAudio, offset, suspects.ToList(), ct);
            return Classify(layout, recheck, offset, compared);
        }

        string message = $"Read-back check passed: {layout.Tracks.Count} track(s), {DataPart(layout)}{compared:N0} audio samples identical (drive offset {offset:+0;-0;0} samples).";
        _note(message);
        return new NativeVerifyResult(true, message, offset, compared);
    }

    private NativeVerifyResult Fail(string message, int offset, long compared)
    {
        _note($"Read-back check failed: {message}");
        return new NativeVerifyResult(false, message, offset, compared);
    }

    private static int TrackAt(SaoAudioLayout layout, int lba) =>
        layout.Tracks.LastOrDefault(t => t.PreGapLba <= lba)?.TrackNumber ?? 1;

    private static string TracksAt(SaoAudioLayout layout, IEnumerable<int> lbas) =>
        string.Join(", ", lbas.Select(l => TrackAt(layout, l)).Distinct().Order());

    // ---- Careful re-read of the sectors that differed ----

    /// <summary>
    /// Most sectors re-read carefully (about 6.7 minutes of audio; the expected audio for them is held in memory, up to
    /// about 70 MB). It was 4,500 until a CD-R of a weak batch read 2.5 million samples wrong straight after its burn,
    /// over more than 4,500 sectors, yet ripped sample-exact minutes later with 198 re-reads (2026-11-11).
    /// </summary>
    public const int MaxRecheckSectors = 30_000;

    /// <summary>Re-read rounds; the drive's cache is flushed before each so the reads come from the disc.</summary>
    public const int RecheckRounds = 5;

    /// <summary>Sectors read far away to push the re-read sectors out of the drive's read cache (about 2.4 MB).</summary>
    private const int CacheFlushSectors = 1024;

    private const int C2Bytes = 294;

    /// <summary>
    /// Sectors per READ CD with C2 pointers: 24 × 2,646 bytes = 63,504, inside the 64 KiB per command that USB bridges such
    /// as the GP60NB50's pass (27 would be 71,442 and fail with Win32 error 87).
    /// </summary>
    public const int C2SectorsPerCommand = 65_536 / (SectorSize + C2Bytes);

    private sealed record RecheckOutcome(List<int> HardToRead, List<int> WrittenWrong, List<int> Unreadable, bool C2Available);

    /// <summary>
    /// Re-reads each suspect sector up to <see cref="RecheckRounds"/> times with C2 error pointers, the way a secure
    /// rip does. A reading that equals what was written settles it: the data on the disc is right. A sector that
    /// reads differently at least twice while the drive flags no error in it was written wrong. Anything else never
    /// read cleanly: unreadable. Without C2 pointers the drive cannot say which, and such sectors count as unreadable.
    /// </summary>
    private RecheckOutcome Recheck(SaoAudioLayout layout, Func<int, Stream> openTrackAudio, int offset, List<int> suspects, CancellationToken ct)
    {
        Dictionary<int, byte[]> expected = ExpectedDiscSectors(layout, openTrackAudio, offset, suspects);
        var matched = new HashSet<int>();
        var cleanButDifferent = new Dictionary<int, int>();
        bool c2 = true;

        for (int round = 0; round < RecheckRounds; round++)
        {
            var remaining = suspects.Where(l => !matched.Contains(l)).ToList();
            if (remaining.Count == 0)
            {
                break;
            }

            FlushReadCache(remaining[0], layout.LeadOutLba);
            foreach (var (start, count) in Runs(remaining, C2SectorsPerCommand))
            {
                ct.ThrowIfCancellationRequested();
                int stride = c2 ? SectorSize + C2Bytes : SectorSize;
                byte[] buffer = new byte[count * stride];
                var r = _transport.ExecuteCommand(
                    MmcCommandBuilder.BuildReadCdCdb(start, count, expectedSectorType: 1, mainChannelSelectionBits: (byte)(c2 ? 0x12 : 0x10)),
                    buffer, ScsiPassThroughNative.SCSI_IOCTL_DATA_IN, Timeout);
                if (!r.IsSuccess && c2 && r.SenseInfo is { SenseKey: 0x05 })
                {
                    _note($"The drive refused C2 error pointers ({Describe(r)}); re-reading without them.");
                    c2 = false;
                    stride = SectorSize;
                    buffer = new byte[count * stride];
                    r = _transport.ExecuteCommand(
                        MmcCommandBuilder.BuildReadCdCdb(start, count, expectedSectorType: 1, mainChannelSelectionBits: 0x10),
                        buffer, ScsiPassThroughNative.SCSI_IOCTL_DATA_IN, Timeout);
                }
                if (!r.IsSuccess || r.DataTransferLength < (uint)buffer.Length)
                {
                    continue; // a failed read settles nothing; the next round tries again
                }

                for (int i = 0; i < count; i++)
                {
                    int lba = start + i;
                    ReadOnlySpan<byte> audio = buffer.AsSpan(i * stride, SectorSize);
                    if (audio.SequenceEqual(expected[lba]))
                    {
                        matched.Add(lba);
                    }
                    else if (c2 && !buffer.AsSpan(i * stride + SectorSize, C2Bytes).ContainsAnyExcept((byte)0))
                    {
                        cleanButDifferent[lba] = cleanButDifferent.GetValueOrDefault(lba) + 1;
                    }
                }
            }
        }

        var hard = suspects.Where(matched.Contains).ToList();
        var wrong = c2 ? suspects.Where(l => !matched.Contains(l) && cleanButDifferent.GetValueOrDefault(l) >= 2).ToList() : [];
        var unreadable = suspects.Where(l => !matched.Contains(l) && !wrong.Contains(l)).ToList();
        _note($"Careful re-read: {hard.Count} sector(s) read right{List(hard)}, {wrong.Count} read differently with no error reported{List(wrong)}, {unreadable.Count} never read cleanly{List(unreadable)}{(c2 ? "" : " (no C2 pointers)")}.");
        return new RecheckOutcome(hard, wrong, unreadable, c2);
    }

    /// <summary>" (LBA 1, 2, 3 …)" for the log: the first ten sector numbers of a verdict, or nothing when there are none.</summary>
    private static string List(List<int> lbas) =>
        lbas.Count == 0 ? "" : $" (LBA {string.Join(", ", lbas.Take(10))}{(lbas.Count > 10 ? ", …" : "")})";

    private NativeVerifyResult Classify(SaoAudioLayout layout, RecheckOutcome o, int offset, long compared)
    {
        if (o.WrittenWrong.Count > 0)
        {
            int first = o.WrittenWrong[0];
            string message = $"{o.WrittenWrong.Count:N0} sector(s) read back differently on every careful re-read while the drive reported no error in them, the first in track {TrackAt(layout, first)} near LBA {first}: the disc was written wrong there.";
            _note($"Read-back check failed: {message}");
            return new NativeVerifyResult(false, message, offset, compared, o.HardToRead.Count, o.WrittenWrong.Count, o.Unreadable.Count);
        }

        if (o.Unreadable.Count > 0)
        {
            int first = o.Unreadable[0];
            string why = o.C2Available
                ? "the drive reported read errors every time"
                : "this drive gives no error pointers, so it cannot tell a bad write from a bad read";
            string message = $"{o.Unreadable.Count:N0} sector(s) never read back right in {RecheckRounds} careful re-reads ({why}), the first in track {TrackAt(layout, first)} near LBA {first}.";
            _note($"Read-back check failed: {message}");
            return new NativeVerifyResult(false, message, offset, compared, o.HardToRead.Count, 0, o.Unreadable.Count);
        }

        string warning = $"{o.HardToRead.Count:N0} sector(s) in track(s) {TracksAt(layout, o.HardToRead)} read wrong in the fast pass and right on a careful re-read: "
            + "the disc holds the right audio, but those parts are hard to read, so some players may skip there. Discs of another make may do better.";
        string passed = $"Read-back check passed: {layout.Tracks.Count} track(s), {DataPart(layout)}{compared:N0} audio samples identical after re-reading (drive offset {offset:+0;-0;0} samples). {warning}";
        _note(passed);
        return new NativeVerifyResult(true, passed, offset, compared, o.HardToRead.Count, Warning: warning);
    }

    /// <summary>What each listed disc sector should hold: written sample p reads back as disc sample p + offset.</summary>
    private Dictionary<int, byte[]> ExpectedDiscSectors(SaoAudioLayout layout, Func<int, Stream> openTrackAudio, int offset, List<int> lbas)
    {
        using var source = new ProgramAreaReader(layout, openTrackAudio, shiftSamples: _shiftSamples);
        source.Skip(_baseLba - layout.AudioStartLba);
        var reader = new ChunkReader(buffer =>
        {
            int sectors = 0;
            while (sectors < ReadSectorsPerCommand && source.ReadSector(buffer.AsSpan(sectors * SectorSize, SectorSize)))
            {
                sectors++;
            }
            return sectors * SectorSize;
        });

        var result = new Dictionary<int, byte[]>();
        long position = 0; // bytes consumed from the written audio, counted from LBA 0
        foreach (int lba in lbas)
        {
            byte[] sector = new byte[SectorSize]; // samples from outside the written audio read as silence
            long start = ((long)(lba - _baseLba) * SamplesPerSector - offset) * 4;
            int into = 0;
            if (start < 0)
            {
                into = (int)Math.Min(SectorSize, -start);
                start = 0;
            }
            if (start > position)
            {
                reader.Skip((int)(start - position));
                position = start;
            }
            int got = reader.Read(sector.AsSpan(into));
            position += got;
            result[lba] = sector;
        }
        return result;
    }

    /// <summary>Groups sorted sector numbers into contiguous runs of at most one READ CD each.</summary>
    private static IEnumerable<(int Start, int Count)> Runs(List<int> sorted, int maxPerRun)
    {
        int i = 0;
        while (i < sorted.Count)
        {
            int start = sorted[i], count = 1;
            while (i + count < sorted.Count && sorted[i + count] == start + count && count < maxPerRun)
            {
                count++;
            }
            yield return (start, count);
            i += count;
        }
    }

    /// <summary>Reads about 2.4 MB half a disc away so the sectors about to be re-read are no longer in the drive's cache.</summary>
    private void FlushReadCache(int nearLba, int endLba)
    {
        // Within the audio (a READ CD for audio of a data sector fails), as far from nearLba as the audio allows.
        int first = _baseLba, total = endLba - _baseLba;
        int span = Math.Min(CacheFlushSectors, total);
        int near = nearLba - first;
        int far = Math.Clamp((near + total / 2) % Math.Max(1, total), 0, Math.Max(0, total - span));
        if (near >= far && near < far + span)
        {
            far = near >= span ? 0 : Math.Max(0, total - span);
        }
        far += first;

        byte[] buffer = new byte[ReadSectorsPerCommand * SectorSize];
        for (int done = 0; done < span; done += ReadSectorsPerCommand)
        {
            int count = Math.Min(ReadSectorsPerCommand, span - done);
            _transport.ExecuteCommand(
                MmcCommandBuilder.BuildReadCdCdb(far + done, count, expectedSectorType: 1, mainChannelSelectionBits: 0x10),
                buffer.AsSpan(0, count * SectorSize), ScsiPassThroughNative.SCSI_IOCTL_DATA_IN, Timeout);
        }
    }

    /// <summary>
    /// Finds how far the audio is shifted when read back. Takes the first two sectors of the written audio that hold
    /// a real signal, reads the same place on the disc with four sectors of margin either side, and looks for the
    /// position where the two are identical. A disc of pure digital silence needs no shift.
    /// </summary>
    private int? FindOffset(ProgramAreaReader expected, int totalSectors, out string? problem)
    {
        problem = null;
        const int marginSectors = MaxOffsetSamples / SamplesPerSector;
        byte[] window = new byte[2 * SectorSize];
        byte[] sector = new byte[SectorSize];

        int probe = -1;
        for (int index = 0; index + 2 + marginSectors <= totalSectors; index++)
        {
            if (!expected.ReadSector(sector))
            {
                break;
            }
            if (index < marginSectors || !IsDistinctive(sector))
            {
                continue;
            }

            sector.CopyTo(window, 0);
            if (!expected.ReadSector(window.AsSpan(SectorSize, SectorSize)))
            {
                break;
            }
            probe = index;
            break;
        }

        if (probe < 0)
        {
            _note("The audio holds no stretch with a clear signal; comparing without a shift.");
            return 0;
        }

        int firstLba = probe - marginSectors;
        int sectors = 2 + 2 * marginSectors;
        byte[] read = new byte[sectors * SectorSize];
        string? readProblem = ReadAudio(_baseLba + firstLba, sectors, read);
        if (readProblem != null)
        {
            problem = readProblem;
            return null;
        }

        for (int distance = 0; distance <= MaxOffsetSamples; distance++)
        {
            foreach (int shift in distance == 0 ? [0] : new[] { distance, -distance })
            {
                int start = (MaxOffsetSamples + shift) * 4;
                if (read.AsSpan(start, window.Length).SequenceEqual(window))
                {
                    return shift;
                }
            }
        }

        problem = $"The audio read back near LBA {_baseLba + probe} does not match what was written, at any shift up to {MaxOffsetSamples} samples.";
        return null;
    }

    /// <summary>True when a sector's samples change often enough to pin down a position.</summary>
    private static bool IsDistinctive(ReadOnlySpan<byte> sector)
    {
        int changes = 0;
        uint previous = BinaryPrimitives.ReadUInt32LittleEndian(sector);
        for (int i = 4; i < sector.Length; i += 4)
        {
            uint sample = BinaryPrimitives.ReadUInt32LittleEndian(sector.Slice(i));
            if (sample != previous)
            {
                changes++;
            }
            previous = sample;
        }
        return changes >= 64;
    }

    private string? ReadAudio(int lba, int sectors, Span<byte> destination)
    {
        ScsiExecutionResult r = default!;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            r = _transport.ExecuteCommand(
                MmcCommandBuilder.BuildReadCdCdb(lba, sectors, expectedSectorType: 1, mainChannelSelectionBits: 0x10),
                destination, ScsiPassThroughNative.SCSI_IOCTL_DATA_IN, Timeout);
            if (r.IsSuccess && r.DataTransferLength >= (uint)destination.Length)
            {
                return null;
            }
        }
        return $"The new disc could not be read back at LBA {lba}: {Describe(r)}";
    }

    /// <summary>Runs a read command, waiting out "becoming ready" and "media changed" answers after a write.</summary>
    private ScsiExecutionResult ExecuteWhenReady(byte[] cdb, byte[] buffer)
    {
        ScsiExecutionResult r = default!;
        for (int attempt = 0; attempt < 20; attempt++)
        {
            r = _transport.ExecuteCommand(cdb, buffer, ScsiPassThroughNative.SCSI_IOCTL_DATA_IN, Timeout);
            if (r.IsSuccess || r.SenseInfo is not ({ SenseKey: 0x06 } or { SenseKey: 0x02, Asc: 0x04 }))
            {
                return r;
            }
            _sleep(TimeSpan.FromMilliseconds(500));
        }
        return r;
    }

    private static string Describe(ScsiExecutionResult r) =>
        r.SenseInfo?.ToString() ?? r.ErrorMessage ?? $"status 0x{r.ScsiStatus:X2}";

    /// <summary>Turns a "fill me a chunk" function into a byte stream that can be read and skipped freely.</summary>
    private sealed class ChunkReader
    {
        private readonly Func<byte[], int> _fill;
        private readonly byte[] _chunk = new byte[ReadSectorsPerCommand * SectorSize];
        private int _position;
        private int _length;
        private bool _ended;

        public ChunkReader(Func<byte[], int> fill) => _fill = fill;

        public int Read(Span<byte> destination)
        {
            int total = 0;
            while (total < destination.Length)
            {
                if (_position == _length)
                {
                    if (_ended)
                    {
                        break;
                    }
                    _length = _fill(_chunk);
                    _position = 0;
                    if (_length == 0)
                    {
                        _ended = true;
                        break;
                    }
                }

                int n = Math.Min(destination.Length - total, _length - _position);
                _chunk.AsSpan(_position, n).CopyTo(destination.Slice(total));
                _position += n;
                total += n;
            }
            return total;
        }

        public void Skip(int bytes)
        {
            Span<byte> scratch = stackalloc byte[1024];
            while (bytes > 0)
            {
                int n = Read(scratch.Slice(0, Math.Min(bytes, scratch.Length)));
                if (n == 0)
                {
                    return;
                }
                bytes -= n;
            }
        }
    }
}
