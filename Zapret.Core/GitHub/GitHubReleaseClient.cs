using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Zapret.Core.Model;

namespace Zapret.Core.GitHub;

public interface IGitHubReleaseClient
{
    /// <summary>
    /// Latest release for a repository, honouring the feed's ETag and dismissal memory. Returns
    /// <see cref="ReleaseCheckStatus.Unavailable"/> instead of throwing when GitHub cannot be reached.
    /// </summary>
    Task<ReleaseCheckResult> CheckAsync(
        string repository,
        ReleaseFeedState state,
        string? installedVersion,
        bool allowPreview,
        CancellationToken cancellationToken = default);

    Task<bool> DownloadAssetAsync(
        GitHubAsset asset,
        string destinationPath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Unauthenticated GitHub Releases access, done politely: ETag/If-None-Match, no token required,
/// and never a hard failure for the rest of the application. SPEC.md §8.3–§8.4.
/// </summary>
public sealed class GitHubReleaseClient : IGitHubReleaseClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly ILogger _logger;

    public GitHubReleaseClient(HttpClient http, ILogger<GitHubReleaseClient>? logger = null)
    {
        _http = http;
        _logger = logger ?? NullLogger<GitHubReleaseClient>.Instance;

        // GitHub rejects requests without a User-Agent. The product name is deliberately ASCII here.
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
        {
            _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ZapretByGrubeer", ThisAssembly.Version));
        }

        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public async Task<ReleaseCheckResult> CheckAsync(
        string repository,
        ReleaseFeedState state,
        string? installedVersion,
        bool allowPreview,
        CancellationToken cancellationToken = default)
    {
        var url = allowPreview
            ? $"https://api.github.com/repos/{repository}/releases?per_page=20"
            : $"https://api.github.com/repos/{repository}/releases/latest";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(state.ETag))
            {
                request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(state.ETag));
            }

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            state.LastCheckUtc = DateTimeOffset.UtcNow;

            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                _logger.LogDebug("{Repository} release metadata unchanged (304)", repository);
                var cached = Deserialize(state.CachedPayload, allowPreview);
                return cached is null
                    ? new ReleaseCheckResult(ReleaseCheckStatus.NotModified)
                    : Evaluate(cached, state, installedVersion, ReleaseCheckStatus.NotModified);
            }

            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            {
                // Rate limited: back off silently, the installed engine keeps running.
                _logger.LogInformation("GitHub rate limit reached while checking {Repository}", repository);
                return ReleaseCheckResult.Unavailable("Could not check for updates.");
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogInformation("GitHub returned {Status} for {Repository}", (int)response.StatusCode, repository);
                return ReleaseCheckResult.Unavailable("Could not check for updates.");
            }

            var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            state.ETag = response.Headers.ETag?.Tag;
            state.CachedPayload = payload;

            var release = Deserialize(payload, allowPreview);
            if (release is null)
            {
                return ReleaseCheckResult.Unavailable("No suitable release was published.");
            }

            return Evaluate(release, state, installedVersion, ReleaseCheckStatus.UpToDate);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // GitHub being unreachable is not an error condition for this product.
            _logger.LogInformation(ex, "Update check for {Repository} could not complete", repository);
            return ReleaseCheckResult.Unavailable("Could not check for updates.");
        }
    }

    private static GitHubRelease? Deserialize(string? payload, bool allowPreview)
    {
        if (string.IsNullOrWhiteSpace(payload)) return null;

        var trimmed = payload.TrimStart();
        if (trimmed.StartsWith('['))
        {
            var releases = JsonSerializer.Deserialize<List<GitHubRelease>>(payload, Json) ?? new List<GitHubRelease>();
            return releases
                .Where(r => !r.IsDraft && (allowPreview || !r.IsPrerelease))
                .OrderByDescending(r => EngineVersion.NormalizeTag(r.Tag), Comparer<string>.Create(EngineVersion.Compare))
                .FirstOrDefault();
        }

        var single = JsonSerializer.Deserialize<GitHubRelease>(payload, Json);
        if (single is null) return null;

        // /releases/latest already excludes drafts and prereleases, but never trust that blindly.
        return single.IsDraft || (!allowPreview && single.IsPrerelease) ? null : single;
    }

    private static ReleaseCheckResult Evaluate(
        GitHubRelease release,
        ReleaseFeedState state,
        string? installedVersion,
        ReleaseCheckStatus statusWhenCurrent)
    {
        state.LastSeenTag = release.Tag;

        var newer = EngineVersion.IsNewer(release.Tag, installedVersion);
        if (!newer) return new ReleaseCheckResult(statusWhenCurrent, release);

        return state.ShouldAnnounce(release.Tag)
            ? new ReleaseCheckResult(ReleaseCheckStatus.UpdateAvailable, release)
            : new ReleaseCheckResult(statusWhenCurrent, release, "A newer release exists but was dismissed or rejected earlier.");
    }

    public async Task<bool> DownloadAssetAsync(
        GitHubAsset asset,
        string destinationPath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var partial = destinationPath + ".part";

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            if (File.Exists(partial)) File.Delete(partial);

            using var response = await _http
                .GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Download of {Asset} failed with {Status}", asset.Name, (int)response.StatusCode);
                return false;
            }

            var expected = response.Content.Headers.ContentLength ?? asset.Size;

            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var target = File.Create(partial))
            {
                var buffer = new byte[81920];
                long written = 0;
                int read;

                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    written += read;
                    if (expected > 0) progress?.Report(Math.Min(1.0, (double)written / expected));
                }
            }

            var actual = new FileInfo(partial).Length;
            if (asset.Size > 0 && actual != asset.Size)
            {
                _logger.LogWarning("Download of {Asset} is {Actual} bytes, expected {Expected}", asset.Name, actual, asset.Size);
                File.Delete(partial);
                return false;
            }

            if (File.Exists(destinationPath)) File.Delete(destinationPath);
            File.Move(partial, destinationPath);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Download of {Asset} could not complete", asset.Name);
            if (File.Exists(partial))
            {
                try { File.Delete(partial); } catch (IOException) { /* leave the temp file behind */ }
            }

            return false;
        }
    }
}

internal static class ThisAssembly
{
    public static string Version { get; } =
        typeof(ThisAssembly).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
}
