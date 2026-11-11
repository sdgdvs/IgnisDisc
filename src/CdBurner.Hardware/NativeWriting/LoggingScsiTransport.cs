using System.Diagnostics;
using System.Text;
using CdBurner.Hardware.Scsi;

namespace CdBurner.Hardware.NativeWriting;

/// <summary>
/// Wraps a SCSI transport and records every command: CDB, direction, transfer length, status, sense key/ASC/ASCQ and
/// time taken. Parameter data sent with MODE SELECT and SEND CUE SHEET, and short responses, are recorded in hex too;
/// audio and lead-in data are not.
/// </summary>
public sealed class LoggingScsiTransport : IScsiTransport
{
    private const int MaxLoggedPayloadBytes = 256;

    private readonly IScsiTransport _inner;
    private readonly bool _ownsInner;
    private readonly List<string> _lines = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public LoggingScsiTransport(IScsiTransport inner, bool ownsInner = false)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _ownsInner = ownsInner;
    }

    public IReadOnlyList<string> Lines => _lines;

    public void Note(string message) => _lines.Add($"{_clock.Elapsed.TotalSeconds,9:F3}  # {message}");

    public ScsiExecutionResult ExecuteCommand(ReadOnlySpan<byte> cdb, Span<byte> dataBuffer, byte dataDirection, TimeSpan timeout)
    {
        long start = _clock.ElapsedTicks;
        string? outPayload = dataDirection == ScsiPassThroughNative.SCSI_IOCTL_DATA_OUT && IsParameterCommand(cdb[0])
            ? Hex(dataBuffer, MaxLoggedPayloadBytes)
            : null;

        ScsiExecutionResult result = _inner.ExecuteCommand(cdb, dataBuffer, dataDirection, timeout);

        double ms = (_clock.ElapsedTicks - start) * 1000.0 / Stopwatch.Frequency;
        string dir = dataDirection switch
        {
            ScsiPassThroughNative.SCSI_IOCTL_DATA_IN => "in ",
            ScsiPassThroughNative.SCSI_IOCTL_DATA_OUT => "out",
            _ => "-  "
        };

        var sb = new StringBuilder();
        sb.Append($"{_clock.Elapsed.TotalSeconds,9:F3}  {Name(cdb[0]),-22} {Hex(cdb, 16),-48} {dir} {dataBuffer.Length,7}  ");
        if (result.IsSuccess)
        {
            sb.Append("GOOD");
        }
        else if (result.SenseInfo is { } sense)
        {
            sb.Append($"CHECK {sense.AscAscqHex} {sense.Description}");
        }
        else
        {
            sb.Append($"FAIL status 0x{result.ScsiStatus:X2} win32 {result.Win32Error} {result.ErrorMessage}");
        }
        sb.Append($"  {ms:F1} ms");
        _lines.Add(sb.ToString());

        if (outPayload != null)
        {
            _lines.Add($"           data out: {outPayload}");
        }
        if (result.IsSuccess && dataDirection == ScsiPassThroughNative.SCSI_IOCTL_DATA_IN && cdb[0] != NativeWriteCommands.OpCodeReadBufferCapacity)
        {
            int n = (int)Math.Min(result.DataTransferLength, (uint)dataBuffer.Length);
            _lines.Add($"           data in:  {Hex(dataBuffer.Slice(0, n), MaxLoggedPayloadBytes)}");
        }

        return result;
    }

    public void SaveTo(string path)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        File.WriteAllLines(path, _lines);
    }

    public void Dispose()
    {
        if (_ownsInner)
        {
            _inner.Dispose();
        }
    }

    private static bool IsParameterCommand(byte opCode) =>
        opCode is NativeWriteCommands.OpCodeModeSelect10 or NativeWriteCommands.OpCodeSendCueSheet;

    private static string Name(byte opCode) => opCode switch
    {
        0x00 => "TEST UNIT READY",
        0x35 => "SYNCHRONIZE CACHE",
        0x42 => "READ SUB-CHANNEL",
        0x43 => "READ TOC",
        0xBB => "SET CD SPEED",
        0x46 => "GET CONFIGURATION",
        0x51 => "READ DISC INFORMATION",
        0x52 => "READ TRACK INFORMATION",
        0x5A => "MODE SENSE(10)",
        0xBE => "READ CD",
        0x28 => "READ(10)",
        0x23 => "READ FORMAT CAPACITIES",
        0xAC => "GET PERFORMANCE",
        NativeWriteCommands.OpCodeCloseTrackSession => "CLOSE TRACK/SESSION",
        NativeWriteCommands.OpCodeWrite10 => "WRITE(10)",
        NativeWriteCommands.OpCodeModeSelect10 => "MODE SELECT(10)",
        NativeWriteCommands.OpCodeReadBufferCapacity => "READ BUFFER CAPACITY",
        NativeWriteCommands.OpCodeSendCueSheet => "SEND CUE SHEET",
        _ => $"OPCODE 0x{opCode:X2}"
    };

    private static string Hex(ReadOnlySpan<byte> data, int max)
    {
        int n = Math.Min(data.Length, max);
        string hex = Convert.ToHexString(data.Slice(0, n));
        var sb = new StringBuilder(hex.Length + hex.Length / 2);
        for (int i = 0; i < hex.Length; i += 2)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(hex, i, 2);
        }
        if (data.Length > max) sb.Append($" … ({data.Length} bytes)");
        return sb.ToString();
    }
}
