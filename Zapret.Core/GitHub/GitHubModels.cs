using System.Text.Json.Serialization;

namespace Zapret.Core.GitHub;

public sealed record GitHubAsset
{
    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
    [JsonPropertyName("size")] public long Size { get; init; }
    [JsonPropertyName("browser_download_url")] public string DownloadUrl { get; init; } = string.Empty;
}

public sealed record GitHubRelease
{
    [JsonPropertyName("tag_name")] public string Tag { get; init; } = string.Empty;
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("body")] public string? Body { get; init; }
    [JsonPropertyName("draft")] public bool IsDraft { get; init; }
    [JsonPropertyName("prerelease")] public bool IsPrerelease { get; init; }
    [JsonPropertyName("published_at")] public DateTimeOffset? PublishedAt { get; init; }
    [JsonPropertyName("html_url")] public string? HtmlUrl { get; init; }
    [JsonPropertyName("assets")] public IReadOnlyList<GitHubAsset> Assets { get; init; } = Array.Empty<GitHubAsset>();

    public bool IsStable => !IsDraft && !IsPrerelease;

    /// <summary>
    /// The asset to download. Upstream ships .zip, .rar and .tar.gz of the same build; only .zip is
    /// extractable without a third-party dependency, so it is required rather than merely preferred.
    /// </summary>
    public GitHubAsset? SelectZipAsset() =>
        Assets.FirstOrDefault(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
}

/// <summary>Outcome of a release check. "Could not reach GitHub" is a normal, non-alarming result.</summary>
public enum ReleaseCheckStatus
{
    UpToDate,
    UpdateAvailable,
    NotModified,
    Unavailable,
}

public sealed record ReleaseCheckResult(
    ReleaseCheckStatus Status,
    GitHubRelease? Release = null,
    string? Message = null)
{
    public static ReleaseCheckResult Unavailable(string message) => new(ReleaseCheckStatus.Unavailable, null, message);
}

/// <summary>
/// Per-feed polling memory: enough to honour ETags, the 6-hour interval, and a dismissed release
/// that must never be announced again. SPEC.md §8.3.
/// </summary>
public sealed class ReleaseFeedState
{
    public DateTimeOffset? LastCheckUtc { get; set; }
    public string? LastSeenTag { get; set; }
    public string? ETag { get; set; }
    public string? DismissedTag { get; set; }

    /// <summary>Tags that failed validation or rolled back; not offered automatically again.</summary>
    public List<string> RejectedTags { get; set; } = new();

    /// <summary>Cached payload matching <see cref="ETag"/>, so a 304 still yields a release.</summary>
    public string? CachedPayload { get; set; }

    public bool IsDue(DateTimeOffset now, TimeSpan interval) =>
        LastCheckUtc is null || now - LastCheckUtc.Value >= interval;

    public bool ShouldAnnounce(string tag) =>
        !string.Equals(DismissedTag, tag, StringComparison.OrdinalIgnoreCase)
        && !RejectedTags.Contains(tag, StringComparer.OrdinalIgnoreCase);
}
