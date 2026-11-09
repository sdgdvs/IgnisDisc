using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace CdBurner.Hardware.Interop;

/// <summary>
/// Managed bridge implementing COM IStream over any System.IO.Stream.
/// Passes managed streams to Windows IMAPIv2 COM interfaces as IStream.
/// </summary>
public sealed class ManagedComStream : IStream, IDisposable
{
    private const int StatFlagNoName = 1;      // STATFLAG_NONAME
    private const int StgTyStream    = 2;      // STGTY_STREAM

    private const int StreamSeekSet  = 0;      // STREAM_SEEK_SET
    private const int StreamSeekCur  = 1;      // STREAM_SEEK_CUR
    private const int StreamSeekEnd  = 2;      // STREAM_SEEK_END

    private readonly Stream _sourceStream;
    private readonly bool _leaveOpen;
    private readonly Action<int>? _onBytesRead;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of <see cref="ManagedComStream"/> wrapping the specified <see cref="Stream"/>.
    /// </summary>
    /// <param name="sourceStream">The underlying managed stream to wrap.</param>
    /// <param name="leaveOpen">True to leave the source stream open after disposing the wrapper; false to dispose it.</param>
    /// <param name="onBytesRead">Optional callback invoked whenever bytes are read from the stream.</param>
    public ManagedComStream(Stream sourceStream, bool leaveOpen = false, Action<int>? onBytesRead = null)
    {
        _sourceStream = sourceStream ?? throw new ArgumentNullException(nameof(sourceStream));
        _leaveOpen = leaveOpen;
        _onBytesRead = onBytesRead;
    }

    /// <summary>
    /// Gets the underlying managed stream.
    /// </summary>
    public Stream SourceStream
    {
        get
        {
            ThrowIfDisposed();
            return _sourceStream;
        }
    }

    /// <summary>
    /// Gets a value indicating whether the underlying stream remains open after disposal.
    /// </summary>
    public bool LeaveOpen => _leaveOpen;

    /// <summary>
    /// Gets a value indicating whether this instance has been disposed.
    /// </summary>
    public bool IsDisposed => _disposed;

