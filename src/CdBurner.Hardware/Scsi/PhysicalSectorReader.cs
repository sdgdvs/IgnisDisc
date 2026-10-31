using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Primitives;
using Microsoft.Win32.SafeHandles;

namespace CdBurner.Hardware.Scsi;

/// <summary>
/// Reads sectors from a physical optical drive.
/// <para>
/// Preferred path: SCSI pass-through (READ(10) for 2048-byte user data, READ CD for raw 2352-byte sectors),
/// which works for CD, DVD and Blu-ray. If pass-through is unavailable (for example the handle could only be
/// opened read-only), it falls back to plain volume reads for 2048-byte data and to IOCTL_CDROM_RAW_READ for
/// CD-DA audio, neither of which require administrator rights.
/// </para>
/// The result contract mirrors <c>VirtualBurnEngine.ReadSectorsAsync</c>: sectors are delivered in order, and on an
/// unrecoverable read error the operation stops and returns a failed result listing the bad LBA, so callers can retry.
/// </summary>
public static partial class PhysicalSectorReader
{
    private const int ChunkSectors = 16; // 32 KiB data / 37,632 B raw per command: below common 64 KiB transfer limits
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    private const uint IOCTL_CDROM_READ_TOC = 0x00024000;
    private const uint FSCTL_ALLOW_EXTENDED_DASD_IO = 0x00090083;
    private const uint IOCTL_DISK_GET_LENGTH_INFO = 0x0007405C;
    private const int TrackModeCdda = 2;

