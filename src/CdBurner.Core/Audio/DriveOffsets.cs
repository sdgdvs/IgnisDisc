using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace CdBurner.Core.Audio;

/// <summary>One drive in AccurateRip's list of read offsets.</summary>
/// <param name="Name">"VENDOR - PRODUCT" as the list writes it, e.g. "HL-DT-ST - DVDRAM GP60NB50".</param>
/// <param name="ReadOffsetSamples">Samples a ripper reads further on to get the true audio (+6 for the GP60NB50).</param>
/// <param name="Submissions">How many users submitted this offset (the list caps it at 255).</param>
public sealed record DriveOffsetEntry(string Name, int ReadOffsetSamples, int Submissions);

/// <summary>
/// AccurateRip's drive read-offset list (<c>DriveOffsets.bin</c>), downloaded when needed and cached; never bundled.
/// Layout, read from the file itself (339,204 bytes on 2026-11-11, exactly 4,916 records): 69-byte records, a
/// little-endian 16-bit read offset, the drive name in 33 bytes padded with zeros, a 32-bit submission count, then 30
/// bytes this code does not use.
/// </summary>
public static class AccurateRipDriveOffsets
{
    public const string Url = "http://www.accuraterip.com/accuraterip/DriveOffsets.bin";
    public const int RecordSize = 69;
    private const int NameSize = 33;

    /// <summary>A cached copy older than this is downloaded again (when the network allows).</summary>
    public static readonly TimeSpan MaxCacheAge = TimeSpan.FromDays(30);

    /// <summary>Parses the list. Records with no name are skipped; a trailing partial record is ignored.</summary>
    public static IReadOnlyList<DriveOffsetEntry> Parse(ReadOnlySpan<byte> data)
    {
        var entries = new List<DriveOffsetEntry>(data.Length / RecordSize);
        for (int offset = 0; offset + RecordSize <= data.Length; offset += RecordSize)
        {
            ReadOnlySpan<byte> record = data.Slice(offset, RecordSize);
            ReadOnlySpan<byte> nameBytes = record.Slice(2, NameSize);
            int end = nameBytes.IndexOf((byte)0);
            string name = Encoding.ASCII.GetString(end < 0 ? nameBytes : nameBytes[..end]).Trim();
            if (name.Length == 0)
            {
                continue;
            }
            entries.Add(new DriveOffsetEntry(
                name,
                BinaryPrimitives.ReadInt16LittleEndian(record),
                BinaryPrimitives.ReadInt32LittleEndian(record.Slice(2 + NameSize, 4))));
        }
        return entries;
    }

    /// <summary>The list's name for a drive: its INQUIRY vendor and product, trimmed, joined by " - ".</summary>
    public static string DriveName(string? vendor, string? product) =>
        $"{(vendor ?? string.Empty).Trim()} - {(product ?? string.Empty).Trim()}";

    /// <summary>The entry for a drive, matched on vendor and product (case and inner spacing ignored), or null.</summary>
    public static DriveOffsetEntry? Find(IEnumerable<DriveOffsetEntry> entries, string? vendor, string? product)
    {
        ArgumentNullException.ThrowIfNull(entries);
        string wanted = Normalize(DriveName(vendor, product));
        return entries.FirstOrDefault(e => Normalize(e.Name) == wanted);
    }

    private static string Normalize(string name) => string.Join(' ', name.ToUpperInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// The list from <paramref name="cachePath"/>, downloaded first when the copy is missing or older than
    /// <see cref="MaxCacheAge"/>. A failed download falls back to an older copy; with none, the list is empty. Only
    /// cancellation throws: an unknown read offset just means no write-offset correction.
    /// </summary>
    public static async Task<IReadOnlyList<DriveOffsetEntry>> LoadAsync(HttpClient? http, string cachePath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cachePath);
        bool fresh = File.Exists(cachePath) && DateTime.UtcNow - File.GetLastWriteTimeUtc(cachePath) < MaxCacheAge;
        if (!fresh && http != null)
        {
            try
            {
                byte[] body = await http.GetByteArrayAsync(Url, ct).ConfigureAwait(false);
                if (body.Length >= RecordSize && Parse(body).Count > 0)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                    string temp = cachePath + ".tmp";
                    await File.WriteAllBytesAsync(temp, body, ct).ConfigureAwait(false);
                    File.Move(temp, cachePath, overwrite: true);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException
                                           or TaskCanceledException && !ct.IsCancellationRequested)
            {
                // Offline or refused: use an older copy if there is one.
            }
        }

        try
        {
            return File.Exists(cachePath) ? Parse(await File.ReadAllBytesAsync(cachePath, ct).ConfigureAwait(false)) : [];
        }
        catch (IOException)
        {
            return [];
        }
    }
}

