using Zapret.Core.SystemIntegration;

namespace Zapret.Tests;

public sealed class HostsManagerTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "zapret-tests", "hosts-" + Guid.NewGuid().ToString("n"));
    private readonly string _hosts;
    private readonly HostsManager _manager;

    public HostsManagerTests()
    {
        Directory.CreateDirectory(_work);
        _hosts = Path.Combine(_work, "hosts");
        _manager = new HostsManager(_hosts, Path.Combine(_work, "backups"));
    }

    private const string Existing = """
        # Copyright (c) 1993-2009 Microsoft Corp.
        127.0.0.1       localhost
        10.0.0.5        my-nas
        """;

    [Fact]
    public void Applying_adds_a_managed_section_and_keeps_everything_else()
    {
        File.WriteAllText(_hosts, Existing);

        Assert.True(_manager.Apply("1.2.3.4 discord.com\r\n5.6.7.8 youtube.com", "Flowseal Zapret 1.10.1"));

        var text = File.ReadAllText(_hosts);

        Assert.Contains("127.0.0.1       localhost", text);
        Assert.Contains("10.0.0.5        my-nas", text);
        Assert.Contains(HostsManager.BeginMarker, text);
        Assert.Contains(HostsManager.EndMarker, text);
        Assert.Contains("1.2.3.4 discord.com", text);
        Assert.Contains("Flowseal Zapret 1.10.1", text);
        Assert.True(_manager.IsApplied());
    }

    [Fact]
    public void Applying_twice_replaces_the_section_instead_of_stacking_it()
    {
        File.WriteAllText(_hosts, Existing);

        _manager.Apply("1.1.1.1 first.example");
        _manager.Apply("2.2.2.2 second.example");

        var lines = File.ReadAllLines(_hosts);

        Assert.Single(lines, l => l.TrimEnd() == HostsManager.BeginMarker);
        Assert.Single(lines, l => l.TrimEnd() == HostsManager.EndMarker);
        Assert.DoesNotContain("first.example", File.ReadAllText(_hosts));
        Assert.Contains("second.example", File.ReadAllText(_hosts));
    }

    [Fact]
    public void Removing_restores_the_file_to_what_it_was()
    {
        File.WriteAllText(_hosts, Existing);
        var before = File.ReadAllLines(_hosts);

        _manager.Apply("1.2.3.4 discord.com");
        Assert.True(_manager.Remove());

        var after = File.ReadAllLines(_hosts).Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();

        Assert.Equal(before.Where(l => !string.IsNullOrWhiteSpace(l)), after);
        Assert.False(_manager.IsApplied());
    }

    [Fact]
    public void Removing_when_nothing_was_applied_changes_nothing()
    {
        File.WriteAllText(_hosts, Existing);

        Assert.True(_manager.Remove());
        Assert.Equal(Existing, File.ReadAllText(_hosts));
    }

    [Fact]
    public void Entries_the_manager_did_not_write_are_never_touched()
    {
        File.WriteAllText(_hosts, Existing + Environment.NewLine + "9.9.9.9 someone-elses-block.example");

        _manager.Apply("1.2.3.4 discord.com");
        _manager.Remove();

        Assert.Contains("9.9.9.9 someone-elses-block.example", File.ReadAllText(_hosts));
    }

    /// <summary>A hand-broken block must not become permanent, so an unterminated section runs to EOF.</summary>
    [Fact]
    public void An_unterminated_section_is_still_removable()
    {
        File.WriteAllText(_hosts, Existing + Environment.NewLine + HostsManager.BeginMarker + Environment.NewLine + "1.2.3.4 orphan.example");

        _manager.Remove();

        var text = File.ReadAllText(_hosts);
        Assert.DoesNotContain("orphan.example", text);
        Assert.DoesNotContain(HostsManager.BeginMarker, text);
        Assert.Contains("localhost", text);
    }

    [Fact]
    public void A_backup_is_written_before_any_change()
    {
        File.WriteAllText(_hosts, Existing);

        _manager.Apply("1.2.3.4 discord.com");

        var backups = Directory.GetFiles(Path.Combine(_work, "backups"), "hosts-*.bak");
        Assert.NotEmpty(backups);
        Assert.Equal(Existing, File.ReadAllText(backups[0]));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_work)) Directory.Delete(_work, recursive: true);
        }
        catch (IOException)
        {
            // Never fail a run over temp files.
        }
    }
}
