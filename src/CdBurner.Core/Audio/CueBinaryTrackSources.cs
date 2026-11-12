using CdBurner.Core.Primitives;

namespace CdBurner.Core.Audio;

/// <summary>
/// A CD-ROM Mode 1 data track from a CUE sheet's BINARY file: track 1 of a mixed-mode CD (data, then audio).
/// The file holds either 2,048-byte sectors (<c>MODE1/2048</c>) or whole 2,352-byte sectors (<c>MODE1/2352</c>); either
/// way the writer is given the 2,048 bytes of user data per sector, and the drive adds sync, header and error correction.
/// </summary>
/// <param name="FilePath">The BINARY file.</param>
/// <param name="OffsetBytes">Where the track's INDEX 01 starts in the file.</param>
/// <param name="SectorCount">Sectors of the track kept from the file.</param>
/// <param name="SectorSize">2,048 or 2,352 bytes a sector in the file.</param>
public sealed record DataTrackImage(string FilePath, long OffsetBytes, int SectorCount, int SectorSize)
{
    public const int UserDataSize = 2048;
    private const int RawUserDataOffset = 16; // sync (12) + header (4)

    /// <summary>Bytes of user data the writer sends.</summary>
    public long UserDataBytes => (long)SectorCount * UserDataSize;

    /// <summary>
    /// Describes the data track at <paramref name="offsetBytes"/> in <paramref name="filePath"/>, checking that the file
    /// is long enough and, for raw sectors, that the first one is a Mode 1 sector.
    /// </summary>
    public static DataTrackImage Open(string filePath, long offsetBytes, int sectorCount, int sectorSize)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (sectorSize is not (UserDataSize or OpticalConstants.RawSectorSize))
        {
            throw new ArgumentOutOfRangeException(nameof(sectorSize), "A Mode 1 data track has 2,048- or 2,352-byte sectors.");
        }
        if (sectorCount <= 0)
        {
            throw new InvalidDataException($"The data track in '{Path.GetFileName(filePath)}' is empty.");
        }

        long length = new FileInfo(filePath).Length;
        if (offsetBytes < 0 || offsetBytes + (long)sectorCount * sectorSize > length)
        {
            throw new InvalidDataException(
                $"'{Path.GetFileName(filePath)}' ends before its data track does ({sectorCount:N0} sectors of {sectorSize} bytes from byte {offsetBytes:N0}).");
        }

        if (sectorSize == OpticalConstants.RawSectorSize)
        {
            byte[] first = new byte[16];
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            fs.Position = offsetBytes;
            fs.ReadExactly(first);
            bool sync = first[0] == 0 && first[11] == 0 && first.AsSpan(1, 10).IndexOfAnyExcept((byte)0xFF) < 0;
            if (!sync)
            {
                throw new InvalidDataException(
                    $"The data track in '{Path.GetFileName(filePath)}' does not start with a CD-ROM sector: the cue sheet's MODE1/2352 does not match the file.");
            }
            if (first[15] != 1)
            {
                throw new InvalidDataException(
                    $"The data track in '{Path.GetFileName(filePath)}' holds Mode {first[15]} sectors, not Mode 1. Only Mode 1 data tracks can be written.");
            }
        }

