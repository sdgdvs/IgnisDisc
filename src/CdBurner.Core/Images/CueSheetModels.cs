using System.Diagnostics.CodeAnalysis;
using CdBurner.Core.Primitives;

namespace CdBurner.Core.Images;

/// <summary>
/// Data type of a track defined in a CDRWIN / Red Book CUE sheet.
/// </summary>
public enum CueTrackDataType
{
    Audio,
    Mode1_2048,
    Mode1_2352,
    Mode2_2336,
    Mode2_2352
}

/// <summary>
/// Represents an INDEX statement within a CUE sheet track (e.g. INDEX 01 00:02:00).
/// </summary>
public record CueIndex(int IndexNumber, MsfAddress Position);

/// <summary>
/// Represents a TRACK within a CUE sheet file.
/// </summary>
public record CueTrack(
    int TrackNumber,
    CueTrackDataType DataType,
    string? Title = null,
    string? Performer = null,
    string? Songwriter = null,
    string? Isrc = null,
    MsfAddress? PreGap = null,
    MsfAddress? PostGap = null,
    IReadOnlyList<string>? Flags = null,
    IReadOnlyList<CueIndex>? Indices = null)
{
    public IReadOnlyList<string> Flags { get; init; } = Flags ?? Array.Empty<string>();
    public IReadOnlyList<CueIndex> Indices { get; init; } = Indices ?? Array.Empty<CueIndex>();

    public virtual bool Equals(CueTrack? other)
    {
        if (ReferenceEquals(null, other)) return false;
        if (ReferenceEquals(this, other)) return true;
        return TrackNumber == other.TrackNumber
            && DataType == other.DataType
            && string.Equals(Title, other.Title, StringComparison.Ordinal)
            && string.Equals(Performer, other.Performer, StringComparison.Ordinal)
            && string.Equals(Songwriter, other.Songwriter, StringComparison.Ordinal)
            && string.Equals(Isrc, other.Isrc, StringComparison.Ordinal)
            && Nullable.Equals(PreGap, other.PreGap)
            && Nullable.Equals(PostGap, other.PostGap)
            && Flags.SequenceEqual(other.Flags)
            && Indices.SequenceEqual(other.Indices);
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(TrackNumber);
        hash.Add(DataType);
        hash.Add(Title);
        hash.Add(Performer);
        hash.Add(Songwriter);
        hash.Add(Isrc);
        hash.Add(PreGap);
        hash.Add(PostGap);
        foreach (var flag in Flags) hash.Add(flag);
        foreach (var index in Indices) hash.Add(index);
        return hash.ToHashCode();
    }
}

/// <summary>
/// Represents a FILE statement and its child tracks in a CUE sheet.
/// </summary>
public record CueFileEntry(
    string FileName,
    string FileType,
    IReadOnlyList<CueTrack>? Tracks = null)
{
    public IReadOnlyList<CueTrack> Tracks { get; init; } = Tracks ?? Array.Empty<CueTrack>();

    public virtual bool Equals(CueFileEntry? other)
    {
        if (ReferenceEquals(null, other)) return false;
        if (ReferenceEquals(this, other)) return true;
        return string.Equals(FileName, other.FileName, StringComparison.Ordinal)
            && string.Equals(FileType, other.FileType, StringComparison.OrdinalIgnoreCase)
            && Tracks.SequenceEqual(other.Tracks);
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(FileName);
        hash.Add(FileType, StringComparer.OrdinalIgnoreCase);
        foreach (var track in Tracks) hash.Add(track);
        return hash.ToHashCode();
    }
}

/// <summary>
/// Represents an entire parsed CDRWIN / Red Book CUE sheet.
/// </summary>
public record CueSheet(
    string? Catalog = null,
    string? Title = null,
    string? Performer = null,
    string? Songwriter = null,
    IReadOnlyList<CueFileEntry>? Files = null,
    IReadOnlyDictionary<string, string>? Remarks = null)
{
    public IReadOnlyList<CueFileEntry> Files { get; init; } = Files ?? Array.Empty<CueFileEntry>();
    public IReadOnlyDictionary<string, string> Remarks { get; init; } = Remarks ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public virtual bool Equals(CueSheet? other)
    {
        if (ReferenceEquals(null, other)) return false;
        if (ReferenceEquals(this, other)) return true;
        return string.Equals(Catalog, other.Catalog, StringComparison.Ordinal)
            && string.Equals(Title, other.Title, StringComparison.Ordinal)
            && string.Equals(Performer, other.Performer, StringComparison.Ordinal)
            && string.Equals(Songwriter, other.Songwriter, StringComparison.Ordinal)
            && Files.SequenceEqual(other.Files)
            && DictionariesEqual(Remarks, other.Remarks);
    }

    private static bool DictionariesEqual(IReadOnlyDictionary<string, string> d1, IReadOnlyDictionary<string, string> d2)
    {
        if (d1.Count != d2.Count) return false;
        foreach (var kvp in d1)
        {
            if (!d2.TryGetValue(kvp.Key, out var val) || val != kvp.Value)
                return false;
        }
        return true;
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Catalog);
        hash.Add(Title);
        hash.Add(Performer);
        hash.Add(Songwriter);
        foreach (var file in Files) hash.Add(file);
        foreach (var kvp in Remarks)
        {
            hash.Add(kvp.Key, StringComparer.OrdinalIgnoreCase);
            hash.Add(kvp.Value);
        }
        return hash.ToHashCode();
    }
}
