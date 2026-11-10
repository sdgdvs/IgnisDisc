using CdBurner.Core.Primitives;

namespace CdBurner.Core.Images;

/// <summary>
/// A read-only, seekable stream over 2,048-byte disc sectors fetched on demand, so an <see cref="Iso.IsoImageReader"/>
/// can parse a disc's file system straight from the drive with no drive letter. Reads are done
/// <see cref="ChunkSectors"/> at a time and the last chunk is kept, since the reader asks for one sector at a time.
/// </summary>
public sealed class SectorReadStream : Stream
{
    /// <summary>Sectors fetched per read.</summary>
    public const int ChunkSectors = 16;

    private readonly Func<int, int, byte[], string?> _readSectors;
    private readonly long _length;
    private readonly byte[] _chunk = new byte[ChunkSectors * OpticalConstants.DataSectorSize];
    private int _chunkLba = -1;
    private int _chunkCount;
    private long _position;

    /// <param name="readSectors">Reads (lba, count) into the buffer; returns null on success or an error message.</param>
    /// <param name="totalSectors">Sectors on the disc, from LBA 0; the stream's length.</param>
    public SectorReadStream(Func<int, int, byte[], string?> readSectors, int totalSectors)
    {
        _readSectors = readSectors ?? throw new ArgumentNullException(nameof(readSectors));
        _length = (long)Math.Max(0, totalSectors) * OpticalConstants.DataSectorSize;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => _position = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length && _position < _length)
        {
            int lba = (int)(_position / OpticalConstants.DataSectorSize);
            if (lba < _chunkLba || lba >= _chunkLba + _chunkCount)
            {
                int totalSectors = (int)(_length / OpticalConstants.DataSectorSize);
                int count = Math.Min(ChunkSectors, totalSectors - lba);
                string? error = _readSectors(lba, count, _chunk);
                if (error != null)
                {
                    _chunkLba = -1;
                    throw new IOException($"Could not read sector {lba}: {error}");
                }
                _chunkLba = lba;
                _chunkCount = count;
            }

            int offsetInChunk = (int)(_position - (long)_chunkLba * OpticalConstants.DataSectorSize);
            int available = (int)Math.Min(_chunkCount * OpticalConstants.DataSectorSize - offsetInChunk, _length - _position);
            int n = Math.Min(available, buffer.Length - total);
            _chunk.AsSpan(offsetInChunk, n).CopyTo(buffer[total..]);
            total += n;
            _position += n;
        }
        return total;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => _length + offset
        };
        return _position;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