        return new DataTrackImage(filePath, offsetBytes, sectorCount, sectorSize);
    }

    /// <summary>
    /// How many sectors at the end of the range are not Mode 1 sectors (no sync pattern and Mode 1 header), up to
    /// <paramref name="max"/>. In a cue sheet without INDEX 00 the range of track 1 runs to track 2's INDEX 01, so it ends
    /// with track 2's audio pre-gap. Only for raw 2,352-byte sectors; 0 otherwise.
    /// </summary>
    public int CountTrailingNonDataSectors(int max)
    {
        if (SectorSize != OpticalConstants.RawSectorSize)
        {
            return 0;
        }
        byte[] header = new byte[16];
        using var fs = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        int count = 0;
        while (count < max && count < SectorCount - 1)
        {
            fs.Position = OffsetBytes + (long)(SectorCount - 1 - count) * SectorSize;
            fs.ReadExactly(header);
            bool mode1 = header[0] == 0 && header[11] == 0 && header.AsSpan(1, 10).IndexOfAnyExcept((byte)0xFF) < 0 && header[15] == 1;
            if (mode1)
            {
                break;
            }
            count++;
        }
        return count;
    }

    /// <summary>Opens the track's user data: <see cref="UserDataSize"/> bytes per sector, in order.</summary>
    public Stream OpenUserData() => new UserDataStream(this);

    /// <summary>
    /// How many sectors at the end of the track hold only zeros in their user data, up to <paramref name="max"/>. A rip
    /// of a mixed-mode CD keeps the data track's two-second post-gap at its end; the writer adds that gap itself.
    /// </summary>
    public int CountTrailingZeroSectors(int max)
    {
        byte[] sector = new byte[SectorSize];
        using var fs = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        int zeros = 0;
        while (zeros < max && zeros < SectorCount)
        {
            fs.Position = OffsetBytes + (long)(SectorCount - 1 - zeros) * SectorSize;
            fs.ReadExactly(sector);
            int start = SectorSize == UserDataSize ? 0 : RawUserDataOffset;
            if (sector.AsSpan(start, UserDataSize).IndexOfAnyExcept((byte)0) >= 0)
            {
                break;
            }
            zeros++;
        }
        return zeros;
    }

    /// <summary>Reads the user data of each sector in turn (forward only).</summary>
    private sealed class UserDataStream : Stream
    {
        private readonly DataTrackImage _image;
        private readonly FileStream _file;
        private readonly byte[] _sector;
        private int _sectorIndex;
        private int _posInSector = UserDataSize; // nothing buffered yet
        private long _delivered;

        public UserDataStream(DataTrackImage image)
        {
            _image = image;
            _file = new FileStream(image.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            _file.Position = image.OffsetBytes;
            _sector = new byte[image.SectorSize];
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int total = 0;
            while (total < buffer.Length)
            {
                if (_posInSector == UserDataSize)
                {
                    if (_sectorIndex == _image.SectorCount)
                    {
                        break;
                    }
                    _file.ReadExactly(_sector);
                    _sectorIndex++;
                    _posInSector = 0;
                }
                int start = _image.SectorSize == UserDataSize ? 0 : RawUserDataOffset;
                int n = Math.Min(buffer.Length - total, UserDataSize - _posInSector);
                _sector.AsSpan(start + _posInSector, n).CopyTo(buffer.Slice(total));
                _posInSector += n;
                total += n;
            }
            _delivered += total;
            return total;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _image.UserDataBytes;
        public override long Position
        {
            get => _delivered;
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _file.Dispose();
            base.Dispose(disposing);
        }
    }
}

/// <summary>
/// Audio straight from a CUE sheet's BINARY (little-endian) or MOTOROLA (big-endian) file: 16-bit stereo samples at
/// 44.1 kHz with no header, as a CD stores them. No decoder is involved; a track is a range of bytes in the file.
/// </summary>
public static class RawCdAudioFile
{
    /// <summary>The CUE sheet FILE types whose bytes are raw CD sectors.</summary>
    public static bool IsRawType(string fileType) =>
        string.Equals(fileType, "BINARY", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(fileType, "MOTOROLA", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A factory for the audio from <paramref name="startFrame"/> for <paramref name="frameCount"/> frames (to the end of
    /// the file when null), as little-endian Red Book PCM.
    /// </summary>
    public static Func<Stream> CreateSliceFactory(string filePath, int startFrame, int? frameCount, bool bigEndian) => () =>
    {
        long fileFrames = new FileInfo(filePath).Length / OpticalConstants.RawSectorSize;
        long start = Math.Clamp(startFrame, 0, fileFrames);
        long frames = frameCount.HasValue ? Math.Clamp(frameCount.Value, 0, fileFrames - start) : fileFrames - start;
        return new FileSliceStream(filePath, start * OpticalConstants.RawSectorSize, frames * OpticalConstants.RawSectorSize, bigEndian);
    };

    /// <summary>Whole frames in the file.</summary>
    public static int FrameCount(string filePath) =>
        (int)Math.Min(int.MaxValue, new FileInfo(filePath).Length / OpticalConstants.RawSectorSize);

    /// <summary>A read-only, seekable window onto part of a file, with 16-bit samples byte-swapped when asked.</summary>
    private sealed class FileSliceStream : Stream
    {
        private readonly FileStream _file;
        private readonly long _start;
        private readonly long _length;
        private readonly bool _swap;
        private long _position;

        public FileSliceStream(string path, long start, long length, bool swap)
        {
            _file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            _start = start;
            _length = length;
            _swap = swap;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            long left = _length - _position;
            if (left <= 0) return 0;
            if (buffer.Length > left) buffer = buffer.Slice(0, (int)left);
            if (_swap && (_position & 1) != 0)
            {
                throw new InvalidOperationException("Big-endian audio is read in whole samples.");
            }
            _file.Position = _start + _position;
            int n = _file.Read(buffer);
            if (_swap)
            {
                n &= ~1;
                for (int i = 0; i + 1 < n; i += 2)
                {
                    (buffer[i], buffer[i + 1]) = (buffer[i + 1], buffer[i]);
                }
            }
            _position += n;
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position
        {
            get => _position;
            set => _position = Math.Clamp(value, 0, _length);
        }
        public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => _length + offset
        };
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _file.Dispose();
            base.Dispose(disposing);
        }
    }
}
