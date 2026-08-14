using System.Net;
using System.Text;
using Zapret.Core.GitHub;

namespace Zapret.Tests;

public sealed class GitHubReleaseClientTests
{
    private const string Repository = "Flowseal/zapret-discord-youtube";

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();

        public List<HttpRequestMessage> Requests { get; } = new();

        public StubHandler Then(Func<HttpRequestMessage, HttpResponseMessage> response)
        {
            _responses.Enqueue(response);
            return this;
        }

        public StubHandler ThenJson(string json, string? etag = null, HttpStatusCode status = HttpStatusCode.OK) =>
            Then(_ =>
            {
                var message = new HttpResponseMessage(status)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                };
                if (etag is not null) message.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(etag);
                return message;
            });

        public StubHandler ThenStatus(HttpStatusCode status) => Then(_ => new HttpResponseMessage(status));

        public StubHandler ThenThrow() => Then(_ => throw new HttpRequestException("no network"));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var next = _responses.Count > 0 ? _responses.Dequeue() : _ => new HttpResponseMessage(HttpStatusCode.NotFound);
            return Task.FromResult(next(request));
        }
    }

    private static string ReleaseJson(string tag, bool prerelease = false, bool draft = false, string assetName = "zapret-x.zip") => $$"""
        {
          "tag_name": "{{tag}}",
          "name": "{{tag}}",
          "body": "notes for {{tag}}",
          "draft": {{draft.ToString().ToLowerInvariant()}},
          "prerelease": {{prerelease.ToString().ToLowerInvariant()}},
          "published_at": "2026-08-09T15:20:39Z",
          "html_url": "https://github.com/x/y/releases/tag/{{tag}}",
          "assets": [{ "name": "{{assetName}}", "size": 1507408, "browser_download_url": "https://example.test/{{assetName}}" }]
        }
        """;

    private static (GitHubReleaseClient Client, StubHandler Handler) Create(StubHandler handler) =>
        (new GitHubReleaseClient(new HttpClient(handler)), handler);

    [Fact]
    public async Task A_newer_stable_release_is_announced()
    {
        var (client, handler) = Create(new StubHandler().ThenJson(ReleaseJson("1.10.2"), etag: "\"abc\""));
        var state = new ReleaseFeedState();

        var result = await client.CheckAsync(Repository, state, "1.10.1", allowPreview: false);

        Assert.Equal(ReleaseCheckStatus.UpdateAvailable, result.Status);
        Assert.Equal("1.10.2", result.Release!.Tag);
        Assert.Equal("\"abc\"", state.ETag);
        Assert.Equal("1.10.2", state.LastSeenTag);
        Assert.NotNull(state.LastCheckUtc);
        Assert.Contains("releases/latest", handler.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task The_same_version_is_not_an_update()
    {
        var (client, _) = Create(new StubHandler().ThenJson(ReleaseJson("1.10.1")));

        var result = await client.CheckAsync(Repository, new ReleaseFeedState(), "1.10.1", allowPreview: false);

        Assert.Equal(ReleaseCheckStatus.UpToDate, result.Status);
    }

    [Fact]
    public async Task A_stored_etag_is_sent_and_a_304_still_yields_the_cached_release()
    {
        var handler = new StubHandler()
            .ThenJson(ReleaseJson("1.10.2"), etag: "\"v1\"")
            .ThenStatus(HttpStatusCode.NotModified);

        var (client, _) = Create(handler);
        var state = new ReleaseFeedState();

        await client.CheckAsync(Repository, state, "1.10.1", allowPreview: false);
        var second = await client.CheckAsync(Repository, state, "1.10.2", allowPreview: false);

        Assert.Equal("\"v1\"", handler.Requests[1].Headers.IfNoneMatch.Single().Tag);
        Assert.Equal(ReleaseCheckStatus.NotModified, second.Status);
        Assert.Equal("1.10.2", second.Release!.Tag);
    }

    [Fact]
    public async Task A_dismissed_release_is_never_announced_again()
    {
        var (client, _) = Create(new StubHandler().ThenJson(ReleaseJson("1.10.2")));
        var state = new ReleaseFeedState { DismissedTag = "1.10.2" };

        var result = await client.CheckAsync(Repository, state, "1.10.1", allowPreview: false);

        Assert.NotEqual(ReleaseCheckStatus.UpdateAvailable, result.Status);
        Assert.Contains("dismissed", result.Message);
    }

    [Fact]
    public async Task A_release_rejected_by_validation_is_not_offered_automatically()
    {
        var (client, _) = Create(new StubHandler().ThenJson(ReleaseJson("1.11.0")));
        var state = new ReleaseFeedState { RejectedTags = { "1.11.0" } };

        var result = await client.CheckAsync(Repository, state, "1.10.1", allowPreview: false);

        Assert.NotEqual(ReleaseCheckStatus.UpdateAvailable, result.Status);
    }

    [Fact]
    public async Task Prereleases_are_ignored_unless_preview_is_enabled()
    {
        var (stableClient, _) = Create(new StubHandler().ThenJson(ReleaseJson("1.11.0-rc1", prerelease: true)));

        var ignored = await stableClient.CheckAsync(Repository, new ReleaseFeedState(), "1.10.1", allowPreview: false);
        Assert.Equal(ReleaseCheckStatus.Unavailable, ignored.Status);

        var listJson = $"[{ReleaseJson("1.11.0-rc1", prerelease: true)},{ReleaseJson("1.10.1")}]";
        var previewHandler = new StubHandler().ThenJson(listJson);
        var (previewClient, handler) = Create(previewHandler);

        var accepted = await previewClient.CheckAsync(Repository, new ReleaseFeedState(), "1.10.1", allowPreview: true);

        Assert.Equal(ReleaseCheckStatus.UpdateAvailable, accepted.Status);
        Assert.Equal("1.11.0-rc1", accepted.Release!.Tag);
        Assert.Contains("releases?per_page", handler.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task Drafts_are_never_used_even_with_preview_enabled()
    {
        var listJson = $"[{ReleaseJson("2.0.0", draft: true)},{ReleaseJson("1.10.1")}]";
        var (client, _) = Create(new StubHandler().ThenJson(listJson));

        var result = await client.CheckAsync(Repository, new ReleaseFeedState(), "1.10.1", allowPreview: true);

        Assert.Equal(ReleaseCheckStatus.UpToDate, result.Status);
        Assert.Equal("1.10.1", result.Release!.Tag);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Github_failures_are_reported_calmly(HttpStatusCode status)
    {
        var (client, _) = Create(new StubHandler().ThenStatus(status));

        var result = await client.CheckAsync(Repository, new ReleaseFeedState(), "1.10.1", allowPreview: false);

        Assert.Equal(ReleaseCheckStatus.Unavailable, result.Status);
        Assert.Equal("Could not check for updates.", result.Message);
    }

    [Fact]
    public async Task No_network_is_not_an_exception_for_the_caller()
    {
        var (client, _) = Create(new StubHandler().ThenThrow());

        var result = await client.CheckAsync(Repository, new ReleaseFeedState(), "1.10.1", allowPreview: false);

        Assert.Equal(ReleaseCheckStatus.Unavailable, result.Status);
    }

    [Fact]
    public void Only_a_zip_asset_is_usable()
    {
        var release = new GitHubRelease
        {
            Tag = "1.10.1",
            Assets =
            [
                new GitHubAsset { Name = "zapret-1.10.1.rar" },
                new GitHubAsset { Name = "zapret-1.10.1.tar.gz" },
                new GitHubAsset { Name = "zapret-1.10.1.zip" },
            ],
        };

        Assert.Equal("zapret-1.10.1.zip", release.SelectZipAsset()!.Name);
        Assert.Null(new GitHubRelease { Assets = [new GitHubAsset { Name = "only.rar" }] }.SelectZipAsset());
    }

    [Fact]
    public void The_polling_interval_is_respected()
    {
        var now = new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
        var state = new ReleaseFeedState { LastCheckUtc = now.AddHours(-3) };

        Assert.False(state.IsDue(now, TimeSpan.FromHours(6)));
        Assert.True(state.IsDue(now.AddHours(4), TimeSpan.FromHours(6)));
        Assert.True(new ReleaseFeedState().IsDue(now, TimeSpan.FromHours(6)));
    }
}
