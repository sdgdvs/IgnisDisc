using System.Net;
using System.Net.Http;
using System.Text;
using CdBurner.Core.Utilities;
using Xunit;

namespace CdBurner.Core.Tests;

public class GitHubUpdateCheckerTests
{
    private sealed class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_handler(request));
        }
    }

    #region CompareVersions Tests

    [Theory]
    [InlineData("1.10", "1.10", 0)]
    [InlineData("1.10", "v1.10", 0)]
    [InlineData("v1.10", "1.10", 0)]
    [InlineData("1.10.0", "v1.10", 0)]
    [InlineData("v1.10.0", "1.10.0.0", 0)]
    [InlineData("1.10", "v1.11", -1)]
    [InlineData("1.9", "v1.10", -1)]
    [InlineData("1.09", "v1.10", -1)]
    [InlineData("1.10", "v1.10.1", -1)]
    [InlineData("1.10", "v2.0", -1)]
    [InlineData("1.11", "v1.10", 1)]
    [InlineData("1.10", "v1.09", 1)]
    [InlineData("1.10", "v1.9", 1)]
    [InlineData("2.0", "v1.10", 1)]
    [InlineData("1.10-preview1", "v1.10", 0)]
    [InlineData("1.10+build.42", "v1.10", 0)]
    public void CompareVersions_EvaluatesCorrectly(string currentVersion, string latestTag, int expectedComparisonSign)
    {
        int result = GitHubUpdateChecker.CompareVersions(currentVersion, latestTag);
        if (expectedComparisonSign == 0)
        {
            Assert.Equal(0, result);
        }
        else if (expectedComparisonSign < 0)
        {
            Assert.True(result < 0, $"Expected {currentVersion} < {latestTag}, but got {result}");
        }
        else
        {
            Assert.True(result > 0, $"Expected {currentVersion} > {latestTag}, but got {result}");
        }
    }

    #endregion

    #region CheckForUpdateAsync Tests

    [Fact]
    public async Task CheckForUpdateAsync_WhenNewerReleaseExists_ReturnsUpdateAvailableWithReleaseDetails()
    {
        string fakeJson = """
        {
            "tag_name": "v1.11",
            "name": "IgnisDisc v1.11",
            "html_url": "https://github.com/tether-works/IgnisDisc/releases/tag/v1.11"
        }
        """;

        string? recordedUserAgent = null;
        var mockHandler = new MockHttpMessageHandler(req =>
        {
            recordedUserAgent = req.Headers.UserAgent.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(fakeJson, Encoding.UTF8, "application/json")
            };
        });

        using var client = new HttpClient(mockHandler);
        var result = await GitHubUpdateChecker.CheckForUpdateAsync(
            currentVersion: "1.10",
            httpClient: client);

        Assert.True(result.Success);
        Assert.True(result.IsUpdateAvailable);
        Assert.Equal("1.10", result.CurrentVersion);
        Assert.Equal("v1.11", result.LatestVersion);
        Assert.Equal("https://github.com/tether-works/IgnisDisc/releases/tag/v1.11", result.ReleaseUrl);
        Assert.Null(result.ErrorMessage);

        Assert.NotNull(recordedUserAgent);
        Assert.Contains("IgnisDisc/1.10", recordedUserAgent);
    }

    [Fact]
    public async Task CheckForUpdateAsync_WhenCurrentIsUpToDate_ReturnsNoUpdateAvailable()
    {
        string fakeJson = """
        {
            "tag_name": "v1.10",
            "name": "IgnisDisc v1.10",
            "html_url": "https://github.com/tether-works/IgnisDisc/releases/tag/v1.10"
        }
        """;

        var mockHandler = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(fakeJson, Encoding.UTF8, "application/json")
        });

        using var client = new HttpClient(mockHandler);
        var result = await GitHubUpdateChecker.CheckForUpdateAsync(
            currentVersion: "1.10",
            httpClient: client);

        Assert.True(result.Success);
        Assert.False(result.IsUpdateAvailable);
        Assert.Equal("1.10", result.CurrentVersion);
        Assert.Equal("v1.10", result.LatestVersion);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public async Task CheckForUpdateAsync_WhenHttpErrorOccurs_ReturnsErrorMessageSafely()
    {
        var mockHandler = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            ReasonPhrase = "Not Found"
        });

        using var client = new HttpClient(mockHandler);
        var result = await GitHubUpdateChecker.CheckForUpdateAsync(
            currentVersion: "1.10",
            httpClient: client);

        Assert.False(result.Success);
        Assert.False(result.IsUpdateAvailable);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("404", result.ErrorMessage);
    }

    [Fact]
    public async Task CheckForUpdateAsync_WhenResponseLacksTagName_ReturnsFailureMessage()
    {
        string invalidJson = """{ "message": "API rate limit exceeded" }""";

        var mockHandler = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(invalidJson, Encoding.UTF8, "application/json")
        });

        using var client = new HttpClient(mockHandler);
        var result = await GitHubUpdateChecker.CheckForUpdateAsync(
            currentVersion: "1.10",
            httpClient: client);

        Assert.False(result.Success);
        Assert.False(result.IsUpdateAvailable);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("tag_name", result.ErrorMessage);
    }

    #endregion
}