/// <summary>
/// The write offset of a drive and how it was arrived at. Read offset R: a ripper reads R samples further on to get
/// the true audio. Combined offset C: how many samples later the audio reads back than it was sent, as the read-back
/// check after a direct burn measures it (C = W + R). Write offset W = C − R: how many samples later the audio lands
/// on the disc than it was sent. Sending it −W samples later puts it where an offset-corrected rip expects it.
/// </summary>
public sealed record WriteOffsetPlan(int? ReadOffset, string? ReadOffsetSource, int? CombinedOffset, int? WriteOffset, string Explanation)
{
    /// <summary>Samples to move the audio later before it is sent (0 when the write offset is unknown).</summary>
    public int ShiftSamples => -(WriteOffset ?? 0);

    public static WriteOffsetPlan Resolve(int? readOffset, string? readOffsetSource, int? combinedOffset, int? explicitWriteOffset)
    {
        if (explicitWriteOffset is int given)
        {
            return new WriteOffsetPlan(readOffset, readOffsetSource, combinedOffset, given,
                $"Write offset {given:+0;-0;0} samples, as given.");
        }
        if (readOffset is null)
        {
            return new WriteOffsetPlan(null, null, combinedOffset, null,
                "The drive's read offset is not known, so its write offset cannot be worked out; the audio is written as it is.");
        }
        if (combinedOffset is null)
        {
            return new WriteOffsetPlan(readOffset, readOffsetSource, null, null,
                $"Read offset {readOffset:+0;-0;0} ({readOffsetSource}); this drive's combined offset is not measured yet, so this burn is not corrected. The read-back check measures it, and later burns on this drive are corrected.");
        }
        int write = combinedOffset.Value - readOffset.Value;
        return new WriteOffsetPlan(readOffset, readOffsetSource, combinedOffset, write,
            $"Write offset {write:+0;-0;0} samples (combined {combinedOffset:+0;-0;0} measured on earlier burns, read {readOffset:+0;-0;0} from {readOffsetSource}).");
    }
}

/// <summary>
/// The combined offset measured on this computer for each drive (JSON, one entry per drive name). Several burns may
/// run at once on different drives, so every update takes an exclusive lock file and replaces the file whole.
/// </summary>
public sealed class DriveOffsetStore
{
    private sealed record Entry(int CombinedOffsetSamples, int Measurements, DateTime LastMeasuredUtc);

    private readonly string _path;

    public DriveOffsetStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IgnisDisc", "drive-offsets.json");

    /// <summary>The last combined offset measured for the drive, or null.</summary>
    public int? GetCombinedOffset(string driveName) =>
        Load().TryGetValue(driveName, out var e) ? e.CombinedOffsetSamples : null;

    /// <summary>Records a measurement. Returns the value it replaced when that differs (worth a note), else null.</summary>
    public int? RecordCombinedOffset(string driveName, int samples)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(driveName);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        using var guard = AcquireLock();
        var all = Load();
        all.TryGetValue(driveName, out var old);
        all[driveName] = new Entry(samples, (old?.Measurements ?? 0) + 1, DateTime.UtcNow);
        string temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, _path, overwrite: true);
        return old != null && old.CombinedOffsetSamples != samples ? old.CombinedOffsetSamples : null;
    }

    private Dictionary<string, Entry> Load()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(_path)) ?? new()
                : new();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return new(); // a damaged file only means measuring again
        }
    }

    private FileStream AcquireLock()
    {
        string lockPath = _path + ".lock";
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            catch (IOException) when (attempt < 50)
            {
                Thread.Sleep(100);
            }
        }
    }
}
