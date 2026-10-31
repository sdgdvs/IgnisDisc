using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace CdBurner.Core.Audio;

public sealed record MusicBrainzTrack(int Position, string Title, string Artist, TimeSpan? Length, string? Isrc);

public sealed record MusicBrainzRelease(
    string ReleaseId, string Title, string Artist, string? Date, string? Country, string? Barcode,
    IReadOnlyList<MusicBrainzTrack> Tracks);

public sealed class MusicBrainzClient
{
    public const string DefaultBaseUrl = "https://musicbrainz.org/ws/2/";

    private readonly HttpClient _httpClient;
    private readonly string _userAgent;
    private readonly string _baseUrl;

    public MusicBrainzClient(HttpClient httpClient, string userAgent, string baseUrl = DefaultBaseUrl)
    {
        ArgumentNullException.ThrowIfNull(httpClient);

        if (string.IsNullOrWhiteSpace(userAgent))
        {
            throw new ArgumentException("User-Agent cannot be null or whitespace.", nameof(userAgent));
        }

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new ArgumentException("Base URL cannot be null or whitespace.", nameof(baseUrl));
        }

        _httpClient = httpClient;
        _userAgent = userAgent;
        _baseUrl = baseUrl.TrimEnd('/') + "/";
    }

    public async Task<IReadOnlyList<MusicBrainzRelease>> LookupDiscIdAsync(string musicBrainzDiscId, CancellationToken cancellationToken = default)
    {
        if (!IsValidDiscId(musicBrainzDiscId))
        {
            throw new ArgumentException("MusicBrainz disc ID must be exactly 28 characters consisting of [A-Za-z0-9._-].", nameof(musicBrainzDiscId));
        }

        string url = $"{_baseUrl}discid/{Uri.EscapeDataString(musicBrainzDiscId)}?inc=recordings+artist-credits+isrcs&fmt=json";

        HttpResponseMessage response = await SendRequestAsync(url, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
        {
            response.Dispose();
            await Task.Delay(1100, cancellationToken).ConfigureAwait(false);
            response = await SendRequestAsync(url, cancellationToken).ConfigureAwait(false);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return Array.Empty<MusicBrainzRelease>();
            }

            if (!response.IsSuccessStatusCode)
            {
                int statusCode = (int)response.StatusCode;
                throw new HttpRequestException(
                    $"MusicBrainz request failed with HTTP status code {statusCode}.",
                    null,
                    response.StatusCode);
            }

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

            return ParseReleases(doc.RootElement, musicBrainzDiscId);
        }
    }

    public static CdTextAlbumMetadata ToAlbumMetadata(MusicBrainzRelease release, string musicBrainzDiscId)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(musicBrainzDiscId);

        var tracks = release.Tracks.Select(t => new CdTextTrackMetadata(
            TrackNumber: t.Position,
            Title: t.Title,
            Performer: t.Artist,
            Isrc: t.Isrc ?? string.Empty
        )).ToList();

        return new CdTextAlbumMetadata(
            AlbumTitle: release.Title,
            AlbumPerformer: release.Artist,
            DiscId: musicBrainzDiscId,
            UpcEan: release.Barcode ?? string.Empty,
            Tracks: tracks
        );
    }

    private static bool IsValidDiscId(string? discId)
    {
        if (discId is null || discId.Length != 28)
        {
            return false;
        }

        foreach (char c in discId)
        {
            bool isValid = (c >= 'A' && c <= 'Z')
                || (c >= 'a' && c <= 'z')
                || (c >= '0' && c <= '9')
                || c == '.' || c == '_' || c == '-';

            if (!isValid)
            {
                return false;
            }
        }

        return true;
    }

    private async Task<HttpResponseMessage> SendRequestAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static IReadOnlyList<MusicBrainzRelease> ParseReleases(JsonElement root, string targetDiscId)
    {
        if (!root.TryGetProperty("releases", out var releasesEl) || releasesEl.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<MusicBrainzRelease>();
        }

        var releases = new List<MusicBrainzRelease>();

        foreach (var releaseEl in releasesEl.EnumerateArray())
        {
            if (releaseEl.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string releaseId = GetRequiredString(releaseEl, "id");
            string title = GetRequiredString(releaseEl, "title");
            string artist = ParseArtistCredit(releaseEl);
            string? date = GetNullableString(releaseEl, "date");
            string? country = GetNullableString(releaseEl, "country");
            string? barcode = GetNullableString(releaseEl, "barcode");

            IReadOnlyList<MusicBrainzTrack> tracks = ParseTracks(releaseEl, targetDiscId, artist);

            releases.Add(new MusicBrainzRelease(releaseId, title, artist, date, country, barcode, tracks));
        }

        return releases;
    }

    private static IReadOnlyList<MusicBrainzTrack> ParseTracks(JsonElement releaseEl, string targetDiscId, string releaseArtist)
    {
        if (!releaseEl.TryGetProperty("media", out var mediaEl) || mediaEl.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<MusicBrainzTrack>();
        }

        JsonElement? chosenMedium = null;
        JsonElement? firstMedium = null;

        foreach (var medium in mediaEl.EnumerateArray())
        {
            if (medium.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (firstMedium == null)
            {
                firstMedium = medium;
            }

            if (medium.TryGetProperty("discs", out var discsEl) && discsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var disc in discsEl.EnumerateArray())
                {
                    if (disc.ValueKind == JsonValueKind.Object &&
                        disc.TryGetProperty("id", out var idEl) &&
                        idEl.ValueKind == JsonValueKind.String &&
                        string.Equals(idEl.GetString(), targetDiscId, StringComparison.Ordinal))
                    {
                        chosenMedium = medium;
                        break;
                    }
                }
            }

            if (chosenMedium != null)
            {
                break;
            }
        }

        chosenMedium ??= firstMedium;

        if (chosenMedium == null)
        {
            return Array.Empty<MusicBrainzTrack>();
        }

        if (!chosenMedium.Value.TryGetProperty("tracks", out var tracksEl) || tracksEl.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<MusicBrainzTrack>();
        }

        var tracks = new List<MusicBrainzTrack>();

        foreach (var trackEl in tracksEl.EnumerateArray())
        {
            if (trackEl.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            int position = 0;
            if (trackEl.TryGetProperty("position", out var posEl) && posEl.ValueKind == JsonValueKind.Number)
            {
                posEl.TryGetInt32(out position);
            }

            string trackTitle = GetRequiredString(trackEl, "title");

            string trackArtist = ParseArtistCredit(trackEl);
            if (string.IsNullOrEmpty(trackArtist))
            {
                trackArtist = releaseArtist;
            }

            TimeSpan? length = null;
            if (trackEl.TryGetProperty("length", out var lenEl) && lenEl.ValueKind == JsonValueKind.Number)
            {
                if (lenEl.TryGetInt64(out long ms))
                {
                    length = TimeSpan.FromMilliseconds(ms);
                }
            }

            string? isrc = null;
            if (trackEl.TryGetProperty("recording", out var recEl) && recEl.ValueKind == JsonValueKind.Object)
            {
                if (recEl.TryGetProperty("isrcs", out var isrcsEl) && isrcsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var isrcItem in isrcsEl.EnumerateArray())
                    {
                        if (isrcItem.ValueKind == JsonValueKind.String)
                        {
                            isrc = isrcItem.GetString();
                            break;
                        }
                    }
                }
            }

            tracks.Add(new MusicBrainzTrack(position, trackTitle, trackArtist, length, isrc));
        }

        return tracks;
    }

    private static string ParseArtistCredit(JsonElement element)
    {
        if (!element.TryGetProperty("artist-credit", out var credits) || credits.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();

        foreach (var credit in credits.EnumerateArray())
        {
            if (credit.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string name = string.Empty;
            if (credit.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
            {
                name = nameEl.GetString() ?? string.Empty;
            }

            string joinphrase = string.Empty;
            if (credit.TryGetProperty("joinphrase", out var joinEl) && joinEl.ValueKind == JsonValueKind.String)
            {
                joinphrase = joinEl.GetString() ?? string.Empty;
            }

            sb.Append(name);
            sb.Append(joinphrase);
        }

        return sb.ToString();
    }

    private static string GetRequiredString(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            return prop.GetString() ?? string.Empty;
        }

        return string.Empty;
    }

    private static string? GetNullableString(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.String)
            {
                return prop.GetString();
            }
        }

        return null;
    }
}
