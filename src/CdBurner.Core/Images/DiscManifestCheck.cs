using System.Security.Cryptography;
using System.Text;
using CdBurner.Core.Iso;
using CdBurner.Core.Primitives;
using CdBurner.Core.Utilities;

namespace CdBurner.Core.Images;

/// <summary>Outcome of checking a disc's files against the <c>SHA256SUMS</c>/<c>BLAKE3SUMS</c> in its root.</summary>
public sealed class ManifestCheckResult
{
    /// <summary>The hash lists that were found and used, e.g. "SHA256SUMS".</summary>
    public IReadOnlyList<string> ManifestNames { get; init; } = Array.Empty<string>();

    /// <summary>Files whose bytes on the disc were hashed and compared.</summary>
    public int FilesChecked { get; init; }

    /// <summary>Listed files whose bytes on the disc do not match the list, or could not be read.</summary>
    public IReadOnlyList<string> MismatchedFiles { get; init; } = Array.Empty<string>();

    /// <summary>Listed files that are not on the disc.</summary>
    public IReadOnlyList<string> MissingFiles { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Listed files that could not be looked up because their name exists only in the disc's UDF file system (for
    /// example a name longer than Joliet allows). They are covered by the sector compare, not by this check.
    /// </summary>
    public int FilesNotLookedUp { get; init; }

    public bool Passed => MismatchedFiles.Count == 0 && MissingFiles.Count == 0;
}

/// <summary>
/// Checks every file listed in a disc's root <c>SHA256SUMS</c> and <c>BLAKE3SUMS</c> (roadmap M1.3) while the disc is
/// read once, front to back. The file system is parsed from the disc's own sectors by an <see cref="IsoImageReader"/>
/// (no drive letter needed); then each sector passed to <see cref="AddSector"/> goes into the hash of the file that
/// owns it.
/// </summary>
public sealed class DiscManifestCheck
{
    private sealed class FileState
    {
        public required string Path { get; init; }
        public required int StartLba { get; init; }
        public long Remaining;
        public bool Unreadable;
        public bool Done;
        public readonly List<(string Expected, Func<byte[]> Finish, Action<ReadOnlySpan<byte>> Append)> Hashes = new();
    }

    private static readonly System.Buffers.SearchValues<char> HexDigits =
        System.Buffers.SearchValues.Create("0123456789abcdefABCDEF");

    private readonly Dictionary<int, List<FileState>> _byStartLba = new();
    private readonly List<FileState> _all = new();
    private readonly List<FileState> _active = new();
    private readonly List<string> _missing;
    private readonly int _notLookedUp;
    private int? _nextLba;

    /// <summary>The hash lists in use.</summary>
    public IReadOnlyList<string> ManifestNames { get; }

    private DiscManifestCheck(IReadOnlyList<string> names, List<string> missing, int notLookedUp)
    {
        ManifestNames = names;
        _missing = missing;
        _notLookedUp = notLookedUp;
    }

    /// <summary>
    /// Reads the hash lists from the disc's root. Returns null when the disc has none.
    /// </summary>
    /// <exception cref="InvalidDataException">A hash list is present but is not in the GNU coreutils format.</exception>
    public static DiscManifestCheck? Prepare(IsoImageReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        var lists = new List<(string Name, IsoFileEntry Entry)>();
        foreach (string name in new[] { IsoUdfImageBuilder.Sha256ManifestFileName, IsoUdfImageBuilder.Blake3ManifestFileName })
        {
            // ISO 9660-only discs show 8.3 names ("SHA256SU"); the list is still the right file.
            var entry = reader.FindFile("/" + name)
                ?? reader.RootDirectory.Files.FirstOrDefault(f => !reader.HasJoliet &&
                    f.Name.Equals(name[..8], StringComparison.OrdinalIgnoreCase));
            if (entry != null)
            {
                lists.Add((name, entry));
            }
        }
        if (lists.Count == 0)
        {
            return null;
        }

        var states = new Dictionary<string, FileState>(StringComparer.Ordinal);
        var missing = new List<string>();
        int notLookedUp = 0;
        var listEntries = new HashSet<IsoFileEntry>(lists.Select(l => l.Entry), ReferenceEqualityComparer.Instance);

        foreach (var (name, entry) in lists)
        {
            bool blake3 = name == IsoUdfImageBuilder.Blake3ManifestFileName;
            foreach (var (hash, path) in ParseList(reader.ReadFileBytes(entry), name))
            {
                if (!states.TryGetValue(path, out var state))
                {
                    var file = reader.FindFile("/" + path);
                    if (file == null || listEntries.Contains(file))
                    {
                        // A name too long for Joliet exists only in UDF, which this reader does not walk.
                        if (reader.HasUdf && file == null)
                        {
                            notLookedUp++;
                        }
                        else if (!missing.Contains(path))
                        {
                            missing.Add(path);
                        }
                        continue;
                    }
                    state = new FileState { Path = path, StartLba = file.StartLba, Remaining = file.SizeBytes };
                    states.Add(path, state);
                }

                if (blake3)
                {
                    var hasher = new Blake3Hasher();
                    state.Hashes.Add((hash, hasher.GetHashAndReset, data => hasher.Append(data)));
                }
                else
                {
                    var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    state.Hashes.Add((hash, () => { byte[] h = hasher.GetHashAndReset(); hasher.Dispose(); return h; },
                        data => hasher.AppendData(data)));
                }
            }
        }

        var check = new DiscManifestCheck(lists.Select(l => l.Name).ToList(), missing, notLookedUp);
        foreach (var state in states.Values)
        {
            check._all.Add(state);
            if (state.Remaining == 0)
            {
                state.Done = true; // empty file: its hash is the hash of no bytes
                continue;
            }
            if (!check._byStartLba.TryGetValue(state.StartLba, out var list))
            {
                check._byStartLba[state.StartLba] = list = new List<FileState>();
            }
            list.Add(state);
        }
        return check;
    }

