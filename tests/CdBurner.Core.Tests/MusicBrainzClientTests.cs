using System.Net;
using System.Text;
using CdBurner.Core.Audio;
using Xunit;

namespace CdBurner.Core.Tests;

public class MusicBrainzClientTests
{
    private const string ValidDiscId = "arIS30RPWowvwNEqsqdDnZzDGhk-";

    [Fact]
    public async Task LookupDiscIdAsync_ParsesReleaseMediumAndTracks()
    {
        const string json = """
        {
          "id": "arIS30RPWowvwNEqsqdDnZzDGhk-",
          "releases": [
            {
              "id": "rel-uuid-001",
              "title": "Album Title",
              "date": "2001-05-15",
              "country": "GB",
              "barcode": "5012345678901",
              "artist-credit": [
                { "name": "Artist One", "joinphrase": " & " },
                { "name": "Artist Two", "joinphrase": "" }
              ],
              "media": [
                {
                  "position": 1,
                  "discs": [
                    { "id": "wrongDiscId0000000000000000" }
                  ],
                  "tracks": [
                    {
                      "position": 1,
                      "title": "Wrong Medium Track",
                      "length": 99999
                    }
                  ]
                },
                {
                  "position": 2,
                  "discs": [
                    { "id": "arIS30RPWowvwNEqsqdDnZzDGhk-" }
                  ],
                  "tracks": [
                    {
                      "position": 1,
                      "title": "First Song",
                      "length": 215000,
                      "artist-credit": [
                        { "name": "Solo Singer", "joinphrase": " feat. " },
                        { "name": "Guest Star", "joinphrase": "" }
                      ],
                      "recording": {
                        "isrcs": [ "GBAYE0100001", "GBAYE0100002" ]
                      }
                    },
                    {
                      "position": 2,
                      "title": "Second Song",
                      "artist-credit": [
                        { "name": "Different Artist", "joinphrase": "" }
                      ],
                      "recording": {
                        "isrcs": [ "GBAYE0100003" ]
                      }
                    },
                    {
                      "position": 3,
                      "title": "Third Song",
                      "length": 180000
                    }
                  ]
                }
              ]
            }
          ]
        }
        """;

        var stub = new StubHandler();
        stub.EnqueueJsonResponse(HttpStatusCode.OK, json);
        using var httpClient = new HttpClient(stub);
        var client = new MusicBrainzClient(httpClient, "TestApp/1.0");

        var releases = await client.LookupDiscIdAsync(ValidDiscId);

        Assert.NotNull(releases);
        Assert.Single(releases);

        var release = releases[0];
        Assert.Equal("rel-uuid-001", release.ReleaseId);
        Assert.Equal("Album Title", release.Title);
        Assert.Equal("Artist One & Artist Two", release.Artist);
        Assert.Equal("2001-05-15", release.Date);
        Assert.Equal("GB", release.Country);
        Assert.Equal("5012345678901", release.Barcode);

        Assert.Equal(3, release.Tracks.Count);

        // Track 1: mixed artist credit, length present, first isrc selected
        var t1 = release.Tracks[0];
        Assert.Equal(1, t1.Position);
        Assert.Equal("First Song", t1.Title);
        Assert.Equal("Solo Singer feat. Guest Star", t1.Artist);
        Assert.Equal(TimeSpan.FromMilliseconds(215000), t1.Length);
        Assert.Equal("GBAYE0100001", t1.Isrc);

        // Track 2: missing length, artist credit present, isrc present
        var t2 = release.Tracks[1];
        Assert.Equal(2, t2.Position);
        Assert.Equal("Second Song", t2.Title);
        Assert.Equal("Different Artist", t2.Artist);
        Assert.Null(t2.Length);
        Assert.Equal("GBAYE0100003", t2.Isrc);

        // Track 3: missing recording (null Isrc), missing artist credit (falls back to release artist)
        var t3 = release.Tracks[2];
        Assert.Equal(3, t3.Position);
        Assert.Equal("Third Song", t3.Title);
        Assert.Equal("Artist One & Artist Two", t3.Artist);
        Assert.Equal(TimeSpan.FromMilliseconds(180000), t3.Length);
        Assert.Null(t3.Isrc);
    }

    [Fact]
    public async Task LookupDiscIdAsync_SendsUserAgentAndExpectedUrl()
    {
        const string userAgent = "TestSuiteApp/2.4.1 ( test@example.com )";
        var stub = new StubHandler();
        stub.EnqueueJsonResponse(HttpStatusCode.OK, """{ "id": "arIS30RPWowvwNEqsqdDnZzDGhk-", "releases": [] }""");
        using var httpClient = new HttpClient(stub);
        var client = new MusicBrainzClient(httpClient, userAgent);

        await client.LookupDiscIdAsync(ValidDiscId);

        Assert.Single(stub.Requests);
        var request = stub.Requests[0];
        Assert.Equal(
            $"https://musicbrainz.org/ws/2/discid/{ValidDiscId}?inc=recordings+artist-credits+isrcs&fmt=json",
            request.RequestUri?.ToString());
        Assert.Equal(userAgent, request.Headers.UserAgent.ToString());
        Assert.Equal("application/json", request.Headers.Accept.ToString());
    }