    /// <summary>
    /// Reads up to <paramref name="cb"/> bytes from the stream into <paramref name="pv"/>.
    /// </summary>
    public void Read(byte[] pv, int cb, nint pcbRead)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(pv);
        if (cb < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(cb), "Byte count must be non-negative.");
        }

        int toRead = Math.Min(pv.Length, cb);
        int bytesRead = _sourceStream.Read(pv, 0, toRead);

        if (pcbRead != nint.Zero)
        {
            Marshal.WriteInt32(pcbRead, bytesRead);
        }

        if (bytesRead > 0)
        {
            _onBytesRead?.Invoke(bytesRead);
        }
    }

    /// <summary>
    /// Writes up to <paramref name="cb"/> bytes from <paramref name="pv"/> into the stream.
    /// </summary>
    public void Write(byte[] pv, int cb, nint pcbWritten)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(pv);
        if (cb < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(cb), "Byte count must be non-negative.");
        }

        int toWrite = Math.Min(pv.Length, cb);
        _sourceStream.Write(pv, 0, toWrite);

        if (pcbWritten != nint.Zero)
        {
            Marshal.WriteInt32(pcbWritten, toWrite);
        }
    }

    /// <summary>
    /// Seeks to a specified position within the stream.
    /// </summary>
    public void Seek(long dlibMove, int dwOrigin, nint plibNewPosition)
    {
        ThrowIfDisposed();

        SeekOrigin origin = dwOrigin switch
        {
            StreamSeekSet => SeekOrigin.Begin,
            StreamSeekCur => SeekOrigin.Current,
            StreamSeekEnd => SeekOrigin.End,
            _ => throw new ArgumentOutOfRangeException(nameof(dwOrigin), $"Invalid origin constant: {dwOrigin}")
        };

        long newPosition = _sourceStream.Seek(dlibMove, origin);

        if (plibNewPosition != nint.Zero)
        {
            Marshal.WriteInt64(plibNewPosition, newPosition);
        }
    }

    /// <summary>
    /// Changes the size of the stream.
    /// </summary>
    public void SetSize(long libNewSize)
    {
        ThrowIfDisposed();
        _sourceStream.SetLength(libNewSize);
    }

    /// <summary>
    /// Copies a specified number of bytes from this stream to another COM IStream.
    /// </summary>
    public void CopyTo(IStream pstm, long cb, nint pcbRead, nint pcbWritten)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(pstm);

        byte[] buffer = new byte[81920];
        long totalRead = 0;
        long totalWritten = 0;
        ulong targetBytes = (ulong)cb;

        while ((ulong)totalWritten < targetBytes)
        {
            ulong remaining = targetBytes - (ulong)totalWritten;
            int toRead = (int)Math.Min((ulong)buffer.Length, remaining);
            int read = _sourceStream.Read(buffer, 0, toRead);
            if (read == 0)
            {
                break;
            }

            _onBytesRead?.Invoke(read);
            totalRead += read;
            pstm.Write(buffer, read, nint.Zero);
            totalWritten += read;
        }

        if (pcbRead != nint.Zero)
        {
            Marshal.WriteInt64(pcbRead, totalRead);
        }

        if (pcbWritten != nint.Zero)
        {
            Marshal.WriteInt64(pcbWritten, totalWritten);
        }
    }

    /// <summary>
    /// Flushes any pending data to the underlying stream.
    /// </summary>
    public void Commit(int grfCommitFlags)
    {
        ThrowIfDisposed();
        _sourceStream.Flush();
    }

    /// <summary>
    /// Discards changes since the last Commit (no-op for non-transacted streams).
    /// </summary>
    public void Revert()
    {
        ThrowIfDisposed();
        // Managed streams do not support transacted rollback; no-op.
    }

    /// <summary>
    /// Region locking is not supported by managed stream wrappers.
    /// </summary>
    public void LockRegion(long libOffset, long cb, int dwLockType)
    {
        ThrowIfDisposed();
        throw new NotSupportedException("LockRegion is not supported by ManagedComStream.");
    }

    /// <summary>
    /// Region unlocking is not supported by managed stream wrappers.
    /// </summary>
    public void UnlockRegion(long libOffset, long cb, int dwLockType)
    {
        ThrowIfDisposed();
        throw new NotSupportedException("UnlockRegion is not supported by ManagedComStream.");
    }

    /// <summary>
    /// Returns statistical data about the stream, including size and access mode.
    /// </summary>
    public void Stat(out STATSTG pstatstg, int grfStatFlag)
    {
        ThrowIfDisposed();

        pstatstg = new STATSTG
        {
            type = StgTyStream,
            cbSize = _sourceStream.CanSeek ? _sourceStream.Length : 0,
            grfLocksSupported = 0,
            grfMode = _sourceStream.CanRead && _sourceStream.CanWrite ? 2 : (_sourceStream.CanWrite ? 1 : 0)
        };

        if ((grfStatFlag & StatFlagNoName) == 0)
        {
            pstatstg.pwcsName = (_sourceStream as FileStream)?.Name ?? "ManagedComStream";
        }
    }

    /// <summary>
    /// Creates a cloned stream wrapper pointing to the same data at the same position.
    /// Supported when wrapping a <see cref="MemoryStream"/>.
    /// </summary>
    public void Clone(out IStream ppstm)
    {
        ThrowIfDisposed();

        if (_sourceStream is MemoryStream mem)
        {
            var copy = new MemoryStream(mem.ToArray(), 0, (int)mem.Length, writable: true, publiclyVisible: true)
            {
                Position = mem.Position
            };
            ppstm = new ManagedComStream(copy, leaveOpen: false);
        }
        else
        {
            throw new NotSupportedException("Clone is only supported when wrapping a MemoryStream.");
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            if (!_leaveOpen)
            {
                _sourceStream.Dispose();
            }
        }
        GC.SuppressFinalize(this);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
