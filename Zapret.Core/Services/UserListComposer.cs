using System.Text;

namespace Zapret.Core.Services;

/// <summary>
/// Writes the manager's service selection into a user domain list without destroying anything the user put
/// there by hand.
/// <para>
/// The list is a plain text file that upstream's strategies read directly, and people do edit it. So the
/// manager owns only a delimited block, exactly like the hosts file: lines outside it are preserved verbatim,
/// in their original order. Switching a service off removes its domains from the block and nothing else.
/// </para>
/// </summary>
public static class UserListComposer
{
    public const string BeginMarker = "# BEGIN ZapretByGrubeer services";
    public const string EndMarker = "# END ZapretByGrubeer services";

    /// <summary>Domains kept by upstream as placeholder content; they are not user data.</summary>
    private static readonly string[] Placeholders = ["domain.example.abc", "# Never leave this file empty"];

    /// <summary>
    /// Produces the new file content: the user's own lines, then the managed block with the domains of every
    /// enabled service. An empty selection still leaves a valid file, because upstream breaks on an empty one.
    /// </summary>
    public static string Compose(string? existingContent, IEnumerable<ServiceDefinition> enabled)
    {
        var manual = ManualLines(existingContent).ToList();

        var domains = enabled
            .SelectMany(s => s.Domains)
            .Select(d => d.Trim().ToLowerInvariant())
            .Where(d => d.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(d => d, StringComparer.Ordinal)
            .ToList();

        var builder = new StringBuilder();

        foreach (var line in manual) builder.AppendLine(line);

        if (domains.Count > 0)
        {
            builder.AppendLine(BeginMarker);

            // ASCII on purpose, like the hosts markers: this file is read by the cygwin-built winws.exe, and
            // there is nothing to gain from putting non-ASCII text into a comment it has to parse past.
            builder.AppendLine("# Managed by ZapretByGrubeer. Edits inside this block are overwritten.");
            foreach (var domain in domains) builder.AppendLine(domain);
            builder.AppendLine(EndMarker);
        }

        // Upstream's strategies fail on an empty list, so the placeholder goes back when nothing is left.
        if (builder.Length == 0)
        {
            builder.AppendLine("# Never leave this file empty");
            builder.AppendLine("domain.example.abc");
        }

        return builder.ToString();
    }

    /// <summary>
    /// Which services the file currently enables, judged by every one of a service's domains being present in
    /// the managed block. Partial presence means someone edited it: the service reads as off rather than on,
    /// so switching it on repairs the block instead of leaving it half-applied.
    /// </summary>
    public static IReadOnlyList<string> DetectEnabled(string? content, IEnumerable<ServiceDefinition> catalog)
    {
        var managed = ManagedDomains(content);
        if (managed.Count == 0) return Array.Empty<string>();

        return catalog
            .Where(s => s.Domains.Count > 0 && s.Domains.All(d => managed.Contains(d.Trim().ToLowerInvariant())))
            .Select(s => s.Id)
            .ToList();
    }

    /// <summary>Lines the user owns: everything outside the managed block, minus upstream's placeholders.</summary>
    public static IEnumerable<string> ManualLines(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) yield break;

        var inside = false;

        foreach (var raw in content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = raw.TrimEnd();

            if (!inside && line == BeginMarker)
            {
                inside = true;
                continue;
            }

            if (inside)
            {
                // An unterminated block (someone deleted the end marker) runs to the end of the file, so a
                // broken block cannot become permanent user data.
                if (line == EndMarker) inside = false;
                continue;
            }

            if (line.Length == 0) continue;
            if (Placeholders.Contains(line, StringComparer.OrdinalIgnoreCase)) continue;

            yield return line;
        }
    }

    private static HashSet<string> ManagedDomains(string? content)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(content)) return result;

        var inside = false;

        foreach (var raw in content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = raw.Trim();

            if (line == BeginMarker)
            {
                inside = true;
                continue;
            }

            if (!inside) continue;
            if (line == EndMarker) break;

            if (line.Length == 0 || line.StartsWith('#')) continue;

            result.Add(line.ToLowerInvariant());
        }

        return result;
    }
}
