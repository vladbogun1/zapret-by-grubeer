using Zapret.Core.Services;

namespace Zapret.Tests;

public sealed class ServiceCatalogTests
{
    [Fact]
    public void The_catalog_covers_the_services_the_specification_names()
    {
        foreach (var expected in new[] { "Discord", "Telegram", "WhatsApp", "YouTube", "Cloudflare", "GitHub", "ChatGPT", "Claude", "Gemini", "Instagram", "Facebook", "X" })
        {
            Assert.NotNull(ServiceCatalog.Find(expected));
        }

        Assert.All(ServiceCatalog.BuiltIn, s => Assert.NotEmpty(s.Domains));
        Assert.All(ServiceCatalog.BuiltIn, s => Assert.All(s.Domains, d => Assert.True(ServiceCatalog.IsPlausibleDomain(d), d)));
    }

    [Theory]
    [InlineData("discord.com", true)]
    [InlineData("web.telegram.org", true)]
    [InlineData("xn--80ak6aa92e.com", true)]
    [InlineData("localhost", false)]
    [InlineData("https://discord.com", false)]
    [InlineData("discord.com/app", false)]
    [InlineData("1.2.3.4:443", false)]
    [InlineData(".discord.com", false)]
    [InlineData("discord..com", false)]
    [InlineData("", false)]
    public void Domain_validation_accepts_hostnames_and_nothing_else(string value, bool expected) =>
        Assert.Equal(expected, ServiceCatalog.IsPlausibleDomain(value));

    [Fact]
    public void A_custom_service_is_normalised_before_it_is_accepted()
    {
        var ok = ServiceCatalog.TryCreateCustom(
            "  My Service  ",
            ["  *.Example.COM ", "example.com", "sub.example.com", "   "],
            "https://example.com/health",
            out var service,
            out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal("My Service", service!.Id);
        Assert.True(service.IsCustom);

        // Leading wildcards and casing are stripped, and the duplicate collapses.
        Assert.Equal(["example.com", "sub.example.com"], service.Domains);
    }

    [Theory]
    [InlineData("", new[] { "example.com" }, "service.error.name")]
    [InlineData("Discord", new[] { "example.com" }, "service.error.duplicate")]
    [InlineData("Mine", new string[0], "service.error.noDomains")]
    [InlineData("Mine", new[] { "not a domain" }, "service.error.badDomain")]
    public void Invalid_custom_services_are_rejected_with_a_reason(string id, string[] domains, string expectedError)
    {
        var ok = ServiceCatalog.TryCreateCustom(id, domains, null, out var service, out var error);

        Assert.False(ok);
        Assert.Null(service);
        Assert.Equal(expectedError, error);
    }
}

public sealed class UserListComposerTests
{
    private static readonly ServiceDefinition[] Catalog =
    [
        new("Discord", ServiceCategory.Messaging, ["discord.com", "discord.gg"]),
        new("YouTube", ServiceCategory.Video, ["youtube.com", "youtu.be"]),
    ];

    [Fact]
    public void Hand_written_lines_survive_a_rewrite()
    {
        const string existing = """
            # my own notes
            my.private.domain
            another.domain
            """;

        var composed = UserListComposer.Compose(existing, [Catalog[0]]);

        Assert.Contains("# my own notes", composed);
        Assert.Contains("my.private.domain", composed);
        Assert.Contains("another.domain", composed);
        Assert.Contains("discord.com", composed);
        Assert.Contains(UserListComposer.BeginMarker, composed);
    }

    [Fact]
    public void Switching_a_service_off_removes_only_its_domains()
    {
        var withBoth = UserListComposer.Compose("keep.me", Catalog);
        Assert.Contains("youtube.com", withBoth);

        var withoutYouTube = UserListComposer.Compose(withBoth, [Catalog[0]]);

        Assert.DoesNotContain("youtube.com", withoutYouTube);
        Assert.Contains("discord.com", withoutYouTube);
        Assert.Contains("keep.me", withoutYouTube);
    }

    [Fact]
    public void Rewriting_twice_does_not_stack_blocks()
    {
        var once = UserListComposer.Compose("keep.me", Catalog);
        var twice = UserListComposer.Compose(once, Catalog);

        Assert.Equal(once, twice);
        Assert.Single(twice.Split(UserListComposer.BeginMarker).Skip(1));
    }

    [Fact]
    public void Enabled_services_are_detected_from_the_managed_block()
    {
        var content = UserListComposer.Compose(null, Catalog);

        var enabled = UserListComposer.DetectEnabled(content, Catalog);

        Assert.Equal(["Discord", "YouTube"], enabled.OrderBy(x => x));
    }

    /// <summary>
    /// A half-present service means the block was edited by hand. Reading it as enabled would leave the user
    /// with a service that looks on and is not, so it reads as off and switching it on repairs the block.
    /// </summary>
    [Fact]
    public void A_partially_present_service_reads_as_disabled()
    {
        var content = UserListComposer.Compose(null, Catalog).Replace("youtu.be" + Environment.NewLine, string.Empty);

        var enabled = UserListComposer.DetectEnabled(content, Catalog);

        Assert.Equal(["Discord"], enabled);
    }

    [Fact]
    public void Domains_outside_the_block_do_not_count_as_enabled()
    {
        const string manual = """
            discord.com
            discord.gg
            """;

        Assert.Empty(UserListComposer.DetectEnabled(manual, Catalog));
    }

    /// <summary>Upstream's strategies break on an empty list, so an empty selection still yields a valid file.</summary>
    [Fact]
    public void An_empty_selection_leaves_a_usable_file()
    {
        var composed = UserListComposer.Compose(null, []);

        Assert.Contains("domain.example.abc", composed);
        Assert.DoesNotContain(UserListComposer.BeginMarker, composed);
    }

    [Fact]
    public void Upstream_placeholders_are_not_treated_as_user_data()
    {
        const string placeholderOnly = """
            # Never leave this file empty
            domain.example.abc
            """;

        var composed = UserListComposer.Compose(placeholderOnly, [Catalog[0]]);

        Assert.DoesNotContain("domain.example.abc", composed);
        Assert.Contains("discord.com", composed);
    }

    [Fact]
    public void An_unterminated_block_is_not_mistaken_for_user_data()
    {
        var broken = "keep.me" + Environment.NewLine + UserListComposer.BeginMarker + Environment.NewLine + "stale.domain";

        var composed = UserListComposer.Compose(broken, [Catalog[0]]);

        Assert.Contains("keep.me", composed);
        Assert.DoesNotContain("stale.domain", composed);
    }
}
