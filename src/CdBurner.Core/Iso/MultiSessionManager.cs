using CdBurner.Core.Abstractions;
using CdBurner.Core.Compilation;
using CdBurner.Core.Primitives;

namespace CdBurner.Core.Iso;

/// <summary>
/// Manages multi-session optical disc mastering, session importing, and tree merging.
/// Allows importing previously recorded directory hierarchies, adding/modifying/removing files,
/// and generating new session images that point to existing file extents without duplicating data.
/// </summary>
public static class MultiSessionManager
{
    /// <summary>
    /// Imports the directory tree from a previously recorded optical session on disc or disc image stream.
    /// Files present in the previous session have <see cref="CompilationFile.ImportedStartLba"/> set to their
    /// on-disc LBA so they do not need to be re-written in subsequent sessions.
    /// </summary>
    /// <param name="previousSessionStream">Readable and seekable stream representing the disc medium or session image.</param>
    /// <param name="sessionStartLba">Starting LBA of the session to import (default is 0 for the first session).</param>
    /// <returns>A new <see cref="CompilationDirectory"/> containing the imported filesystem hierarchy.</returns>
    public static CompilationDirectory ImportSessionTree(Stream previousSessionStream, int sessionStartLba = 0)
    {
        ArgumentNullException.ThrowIfNull(previousSessionStream);

        using var reader = IsoImageReader.FromStream(previousSessionStream, leaveOpen: true, sessionStartLba);
        var root = CompilationDirectory.CreateRoot();

        PopulateDirectory(reader.RootDirectory, root, previousSessionStream);
        return root;
    }

    /// <summary>
    /// Asynchronously imports the active or specified session directory tree directly from an optical drive recorder backend.
    /// </summary>
    /// <param name="recorder">The optical recorder backend holding the medium.</param>
    /// <param name="engine">The burn engine backend capable of reading sectors from the recorder.</param>
    /// <param name="sessionIndex">Optional 0-based session index to import (defaults to the last session).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A new <see cref="CompilationDirectory"/> containing the imported filesystem hierarchy.</returns>
    public static async Task<CompilationDirectory> ImportFromRecorderAsync(
        IDiscRecorderBackend recorder,
        IBurnEngineBackend engine,
        int? sessionIndex = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(engine);

        var status = recorder.QueryMediaStatus();
        if (!status.MediaPresent || status.IsBlank)
        {
            throw new InvalidOperationException("No recorded media present in the recorder to import from.");
        }

        // Fast-path: if the recorder provides a direct stream (e.g. VirtualDiscRecorder)
        if (recorder.GetType().GetMethod("CreateReadStream")?.Invoke(recorder, null) is Stream fastStream)
        {
            using (fastStream)
            {
                int startLba = DetermineSessionStartLba(recorder, sessionIndex, status);
                return ImportSessionTree(fastStream, startLba);
            }
        }

        // Hardware / generic fallback: read sectors starting at sessionStartLba into memory stream
        int sessionStartLba = DetermineSessionStartLba(recorder, sessionIndex, status);
        int remainingSectorsOnDisc = status.UsedSectors > sessionStartLba
            ? status.UsedSectors - sessionStartLba
            : 1000;
        int sectorsToRead = Math.Min(remainingSectorsOnDisc > 0 ? remainingSectorsOnDisc : 512, 2048);

        var memoryStream = new MemoryStream();
        var ripResult = await engine.ReadSectorsAsync(
            recorder,
            startLba: sessionStartLba,
            sectorCount: sectorsToRead,
            raw2352Mode: false,
            sectorConsumer: async (lba, memory) =>
            {
                await memoryStream.WriteAsync(memory, cancellationToken).ConfigureAwait(false);
            },
            progress: null,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!ripResult.Success)
        {
            throw new InvalidOperationException($"Failed to read sectors from media for session import: {ripResult.ErrorMessage}", ripResult.Exception);
        }

        memoryStream.Position = 0;
        Stream sessionStream = sessionStartLba > 0
            ? new SessionOffsetStream(memoryStream, (long)sessionStartLba * OpticalConstants.DataSectorSize)
            : memoryStream;
        return ImportSessionTree(sessionStream, sessionStartLba);
    }

    /// <summary>
    /// Compiles an appended session image stream starting at <paramref name="nextWritableLba"/>.
    /// New volume descriptors and directory records are placed at <paramref name="nextWritableLba"/>,
    /// previously recorded files point directly to their <see cref="CompilationFile.ImportedStartLba"/>
    /// without re-writing their payload bytes, and new/modified files have their data extents allocated
    /// after the descriptors and written to the stream.
    /// </summary>
    /// <param name="mergedRoot">Root directory containing the merged compilation tree.</param>
    /// <param name="nextWritableLba">The starting LBA on media where the new session will be burned.</param>
    /// <param name="options">Optional ISO build options (defaults to ISO 9660 + Joliet, UDF disabled by default for appended sessions).</param>
    /// <returns>A seekable <see cref="MemoryStream"/> containing the new session's sectors, positioned at 0.</returns>
    public static MemoryStream BuildAppendedSessionStream(
        CompilationDirectory mergedRoot,
        int nextWritableLba,
        IsoBuildOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(mergedRoot);
        ArgumentOutOfRangeException.ThrowIfNegative(nextWritableLba);

        options ??= new IsoBuildOptions(IncludeUdf: false);
        return IsoUdfImageBuilder.BuildToMemoryStream(mergedRoot, options, sessionStartLba: nextWritableLba);
    }

