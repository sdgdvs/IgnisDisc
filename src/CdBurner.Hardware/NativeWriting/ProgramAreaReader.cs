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

    public ProgramAreaReader(SaoAudioLayout layout, Func<int, Stream> openTrackAudio, Action<string>? note = null)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _openTrackAudio = openTrackAudio ?? throw new ArgumentNullException(nameof(openTrackAudio));
        _note = note ?? (_ => { });
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