    /// <summary>Feeds the next sector of the disc. Sectors must arrive in LBA order without gaps.</summary>
    public void AddSector(int lba, ReadOnlySpan<byte> sector)
    {
        Advance(lba);
        foreach (var file in _active)
        {
            int take = (int)Math.Min(file.Remaining, Math.Min(sector.Length, OpticalConstants.DataSectorSize));
            if (!file.Unreadable)
            {
                foreach (var hash in file.Hashes)
                {
                    hash.Append(sector[..take]);
                }
            }
            file.Remaining -= take;
        }
        Retire();
    }

    /// <summary>The sector at <paramref name="lba"/> could not be read: any file that owns it fails.</summary>
    public void MarkUnreadable(int lba)
    {
        Advance(lba);
        foreach (var file in _active)
        {
            file.Unreadable = true;
            file.Remaining -= Math.Min(file.Remaining, OpticalConstants.DataSectorSize);
        }
        Retire();
    }

    private void Advance(int lba)
    {
        if (_nextLba is int expected && lba != expected)
        {
            // A gap: whatever was being read is incomplete.
            foreach (var file in _active)
            {
                file.Unreadable = true;
                file.Remaining = 0;
            }
            Retire();
        }
        _nextLba = lba + 1;
        if (_byStartLba.Remove(lba, out var starting))
        {
            _active.AddRange(starting);
        }
    }

    private void Retire()
    {
        for (int i = _active.Count - 1; i >= 0; i--)
        {
            if (_active[i].Remaining == 0)
            {
                _active[i].Done = true;
                _active.RemoveAt(i);
            }
        }
    }

    /// <summary>Compares the hashes. Files whose sectors were never (fully) read count as mismatched.</summary>
    public ManifestCheckResult Finish()
    {
        var mismatched = new List<string>();
        int checkedCount = 0;
        foreach (var file in _all)
        {
            bool ok = file.Done && !file.Unreadable;
            foreach (var hash in file.Hashes)
            {
                string actual = Convert.ToHexString(hash.Finish()).ToLowerInvariant();
                ok &= actual == hash.Expected;
            }
            checkedCount++;
            if (!ok)
            {
                mismatched.Add("/" + file.Path);
            }
        }

        return new ManifestCheckResult
        {
            ManifestNames = ManifestNames,
            FilesChecked = checkedCount,
            MismatchedFiles = mismatched,
            MissingFiles = _missing.Select(p => "/" + p).ToList(),
            FilesNotLookedUp = _notLookedUp
        };
    }

    /// <summary>Parses GNU coreutils <c>sha256sum</c>/<c>b3sum</c> text-mode lines, including escaped names.</summary>
    internal static List<(string Hash, string Path)> ParseList(byte[] bytes, string listName)
    {
        var result = new List<(string, string)>();
        string text = Encoding.UTF8.GetString(bytes);
        int lineNumber = 0;
        foreach (string raw in text.Split('\n'))
        {
            lineNumber++;
            if (raw.Length == 0)
            {
                continue;
            }
            bool escaped = raw[0] == '\\';
            string line = escaped ? raw[1..] : raw;
            if (line.Length < 67 || line[64] != ' ' || (line[65] != ' ' && line[65] != '*') ||
                line.AsSpan(0, 64).ContainsAnyExcept(HexDigits))
            {
                throw new InvalidDataException($"{listName} line {lineNumber} is not a hash list line.");
            }
            string path = line[66..];
            if (escaped)
            {
                var sb = new StringBuilder(path.Length);
                for (int i = 0; i < path.Length; i++)
                {
                    if (path[i] == '\\' && i + 1 < path.Length)
                    {
                        i++;
                        sb.Append(path[i] == 'n' ? '\n' : path[i]);
                    }
                    else
                    {
                        sb.Append(path[i]);
                    }
                }
                path = sb.ToString();
            }
            result.Add((line[..64].ToLowerInvariant(), path));
        }
        return result;
    }
}
