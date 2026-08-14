namespace Zapret.Core.Services;

public enum ServiceCategory
{
    Messaging,
    Video,
    Infrastructure,
    Ai,
    Social,
    Custom,
}

/// <summary>
/// A service a user can switch on. <see cref="Id"/> is a stable technical name and also the display name for
/// well-known products (Discord is Discord in every language); <see cref="Category"/> is a localisation key
/// resolved by the UI.
/// </summary>
public sealed record ServiceDefinition(
    string Id,
    ServiceCategory Category,
    IReadOnlyList<string> Domains,
    string? CheckUrl = null,
    bool IsCustom = false)
{
    public string CategoryKey => Category switch
    {
        ServiceCategory.Messaging => "category.messaging",
        ServiceCategory.Video => "category.video",
        ServiceCategory.Infrastructure => "category.infrastructure",
        ServiceCategory.Ai => "category.ai",
        ServiceCategory.Social => "category.social",
        _ => "category.custom",
    };
}

/// <summary>
/// The services the manager knows how to switch on, so a user never has to learn which domains belong to
/// which product or edit a text file by hand (SPEC.md §18, §34).
/// <para>
/// This is not a replacement for upstream's own lists: those stay untouched and keep working. These entries
/// only ever drive the manager-owned block of the user list, which is additive on top of upstream.
/// </para>
/// </summary>
public static class ServiceCatalog
{
    public static IReadOnlyList<ServiceDefinition> BuiltIn { get; } =
    [
        new("Discord", ServiceCategory.Messaging,
            ["discord.com", "discordapp.com", "discord.gg", "discordapp.net", "discord.media"],
            "https://discord.com/app"),

        new("Telegram", ServiceCategory.Messaging,
            ["telegram.org", "t.me", "telegram.me", "web.telegram.org", "telegra.ph"],
            "https://web.telegram.org/"),

        new("WhatsApp", ServiceCategory.Messaging,
            ["whatsapp.com", "whatsapp.net", "web.whatsapp.com"],
            "https://web.whatsapp.com/"),

        new("YouTube", ServiceCategory.Video,
            ["youtube.com", "youtu.be", "ytimg.com", "googlevideo.com", "ggpht.com", "youtube-nocookie.com"],
            "https://www.youtube.com/generate_204"),

        new("Twitch", ServiceCategory.Video,
            ["twitch.tv", "ttvnw.net", "jtvnw.net"],
            "https://www.twitch.tv/"),

        new("Cloudflare", ServiceCategory.Infrastructure,
            ["cloudflare.com", "cloudflare-dns.com", "cdnjs.cloudflare.com"],
            "https://cloudflare.com/cdn-cgi/trace"),

        new("GitHub", ServiceCategory.Infrastructure,
            ["github.com", "githubusercontent.com", "githubassets.com", "ghcr.io"],
            "https://github.com/"),

        new("ChatGPT", ServiceCategory.Ai,
            ["openai.com", "chatgpt.com", "oaistatic.com", "oaiusercontent.com"],
            "https://chatgpt.com/"),

        new("Claude", ServiceCategory.Ai,
            ["claude.ai", "anthropic.com"],
            "https://claude.ai/"),

        new("Gemini", ServiceCategory.Ai,
            ["gemini.google.com", "bard.google.com"],
            "https://gemini.google.com/"),

        new("Instagram", ServiceCategory.Social,
            ["instagram.com", "cdninstagram.com"],
            "https://www.instagram.com/"),

        new("Facebook", ServiceCategory.Social,
            ["facebook.com", "fbcdn.net", "fb.com"],
            "https://www.facebook.com/"),

        new("X", ServiceCategory.Social,
            ["x.com", "twitter.com", "twimg.com", "t.co"],
            "https://x.com/"),
    ];

    public static ServiceDefinition? Find(string id) =>
        BuiltIn.FirstOrDefault(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Validates a user-defined service. Domains are checked rather than trusted: a stray line in the user
    /// list makes upstream's <c>winws</c> reject the whole file, which would look like the manager broke.
    /// </summary>
    public static bool TryCreateCustom(
        string id,
        IEnumerable<string> domains,
        string? checkUrl,
        out ServiceDefinition? service,
        out string? error)
    {
        service = null;

        var name = id.Trim();
        if (name.Length == 0)
        {
            error = "service.error.name";
            return false;
        }

        if (Find(name) is not null)
        {
            error = "service.error.duplicate";
            return false;
        }

        var cleaned = domains
            .Select(d => d.Trim().TrimStart('*', '.').ToLowerInvariant())
            .Where(d => d.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (cleaned.Count == 0)
        {
            error = "service.error.noDomains";
            return false;
        }

        if (cleaned.Any(d => !IsPlausibleDomain(d)))
        {
            error = "service.error.badDomain";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(checkUrl) &&
            !Uri.TryCreate(checkUrl, UriKind.Absolute, out var uri) ||
            (!string.IsNullOrWhiteSpace(checkUrl) && Uri.TryCreate(checkUrl, UriKind.Absolute, out var parsed)
             && parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp))
        {
            error = "service.error.badUrl";
            return false;
        }

        service = new ServiceDefinition(name, ServiceCategory.Custom, cleaned, checkUrl, IsCustom: true);
        error = null;
        return true;
    }

    /// <summary>
    /// A hostname, not a URL and not an address range: upstream's domain list takes one host per line, and
    /// anything else there is silently ineffective rather than an error the user would notice.
    /// </summary>
    public static bool IsPlausibleDomain(string value)
    {
        if (value.Length is 0 or > 253) return false;
        if (value.Contains('/') || value.Contains(':') || value.Contains(' ')) return false;
        if (!value.Contains('.')) return false;
        if (value.StartsWith('.') || value.EndsWith('.') || value.Contains("..")) return false;

        return value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');
    }
}