    [Fact]
    public async Task LookupDiscIdAsync_NotFound_ReturnsEmpty()
    {
        var stub = new StubHandler();
        stub.EnqueueJsonResponse(HttpStatusCode.NotFound, """{ "error": "Not Found" }""");
        using var httpClient = new HttpClient(stub);
        var client = new MusicBrainzClient(httpClient, "TestApp/1.0");

        var result = await client.LookupDiscIdAsync(ValidDiscId);

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public async Task LookupDiscIdAsync_ServiceUnavailable_RetriesOnceThenSucceeds()
    {
        var stub = new StubHandler();
        stub.EnqueueJsonResponse(HttpStatusCode.ServiceUnavailable, "Service Unavailable");
        stub.EnqueueJsonResponse(HttpStatusCode.OK, """{ "id": "arIS30RPWowvwNEqsqdDnZzDGhk-", "releases": [] }""");
        using var httpClient = new HttpClient(stub);
        var client = new MusicBrainzClient(httpClient, "TestApp/1.0");

        var result = await client.LookupDiscIdAsync(ValidDiscId);

        Assert.NotNull(result);
        Assert.Empty(result);
        Assert.Equal(2, stub.Requests.Count);
    }

    [Fact]
    public async Task LookupDiscIdAsync_ServerError_Throws()
    {
        var stub = new StubHandler();
        stub.EnqueueJsonResponse(HttpStatusCode.InternalServerError, "Server Error");
        using var httpClient = new HttpClient(stub);
        var client = new MusicBrainzClient(httpClient, "TestApp/1.0");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.LookupDiscIdAsync(ValidDiscId));

        Assert.Contains("500", ex.Message);
        Assert.Single(stub.Requests);
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("arIS30RPWowvwNEqsqdDnZzDGhk/")]
    [InlineData("arIS30RPWowvwNEqsqdDnZzDGhk-1")]
    public async Task LookupDiscIdAsync_InvalidDiscId_Throws(string invalidId)
    {
        var stub = new StubHandler();
        using var httpClient = new HttpClient(stub);
        var client = new MusicBrainzClient(httpClient, "TestApp/1.0");

        await Assert.ThrowsAsync<ArgumentException>(() => client.LookupDiscIdAsync(invalidId));
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public void ToAlbumMetadata_MapsReleaseAndTracks()
    {
        var tracks = new List<MusicBrainzTrack>
        {
            new(Position: 1, Title: "Song One", Artist: "Artist One", Length: TimeSpan.FromMinutes(3), Isrc: "USABC0100001"),
            new(Position: 2, Title: "Song Two", Artist: "Artist Two", Length: null, Isrc: null)
        };

        var release = new MusicBrainzRelease(
            ReleaseId: "rel-12345",
            Title: "Album Name",
            Artist: "Album Performer",
            Date: "2022-01-01",
            Country: "US",
            Barcode: "012345678901",
            Tracks: tracks);

        var metadata = MusicBrainzClient.ToAlbumMetadata(release, ValidDiscId);

        Assert.NotNull(metadata);
        Assert.Equal("Album Name", metadata.AlbumTitle);
        Assert.Equal("Album Performer", metadata.AlbumPerformer);
        Assert.Equal(ValidDiscId, metadata.DiscId);
        Assert.Equal("012345678901", metadata.UpcEan);
        Assert.NotNull(metadata.Tracks);
        Assert.Equal(2, metadata.Tracks.Count);

        Assert.Equal(1, metadata.Tracks[0].TrackNumber);
        Assert.Equal("Song One", metadata.Tracks[0].Title);
        Assert.Equal("Artist One", metadata.Tracks[0].Performer);
        Assert.Equal("USABC0100001", metadata.Tracks[0].Isrc);

        Assert.Equal(2, metadata.Tracks[1].TrackNumber);
        Assert.Equal("Song Two", metadata.Tracks[1].Title);
        Assert.Equal("Artist Two", metadata.Tracks[1].Performer);
        Assert.Equal(string.Empty, metadata.Tracks[1].Isrc);

        // Also assert when Barcode is null, UpcEan maps to empty string
        var releaseNoBarcode = new MusicBrainzRelease(
            ReleaseId: "rel-null-barcode",
            Title: "No Barcode Album",
            Artist: "Artist",
            Date: null,
            Country: null,
            Barcode: null,
            Tracks: Array.Empty<MusicBrainzTrack>());

        var metadataNoBarcode = MusicBrainzClient.ToAlbumMetadata(releaseNoBarcode, ValidDiscId);
        Assert.Equal(string.Empty, metadataNoBarcode.UpcEan);
    }

    [Fact]
    public async Task Constructor_ValidatesArgumentsAndNormalizesBaseUrl()
    {
        var stub = new StubHandler();
        using var httpClient = new HttpClient(stub);

        Assert.Throws<ArgumentNullException>(() => new MusicBrainzClient(null!, "TestAgent/1.0"));
        Assert.Throws<ArgumentException>(() => new MusicBrainzClient(httpClient, ""));
        Assert.Throws<ArgumentException>(() => new MusicBrainzClient(httpClient, "   "));
        Assert.Throws<ArgumentException>(() => new MusicBrainzClient(httpClient, "TestAgent/1.0", ""));

        // Base URL normalized with single trailing slash
        var clientWithoutSlash = new MusicBrainzClient(httpClient, "TestAgent/1.0", "https://example.com/ws/2");
        stub.EnqueueJsonResponse(HttpStatusCode.OK, """{ "releases": [] }""");
        await clientWithoutSlash.LookupDiscIdAsync(ValidDiscId);
        Assert.StartsWith("https://example.com/ws/2/discid/", stub.Requests[0].RequestUri?.ToString());
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new();
        public List<HttpRequestMessage> Requests { get; } = new();

        public void EnqueueResponse(HttpResponseMessage response) => _responses.Enqueue(response);

        public void EnqueueJsonResponse(HttpStatusCode statusCode, string json)
        {
            var response = new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            _responses.Enqueue(response);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("No queued response available in StubHandler.");
            }

            return Task.FromResult(_responses.Dequeue());
        }
    }
}