    /// <summary>
    /// Opens an <see cref="IsoImageReader"/> targeting a specific session on a stream.
    /// </summary>
    public static IsoImageReader OpenSessionReader(Stream stream, int sessionStartLba = 0)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return IsoImageReader.FromStream(stream, leaveOpen: true, sessionStartLba);
    }

    #region Tree Traversal Helpers

    private static void PopulateDirectory(
        IsoDirectoryEntry isoDir,
        CompilationDirectory targetDir,
        Stream previousSessionStream)
    {
        foreach (var child in isoDir.Children)
        {
            if (child is IsoDirectoryEntry subDir)
            {
                var newSubDir = targetDir.AddDirectory(subDir.Name);
                newSubDir.LastWriteTimeUtc = subDir.RecordingTimeUtc;
                if (subDir.IsHidden)
                {
                    newSubDir.Attributes |= FileAttributes.Hidden;
                }

                PopulateDirectory(subDir, newSubDir, previousSessionStream);
            }
            else if (child is IsoFileEntry file)
            {
                int fileStartLba = file.StartLba;
                long fileSizeBytes = file.SizeBytes;

                var compFile = new CompilationFile(
                    name: file.Name,
                    sourceFilePath: string.Empty,
                    sizeBytes: fileSizeBytes,
                    importedStartLba: fileStartLba,
                    streamFactory: () => CreateImportedFileStream(previousSessionStream, fileStartLba, fileSizeBytes),
                    parent: targetDir,
                    lastWriteTimeUtc: file.RecordingTimeUtc,
                    attributes: file.IsHidden ? FileAttributes.Hidden : FileAttributes.Normal);

                targetDir.AddFile(compFile);
            }
        }
    }

    private static Stream CreateImportedFileStream(Stream stream, int startLba, long sizeBytes)
    {
        if (sizeBytes <= 0)
        {
            return Stream.Null;
        }

        if (stream.CanSeek)
        {
            byte[] data = new byte[sizeBytes];
            lock (stream)
            {
                long prevPos = stream.Position;
                stream.Position = (long)startLba * OpticalConstants.DataSectorSize;
                int read = 0;
                while (read < sizeBytes)
                {
                    int r = stream.Read(data, read, (int)(sizeBytes - read));
                    if (r == 0) break;
                    read += r;
                }
                stream.Position = prevPos;
            }
            return new MemoryStream(data, writable: false);
        }

        return Stream.Null;
    }

    private static int DetermineSessionStartLba(
        IDiscRecorderBackend recorder,
        int? sessionIndex,
        MediaStatusDto status)
    {
        // If VirtualDiscRecorder, inspect recorded session list
        var mediumProp = recorder.GetType().GetProperty("CurrentMedium");
        if (mediumProp?.GetValue(recorder) is object mediumObj)
        {
            var sessionsProp = mediumObj.GetType().GetProperty("Sessions");
            if (sessionsProp?.GetValue(mediumObj) is System.Collections.IList sessions && sessions.Count > 0)
            {
                int idx = sessionIndex ?? (sessions.Count - 1);
                if (idx >= 0 && idx < sessions.Count)
                {
                    var sessionItem = sessions[idx];
                    var startLbaProp = sessionItem?.GetType().GetProperty("StartLba");
                    if (startLbaProp?.GetValue(sessionItem) is int sLba)
                    {
                        return sLba;
                    }
                }
            }
        }

        return 0;
    }

    private sealed class SessionOffsetStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _baseOffset;
        private long _position;

        public SessionOffsetStream(Stream inner, long baseOffset)
        {
            _inner = inner;
            _baseOffset = baseOffset;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _baseOffset + _inner.Length;

        public override long Position
        {
            get => _position;
            set
            {
                ArgumentOutOfRangeException.ThrowIfNegative(value);
                _position = value;
            }
        }

        public override void Flush() => _inner.Flush();

        public override int Read(byte[] buffer, int offset, int count)
        {
            return Read(buffer.AsSpan(offset, count));
        }

        public override int Read(Span<byte> buffer)
        {
            if (buffer.IsEmpty || _position >= Length)
            {
                return 0;
            }

            int totalRead = 0;
            if (_position < _baseOffset)
            {
                int zeroBytes = (int)Math.Min(buffer.Length, _baseOffset - _position);
                buffer.Slice(0, zeroBytes).Clear();
                _position += zeroBytes;
                totalRead += zeroBytes;
                buffer = buffer.Slice(zeroBytes);
            }

            if (!buffer.IsEmpty && _position < Length)
            {
                _inner.Position = _position - _baseOffset;
                int r = _inner.Read(buffer);
                _position += r;
                totalRead += r;
            }

            return totalRead;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            long target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => Length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
            Position = target;
            return _position;
        }

        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    #endregion
}