    public static Task<RipOperationResult> ReadAsync(
        string volumePath,
        int startLba,
        int sectorCount,
        bool raw2352Mode,
        Func<int, ReadOnlyMemory<byte>, ValueTask> sectorConsumer,
        IProgress<BurnProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sectorConsumer);

        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(RipOperationResult.Failed("Reading from physical drives is only supported on Windows."));
        }

        if (string.IsNullOrWhiteSpace(volumePath))
        {
            return Task.FromResult(RipOperationResult.Failed("The drive has no drive letter assigned, so it cannot be read."));
        }

        if (startLba < 0 || sectorCount < 0)
        {
            return Task.FromResult(RipOperationResult.Failed($"Invalid sector range (start {startLba}, count {sectorCount})."));
        }

        // Device I/O is blocking; keep it off the caller's (possibly UI) thread.
        return Task.Run(() => ReadCoreAsync(volumePath, startLba, sectorCount, raw2352Mode, sectorConsumer, progress, cancellationToken), cancellationToken);
    }

    private static async Task<RipOperationResult> ReadCoreAsync(
        string volumePath,
        int startLba,
        int sectorCount,
        bool raw2352Mode,
        Func<int, ReadOnlyMemory<byte>, ValueTask> sectorConsumer,
        IProgress<BurnProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        int sectorSize = raw2352Mode ? OpticalConstants.RawSectorSize : OpticalConstants.DataSectorSize;

        using ISectorSource? source = OpenBestSource(volumePath, raw2352Mode, out string? openError);
        if (source == null)
        {
            return RipOperationResult.Failed(openError ?? $"Unable to open drive {volumePath} for reading.");
        }

        byte[] buffer = new byte[ChunkSectors * sectorSize];
        int readCount = 0;
        long bytesRead = 0;

        while (readCount < sectorCount)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int lba = startLba + readCount;
            int chunk = Math.Min(ChunkSectors, sectorCount - readCount);

            string? error = source.Read(lba, chunk, buffer);
            if (error != null)
            {
                // Narrow the failure down to the first unreadable sector, delivering the good ones before it.
                for (int i = 0; i < chunk; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string? singleError = source.Read(lba + i, 1, buffer);
                    if (singleError != null)
                    {
                        int badLba = lba + i;
                        return new RipOperationResult
                        {
                            Success = false,
                            ErrorMessage = $"Unrecovered read error at LBA {badLba}: {singleError}",
                            SectorsRead = readCount,
                            BadSectorsCount = 1,
                            BadSectorsList = new[] { badLba },
                            TotalTime = sw.Elapsed,
                            Exception = new IOException(singleError)
                        };
                    }

                    await sectorConsumer(lba + i, buffer.AsMemory(0, sectorSize)).ConfigureAwait(false);
                    readCount++;
                    bytesRead += sectorSize;
                }
                continue;
            }

            for (int i = 0; i < chunk; i++)
            {
                await sectorConsumer(lba + i, buffer.AsMemory(i * sectorSize, sectorSize)).ConfigureAwait(false);
            }

            readCount += chunk;
            bytesRead += (long)chunk * sectorSize;

            if (progress != null)
            {
                double seconds = Math.Max(0.001, sw.Elapsed.TotalSeconds);
                long bytesPerSecond = (long)(bytesRead / seconds);
                double oneX = raw2352Mode ? OpticalConstants.RawSectorSize * 75.0 : OpticalConstants.CdData1XBytesPerSec;
                progress.Report(new BurnProgressSnapshot
                {
                    ElapsedTime = sw.Elapsed,
                    RemainingTime = readCount > 0 ? TimeSpan.FromSeconds(seconds * (sectorCount - readCount) / readCount) : TimeSpan.Zero,
                    CurrentLba = readCount,
                    TotalLba = sectorCount,
                    BufferLevelPercent = 100.0,
                    CurrentBytesPerSecond = bytesPerSecond,
                    CurrentSpeedMultiplier = bytesPerSecond / oneX,
                    CurrentActionDescription = "ReadingSectors"
                });
            }
        }

        return RipOperationResult.Succeeded(readCount, sw.Elapsed);
    }

    /// <summary>
    /// Reads the CD Table of Contents via IOCTL_CDROM_READ_TOC (read-only handle; no elevation needed).
    /// </summary>
    public static IReadOnlyList<DiscTocTrack> ReadTableOfContents(string volumePath)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(volumePath))
        {
            return Array.Empty<DiscTocTrack>();
        }

        using var handle = OpenDevice(volumePath, FileAccess.Read, out _);
        if (handle == null)
        {
            return Array.Empty<DiscTocTrack>();
        }

        // CDROM_TOC: Length[2], FirstTrack, LastTrack, TRACK_DATA[100] (8 bytes each) = 804 bytes
        byte[] toc = new byte[804];
        if (!DeviceIoControlBuffer(handle, IOCTL_CDROM_READ_TOC, Array.Empty<byte>(), toc, out _))
        {
            return Array.Empty<DiscTocTrack>();
        }

        int first = toc[2];
        int last = toc[3];
        if (first < 1 || last < first || last > 99)
        {
            return Array.Empty<DiscTocTrack>();
        }

        var entries = new List<(int Number, int Lba, bool IsData)>();
        for (int i = 0; i <= last - first + 1; i++) // includes lead-out (track 0xAA)
        {
            int off = 4 + i * 8;
            byte control = (byte)(toc[off + 1] & 0x0F);
            int number = toc[off + 2];
            int lba = (toc[off + 5] * 60 + toc[off + 6]) * 75 + toc[off + 7] - 150; // MSF address
            entries.Add((number, lba, (control & 0x04) != 0));
        }

        var tracks = new List<DiscTocTrack>();
        for (int i = 0; i < entries.Count - 1; i++)
        {
            var cur = entries[i];
            var next = entries[i + 1];
            int count = next.Lba - cur.Lba;

            // Enhanced CD: the audio session ends 11,400 sectors (lead-out + lead-in + pre-gap) before the data track.
            if (!cur.IsData && next.IsData && next.Number != 0xAA)
            {
                count -= 11_400;
            }

            if (count > 0)
            {
                tracks.Add(new DiscTocTrack(cur.Number, cur.Lba, count, !cur.IsData));
            }
        }

        return tracks;
    }

    /// <summary>
    /// Returns the number of readable sectors on the inserted disc (last recorded LBA + 1), or null if unknown.
    /// Uses READ CAPACITY via pass-through, then IOCTL_DISK_GET_LENGTH_INFO on a read-only handle.
    /// </summary>
    public static int? ReadCapacity(string volumePath)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(volumePath))
        {
            return null;
        }

        using (var rw = OpenDevice(volumePath, FileAccess.ReadWrite, out _))
        {
            if (rw != null)
            {
                using var transport = new Win32ScsiTransport(rw, ownsHandle: false);
                byte[] data = new byte[8];
                byte[] cdb = new byte[10];
                cdb[0] = 0x25; // READ CAPACITY (10)
                var result = transport.ExecuteCommand(cdb, data, ScsiPassThroughNative.SCSI_IOCTL_DATA_IN, TimeSpan.FromSeconds(10));
                if (result.IsSuccess)
                {
                    uint lastLba = (uint)(data[0] << 24 | data[1] << 16 | data[2] << 8 | data[3]);
                    if (lastLba > 0 && lastLba < int.MaxValue)
                    {
                        return (int)lastLba + 1;
                    }
                }
            }
        }

        using var ro = OpenDevice(volumePath, FileAccess.Read, out _);
        if (ro != null)
        {
            byte[] length = new byte[8];
            if (DeviceIoControlBuffer(ro, IOCTL_DISK_GET_LENGTH_INFO, Array.Empty<byte>(), length, out _))
            {
                long bytes = BitConverter.ToInt64(length, 0);
                if (bytes > 0)
                {
                    return (int)Math.Min(int.MaxValue, bytes / OpticalConstants.DataSectorSize);
                }
            }
        }

        return null;
    }

    #region Sector sources

    private interface ISectorSource : IDisposable
    {
        /// <summary>Reads <paramref name="count"/> sectors into <paramref name="buffer"/>; returns null on success or an error message.</summary>
        string? Read(int lba, int count, byte[] buffer);
    }

    private static ISectorSource? OpenBestSource(string volumePath, bool raw, out string? error)
    {
        error = null;

        // 1. SCSI pass-through needs a read/write handle.
        var rw = OpenDevice(volumePath, FileAccess.ReadWrite, out int rwError);
        if (rw != null)
        {
            var spti = new SptiSource(rw, raw);
            if (spti.Probe())
            {
                return spti;
            }
            spti.Dispose();
        }

        // 2. Read-only fallbacks
        var ro = OpenDevice(volumePath, FileAccess.Read, out int roError);
        if (ro == null)
        {
            error = $"Unable to open drive {volumePath} (Win32 error {(roError != 0 ? roError : rwError)}: {new Win32Exception(roError != 0 ? roError : rwError).Message}).";
            return null;
        }

        if (raw)
        {
            return new RawReadSource(ro);
        }

        DeviceIoControlBuffer(ro, FSCTL_ALLOW_EXTENDED_DASD_IO, Array.Empty<byte>(), Array.Empty<byte>(), out _);
        return new VolumeSource(ro);
    }

    private sealed class SptiSource : ISectorSource
    {
        private readonly SafeFileHandle _handle;
        private readonly Win32ScsiTransport _transport;
        private readonly bool _raw;

        public SptiSource(SafeFileHandle handle, bool raw)
        {
            _handle = handle;
            _transport = new Win32ScsiTransport(handle, ownsHandle: false);
            _raw = raw;
        }

        /// <summary>TEST UNIT READY: confirms pass-through is permitted on this handle.</summary>
        public bool Probe()
        {
            var result = _transport.ExecuteCommand(MmcCommandBuilder.BuildTestUnitReadyCdb(), Span<byte>.Empty,
                ScsiPassThroughNative.SCSI_IOCTL_DATA_UNSPECIFIED, TimeSpan.FromSeconds(10));
            // A SCSI check condition (e.g. "not ready") still proves the command reached the drive.
            return result.IsSuccess || result.Win32Error == 0;
        }

        public string? Read(int lba, int count, byte[] buffer)
        {
            int sectorSize = _raw ? OpticalConstants.RawSectorSize : OpticalConstants.DataSectorSize;
            var span = buffer.AsSpan(0, count * sectorSize);
            byte[] cdb = _raw
                ? MmcCommandBuilder.BuildReadCdCdb(lba, count, expectedSectorType: 0, mainChannelSelectionBits: 0xF8)
                : BuildRead10Cdb(lba, count);

            ScsiExecutionResult result = default!;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                result = _transport.ExecuteCommand(cdb, span, ScsiPassThroughNative.SCSI_IOCTL_DATA_IN, CommandTimeout);
                if (result.IsSuccess && result.DataTransferLength >= span.Length)
                {
                    return null;
                }
            }

            return result.IsSuccess
                ? $"short transfer ({result.DataTransferLength} of {span.Length} bytes)"
                : result.ErrorMessage ?? "read failed";
        }

        public void Dispose()
        {
            _transport.Dispose();
            _handle.Dispose();
        }
    }

    private sealed class VolumeSource : ISectorSource
    {
        private readonly SafeFileHandle _handle;
        public VolumeSource(SafeFileHandle handle) => _handle = handle;

        public string? Read(int lba, int count, byte[] buffer)
        {
            try
            {
                int length = count * OpticalConstants.DataSectorSize;
                int total = 0;
                while (total < length)
                {
                    int n = RandomAccess.Read(_handle, buffer.AsSpan(total, length - total), (long)lba * OpticalConstants.DataSectorSize + total);
                    if (n <= 0)
                    {
                        return "end of media";
                    }
                    total += n;
                }
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return ex.Message;
            }
        }

        public void Dispose() => _handle.Dispose();
    }

    private sealed class RawReadSource : ISectorSource
    {
        private const uint IOCTL_CDROM_RAW_READ = ScsiPassThroughNative.IOCTL_CDROM_RAW_READ;
        private readonly SafeFileHandle _handle;
        public RawReadSource(SafeFileHandle handle) => _handle = handle;

        public string? Read(int lba, int count, byte[] buffer)
        {
            // RAW_READ_INFO { LARGE_INTEGER DiskOffset; ULONG SectorCount; TRACK_MODE_TYPE TrackMode; }
            byte[] info = new byte[16];
            BitConverter.TryWriteBytes(info.AsSpan(0, 8), (long)lba * OpticalConstants.DataSectorSize);
            BitConverter.TryWriteBytes(info.AsSpan(8, 4), count);
            BitConverter.TryWriteBytes(info.AsSpan(12, 4), TrackModeCdda);

            byte[] output = count * OpticalConstants.RawSectorSize == buffer.Length
                ? buffer
                : new byte[count * OpticalConstants.RawSectorSize];

            if (!DeviceIoControlBuffer(_handle, IOCTL_CDROM_RAW_READ, info, output, out int win32Error))
            {
                return win32Error == 1
                    ? "this drive only supports raw reads of audio (CD-DA) sectors without administrator rights"
                    : new Win32Exception(win32Error).Message;
            }

            if (!ReferenceEquals(output, buffer))
            {
                output.CopyTo(buffer, 0);
            }
            return null;
        }

        public void Dispose() => _handle.Dispose();
    }

    private static byte[] BuildRead10Cdb(int lba, int count)
    {
        byte[] cdb = new byte[10];
        cdb[0] = 0x28; // READ (10)
        cdb[2] = (byte)(lba >> 24);
        cdb[3] = (byte)(lba >> 16);
        cdb[4] = (byte)(lba >> 8);
        cdb[5] = (byte)lba;
        cdb[7] = (byte)(count >> 8);
        cdb[8] = (byte)count;
        return cdb;
    }

    #endregion

    #region Native

    private static SafeFileHandle? OpenDevice(string volumePath, FileAccess access, out int win32Error)
    {
        win32Error = 0;
        string letter = volumePath.Trim().TrimEnd('\\');
        string path = letter.StartsWith(@"\\", StringComparison.Ordinal) ? letter : @"\\.\" + letter;

        uint desired = 0;
        if (access.HasFlag(FileAccess.Read)) desired |= 0x80000000;  // GENERIC_READ
        if (access.HasFlag(FileAccess.Write)) desired |= 0x40000000; // GENERIC_WRITE

        var handle = CreateFileW(path, desired, 0x1 | 0x2 /* share read/write */, nint.Zero, 3 /* OPEN_EXISTING */, 0, nint.Zero);
        if (handle.IsInvalid)
        {
            win32Error = Marshal.GetLastWin32Error();
            handle.Dispose();
            return null;
        }
        return handle;
    }

    private static unsafe bool DeviceIoControlBuffer(SafeFileHandle handle, uint code, byte[] input, byte[] output, out int win32Error)
    {
        win32Error = 0;
        fixed (byte* pIn = input)
        fixed (byte* pOut = output)
        {
            bool ok = DeviceIoControl(handle, code,
                input.Length > 0 ? pIn : null, (uint)input.Length,
                output.Length > 0 ? pOut : null, (uint)output.Length,
                out _, nint.Zero);
            if (!ok)
            {
                win32Error = Marshal.GetLastWin32Error();
            }
            return ok;
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode, nint lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, nint hTemplateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool DeviceIoControl(
        SafeFileHandle hDevice, uint dwIoControlCode, void* lpInBuffer, uint nInBufferSize,
        void* lpOutBuffer, uint nOutBufferSize, out uint lpBytesReturned, nint lpOverlapped);

    #endregion
}
