using System.Net;
using System.Text;
using OpenPuckWeblessSettings.Services;

namespace OpenPuckWeblessSettings.Tests;

public sealed class AppReleaseServiceTests
{
    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("1.2.3-rc.1+build.7", "1.2.3-rc.1")]
    public void ParsesAndNormalizesSemanticVersions(string input, string expected)
    {
        Assert.True(SemanticVersion.TryParse(input, out var version));
        Assert.Equal(expected, version.ToString());
    }

    [Theory]
    [InlineData("1.0", false)]
    [InlineData("1.0.0-", false)]
    [InlineData("1.0.0-01", false)]
    [InlineData("1.0.0_rc1", false)]
    [InlineData("1.0.0", true)]
    public void RejectsMalformedSemanticVersions(string input, bool expected) =>
        Assert.Equal(expected, SemanticVersion.TryParse(input, out _));

    [Theory]
    [InlineData("1.0.0", "1.0.0-rc.9")]
    [InlineData("1.0.0-rc.2", "1.0.0-rc.1")]
    [InlineData("1.0.0-beta.11", "1.0.0-beta.2")]
    [InlineData("2.0.0-alpha", "1.99.99")]
    public void OrdersSemanticVersions(string newer, string older) =>
        Assert.True(SemanticVersion.Parse(newer) > SemanticVersion.Parse(older));

    [Fact]
    public async Task StableChecksIgnorePrereleasesDraftsAndMalformedTags()
    {
        using var service = Service(HttpStatusCode.OK, ReleasesJson);
        var result = await service.CheckAsync(SemanticVersion.Parse("1.0.0"), false, CancellationToken.None);

        Assert.True(result.IsUpdateAvailable);
        Assert.Equal("1.1.0", result.Latest!.Version.ToString());
        Assert.False(result.Latest.IsPrerelease);
    }

    [Fact]
    public async Task PrereleaseChecksSelectHighestSemanticVersion()
    {
        using var service = Service(HttpStatusCode.OK, ReleasesJson);
        var result = await service.CheckAsync(SemanticVersion.Parse("1.1.0-rc.1"), true, CancellationToken.None);

        Assert.True(result.IsUpdateAvailable);
        Assert.Equal("1.2.0-rc.2", result.Latest!.Version.ToString());
        Assert.True(result.Latest.IsPrerelease);
    }

    [Fact]
    public async Task CurrentVersionDoesNotReportAnUpdate()
    {
        using var service = Service(HttpStatusCode.OK, ReleasesJson);
        var result = await service.CheckAsync(SemanticVersion.Parse("1.1.0"), false, CancellationToken.None);
        Assert.False(result.IsUpdateAvailable);
    }

    [Fact]
    public async Task SemanticPrereleaseSuffixCannotLeakIntoStableChannel()
    {
        const string json = """
            [{"tag_name":"v2.0.0-rc.1","name":"Mislabeled RC","body":"","prerelease":false,"draft":false,"published_at":"2026-07-05T00:00:00Z","html_url":"https://github.com/kevinf100/OpenPuckWeblessSettings/releases/tag/v2.0.0-rc.1"}]
            """;
        using var service = Service(HttpStatusCode.OK, json);
        var result = await service.CheckAsync(SemanticVersion.Parse("1.0.0"), false, CancellationToken.None);
        Assert.Null(result.Latest);
    }

    [Fact]
    public async Task GitHubErrorsAreSurfaced()
    {
        using var service = Service(HttpStatusCode.Forbidden, "[]");
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => service.GetReleasesAsync(CancellationToken.None));
        Assert.Contains("rate-limited", error.Message);
    }

    [Fact]
    public async Task CancellationIsHonored()
    {
        using var service = new GitHubAppReleaseService(new HttpClient(new DelegateHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        })));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetReleasesAsync(cancellation.Token));
    }

    [Fact]
    public void NormalizesAssemblyVersionsAndRestrictsReleaseLinks()
    {
        Assert.Equal("0.2.0-rc.1", AssemblyApplicationVersionProvider.Normalize("v0.2.0-rc.1+abc123"));
        Assert.True(GitHubAppReleaseService.IsTrustedReleaseUri(new Uri("https://github.com/kevinf100/OpenPuckWeblessSettings/releases/tag/v0.2.0")));
        Assert.False(GitHubAppReleaseService.IsTrustedReleaseUri(new Uri("https://example.com/kevinf100/OpenPuckWeblessSettings/releases/tag/v0.2.0")));
    }

    private static GitHubAppReleaseService Service(HttpStatusCode status, string body) =>
        new(new HttpClient(new DelegateHandler((_, _) => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        }))));

    private const string ReleasesJson = """
        [
          {"tag_name":"v1.1.0","name":"Stable","body":"notes","prerelease":false,"draft":false,"published_at":"2026-07-01T00:00:00Z","html_url":"https://github.com/kevinf100/OpenPuckWeblessSettings/releases/tag/v1.1.0"},
          {"tag_name":"v1.2.0-rc.2","name":"RC","body":"preview","prerelease":true,"draft":false,"published_at":"2026-07-02T00:00:00Z","html_url":"https://github.com/kevinf100/OpenPuckWeblessSettings/releases/tag/v1.2.0-rc.2"},
          {"tag_name":"v9.0.0","name":"Draft","body":"hidden","prerelease":false,"draft":true,"published_at":"2026-07-03T00:00:00Z","html_url":"https://github.com/kevinf100/OpenPuckWeblessSettings/releases/tag/v9.0.0"},
          {"tag_name":"not-a-version","name":"Bad","body":"bad","prerelease":false,"draft":false,"published_at":"2026-07-04T00:00:00Z","html_url":"https://github.com/kevinf100/OpenPuckWeblessSettings/releases/tag/bad"}
        ]
        """;

    private sealed class DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
