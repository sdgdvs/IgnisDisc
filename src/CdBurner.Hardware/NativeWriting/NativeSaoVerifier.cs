using System.Buffers.Binary;
using CdBurner.Hardware.Scsi;

namespace CdBurner.Hardware.NativeWriting;

/// <summary>Outcome of the read-back check that follows every real native write.</summary>
/// <param name="Passed">True when the table of contents, the CD-Text and every compared audio sample matched.</param>
/// <param name="Message">What was checked, or the first thing that did not match.</param>
/// <param name="OffsetSamples">How many samples later than written the audio reads back (the drive's combined
/// write and read offset); null when the audio was not compared.</param>
/// <param name="SamplesCompared">Stereo samples compared.</param>
public sealed record NativeVerifyResult(bool Passed, string Message, int? OffsetSamples = null, long SamplesCompared = 0);

/// <summary>
/// Reads a freshly written Session-at-Once audio disc back and compares it with what was sent: the table of contents
/// (track starts, lead-out, pre-emphasis and copy bits), the CD-Text packs, and the audio itself.
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
    public NativeVerifyResult Verify(
        SaoAudioLayout layout,
        Func<int, Stream> openTrackAudio,
        byte[]? cdTextPacks,
        Action<int, int>? progress = null,
        CancellationToken cancellationToken = default)
    {
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

        try
        {
            return CompareAudio(layout, openTrackAudio, progress, cancellationToken);
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
            if ((control & 0x04) != 0)
            {
                return $"Track {expected.TrackNumber} is marked as a data track on the disc.";
            }
            if ((control & 0x03) != expected.TocControl)
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

    // ---- Audio ----

    private NativeVerifyResult CompareAudio(SaoAudioLayout layout, Func<int, Stream> openTrackAudio, Action<int, int>? progress, CancellationToken ct)
    {
        int totalSectors = layout.LeadOutLba; // LBA 0 up to the lead-out; the two seconds before LBA 0 cannot be read
        if (totalSectors <= 0)
        {
            return Fail("There is no audio to compare.");
        }

        int offset;
        using (var probeSource = new ProgramAreaReader(layout, openTrackAudio))
        {
            probeSource.Skip(-SaoAudioLayout.FirstWritableLba);
            var found = FindOffset(probeSource, totalSectors, out string? offsetProblem);
            if (found is null)
            {
                return Fail(offsetProblem!);
            }
            offset = found.Value;
        }
        _note($"Audio reads back {offset} sample(s) {(offset >= 0 ? "later" : "earlier")} than written (drive offset).");

        using var expectedSource = new ProgramAreaReader(layout, openTrackAudio);
        expectedSource.Skip(-SaoAudioLayout.FirstWritableLba);

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
            readProblem = ReadAudio(nextReadLba, sectors, buffer.AsSpan(0, sectors * SectorSize));
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
            int lba = (int)(firstDifferent / SamplesPerSector);
            int track = layout.Tracks.LastOrDefault(t => t.PreGapLba <= lba)?.TrackNumber ?? 1;
            return Fail($"{different:N0} of {compared:N0} audio samples read back differently, the first in track {track} near LBA {lba}.");
        }

        string message = $"Read-back check passed: {layout.Tracks.Count} track(s), {compared:N0} audio samples identical (drive offset {offset:+0;-0;0} samples).";
        _note(message);
        return new NativeVerifyResult(true, message, offset, compared);
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
        string? readProblem = ReadAudio(firstLba, sectors, read);
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

        problem = $"The audio read back near LBA {probe} does not match what was written, at any shift up to {MaxOffsetSamples} samples.";
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
