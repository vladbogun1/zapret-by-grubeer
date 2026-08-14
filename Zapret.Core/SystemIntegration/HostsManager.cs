using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Zapret.Core.SystemIntegration;

/// <summary>
/// Owns exactly one section of the system hosts file and nothing else. Upstream only tells the user to
/// copy the file by hand; the manager does it properly, which is what makes a clean uninstall possible
/// (SPEC.md §10.1, docs/flowseal-compatibility.md §5.3).
/// </summary>
public sealed class HostsManager(
    string? hostsFilePath = null,
    string? backupDirectory = null,
    ILogger<HostsManager>? logger = null)
{
    // ASCII on purpose: the hosts file is parsed by the DNS client resolver.
    public const string BeginMarker = "# BEGIN ZapretByGrubeer";
    public const string EndMarker = "# END ZapretByGrubeer";

    private readonly string _hosts = hostsFilePath ?? AppPaths.SystemHostsFile;
    private readonly string _backups = backupDirectory ?? AppPaths.HostsBackups;
    private readonly ILogger _logger = logger ?? NullLogger<HostsManager>.Instance;
    private readonly object _gate = new();

    public bool IsApplied()
    {
        lock (_gate)
        {
            return File.Exists(_hosts) && ReadLines().Any(l => l.TrimEnd() == BeginMarker);
        }
    }

    /// <summary>Replaces the managed section with <paramref name="payload"/>, backing the file up first.</summary>
    public bool Apply(string payload, string? sourceDescription = null)
    {
        lock (_gate)
        {
            try
            {
                Backup();

                var kept = WithoutManagedSection(ReadLines()).ToList();

                // One blank line before the section keeps the file readable if it did not end with one.
                if (kept.Count > 0 && !string.IsNullOrWhiteSpace(kept[^1])) kept.Add(string.Empty);

                kept.Add(BeginMarker);
                kept.Add($"# Managed by {AppPaths.DisplayName}. Edits inside this block are overwritten.");
                if (sourceDescription is not null) kept.Add($"# Source: {sourceDescription}");

                foreach (var line in payload.Replace("\r\n", "\n").Split('\n'))
                {
                    var trimmed = line.TrimEnd();
                    if (trimmed == BeginMarker || trimmed == EndMarker) continue;
                    kept.Add(trimmed);
                }

                kept.Add(EndMarker);

                Write(kept);
                _logger.LogInformation("Applied the managed hosts section to {Path}", _hosts);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogError(ex, "Could not write the managed hosts section");
                return false;
            }
        }
    }

    /// <summary>Removes only the managed section. Everything else in the file is left untouched.</summary>
    public bool Remove()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_hosts)) return true;

                var lines = ReadLines().ToList();
                if (!lines.Any(l => l.TrimEnd() == BeginMarker)) return true;

                Backup();
                Write(WithoutManagedSection(lines).ToList());

                _logger.LogInformation("Removed the managed hosts section from {Path}", _hosts);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogError(ex, "Could not remove the managed hosts section");
                return false;
            }
        }
    }

    /// <summary>
    /// Drops the block between the markers. An unterminated block (someone deleted the end marker by
    /// hand) is treated as running to the end of file, so a broken block cannot become permanent.
    /// </summary>
    internal static IEnumerable<string> WithoutManagedSection(IEnumerable<string> lines)
    {
        var inside = false;

        foreach (var line in lines)
        {
            var trimmed = line.TrimEnd();

            if (!inside && trimmed == BeginMarker)
            {
                inside = true;
                continue;
            }

            if (inside)
            {
                if (trimmed == EndMarker) inside = false;
                continue;
            }

            yield return line;
        }
    }

    public string? Backup()
    {
        if (!File.Exists(_hosts)) return null;

        Directory.CreateDirectory(_backups);
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        var destination = Path.Combine(_backups, $"hosts-{stamp}.bak");

        if (!File.Exists(destination)) File.Copy(_hosts, destination);
        return destination;
    }

    private IEnumerable<string> ReadLines() =>
        File.Exists(_hosts) ? File.ReadAllLines(_hosts, Encoding.UTF8) : Array.Empty<string>();

    private void Write(IReadOnlyList<string> lines)
    {
        // The resolver expects a plain ASCII-compatible file: UTF-8 without a BOM.
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        File.WriteAllText(_hosts, string.Join(Environment.NewLine, lines) + Environment.NewLine, encoding);
    }
}
