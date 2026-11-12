namespace CdBurner.Hardware.NativeWriting;

/// <summary>
/// Produces the program area of a Session-at-Once audio disc one 2,352-byte sector at a time, starting at LBA −150:
/// for each track its pre-gap (silence, then any pre-gap audio from the track's stream) and then its audio. The
/// writer sends these sectors to the drive and the read-back check builds the same sequence again to compare, so
/// both always agree on what belongs on the disc.
/// </summary>
internal sealed class ProgramAreaReader : IDisposable
{
    public const int SectorSize = 2352;

    private readonly SaoAudioLayout _layout;
    private readonly Func<int, Stream> _openTrackAudio;
    private readonly Action<string> _note;
    private int _track;
    private int _sectorInTrack;
    private Stream? _audio;
    private bool _audioEnded;

    // Write-offset shift: the whole program area moved by whole samples, sector boundaries kept.
    private readonly int _shiftBytes;      // > 0: audio later (silence first); < 0: audio earlier (silence at the end)
    private readonly int _totalSectors;
    private int _sectorsOut;
    private int _leadingSilence;           // bytes of silence still to send before the audio (shift > 0)
    private bool _skippedStart;            // the first -shift bytes were dropped (shift < 0)
    private readonly byte[] _raw = new byte[SectorSize];
    private int _rawPos, _rawLen;          // unread bytes of _raw are _rawPos.._rawLen; empty at the start
    private bool _rawEnded;

    /// <param name="shiftSamples">Moves all the audio this many samples later (negative: earlier), across track
    /// boundaries, to correct the drive's write offset. Samples pushed past either end of the program area are
    /// dropped and the gap is silence. The writer and the read-back check must pass the same value.</param>
    public ProgramAreaReader(SaoAudioLayout layout, Func<int, Stream> openTrackAudio, Action<string>? note = null, int shiftSamples = 0)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _openTrackAudio = openTrackAudio ?? throw new ArgumentNullException(nameof(openTrackAudio));
        _note = note ?? (_ => { });
        _shiftBytes = checked(shiftSamples * 4);
        _leadingSilence = Math.Max(0, _shiftBytes);
        _totalSectors = layout.Tracks.Sum(t => t.PreGapSectors + t.SectorCount);
        if (Math.Abs(_shiftBytes) >= (long)_totalSectors * SectorSize)
        {
            throw new ArgumentOutOfRangeException(nameof(shiftSamples), "The shift is longer than the program area.");
        }
    }

    /// <summary>Number of the track the next sector belongs to (the last track once everything was read).</summary>
    public int CurrentTrackNumber => _layout.Tracks[Math.Min(_track, _layout.Tracks.Count - 1)].TrackNumber;

    /// <summary>Fills <paramref name="sector"/> with the next sector. Returns false after the last one.</summary>
    /// <exception cref="IOException">The track's audio could not be opened or read.</exception>
    public bool ReadSector(Span<byte> sector)
    {
        if (sector.Length != SectorSize)
        {
            throw new ArgumentException($"A sector buffer holds {SectorSize} bytes.", nameof(sector));
        }
        if (_shiftBytes == 0)
        {
            return ReadRawSector(sector);
        }
        if (_sectorsOut >= _totalSectors)
        {
            return false;
        }

        if (!_skippedStart && _shiftBytes < 0)
        {
            _skippedStart = true;
            TakeRaw(new byte[-_shiftBytes]);
        }

        int filled = 0;
        if (_leadingSilence > 0)
        {
            int n = Math.Min(_leadingSilence, SectorSize);
            sector[..n].Clear();
            _leadingSilence -= n;
            filled = n;
        }
        TakeRaw(sector[filled..]);
        _sectorsOut++;
        return true;
    }

    /// <summary>Copies the next bytes of the unshifted program area into <paramref name="destination"/>; silence once it has ended.</summary>
    private void TakeRaw(Span<byte> destination)
    {
        int done = 0;
        while (done < destination.Length)
        {
            if (_rawPos == _rawLen)
            {
                if (_rawEnded || !ReadRawSector(_raw))
                {
                    _rawEnded = true;
                    destination[done..].Clear();
                    return;
                }
                _rawPos = 0;
                _rawLen = SectorSize;
            }
            int n = Math.Min(destination.Length - done, _rawLen - _rawPos);
            _raw.AsSpan(_rawPos, n).CopyTo(destination[done..]);
            _rawPos += n;
            done += n;
        }
    }

    /// <summary>The next sector of the program area as laid out, before any shift.</summary>
    private bool ReadRawSector(Span<byte> sector)
    {
        while (_track < _layout.Tracks.Count)
        {
            var track = _layout.Tracks[_track];
            int total = track.PreGapSectors + track.SectorCount;
            if (_sectorInTrack >= total)
            {
                CloseAudio();
                _track++;
                _sectorInTrack = 0;
                continue;
            }

            int silentSectors = track.PreGapSectors - track.PreGapAudioSectors;
            if (_sectorInTrack < silentSectors)
            {
                sector.Clear();
            }
            else
            {
                _audio ??= _openTrackAudio(_track);
                int got = _audioEnded ? 0 : ReadFully(_audio, sector);
                if (got < SectorSize)
                {
                    if (!_audioEnded)
                    {
                        _note($"Track {track.TrackNumber} audio ended early at sector {_sectorInTrack - silentSectors}; padding with silence.");
                    }
                    _audioEnded = true;
                    sector.Slice(got).Clear();
                }
            }

            _sectorInTrack++;
            return true;
        }

        return false;
    }

    /// <summary>Reads and drops <paramref name="count"/> sectors. Returns false if the program area ended first.</summary>
    public bool Skip(int count)
    {
        Span<byte> scratch = stackalloc byte[SectorSize];
        for (int i = 0; i < count; i++)
        {
            if (!ReadSector(scratch))
            {
                return false;
            }
        }
        return true;
    }

    public void Dispose() => CloseAudio();

    private void CloseAudio()
    {
        _audio?.Dispose();
        _audio = null;
        _audioEnded = false;
    }

    private static int ReadFully(Stream s, Span<byte> destination)
    {
        int total = 0;
        while (total < destination.Length)
        {
            int n = s.Read(destination.Slice(total));
            if (n <= 0) break;
            total += n;
        }
        return total;
    }
}
