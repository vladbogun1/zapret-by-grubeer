using System.Text;
using System.Text.RegularExpressions;
using Zapret.Core.Model;

namespace Zapret.Core.Flowseal;

public sealed record StrategyParseContext(string RuntimeRoot, GameFilterState GameFilter)
{
    /// <summary>Runtime root with a trailing separator, which is what <c>%~dp0</c> expands to.</summary>
    public string RootWithSeparator =>
        RuntimeRoot.EndsWith(Path.DirectorySeparatorChar) ? RuntimeRoot : RuntimeRoot + Path.DirectorySeparatorChar;
}

public sealed class StrategyParseException(string message) : Exception(message);

/// <summary>
/// Turns an upstream strategy <c>.bat</c> into the <c>argv</c> that <c>cmd.exe</c> would hand to
/// <c>winws.exe</c>, without executing the batch file.
/// <para>
/// Upstream's own <c>service.bat</c> does this in batch, and has to fight <c>for %%i in (…)</c>
/// tokenizing on commas — hence its <c>mergeargs</c> state machine. That workaround is
/// deliberately not reproduced here: commas are ordinary characters. See
/// docs/flowseal-compatibility.md §4.2.
/// </para>
/// </summary>
public static class StrategyBatParser
{
    private const string EngineExecutable = "winws.exe";

    private static readonly Regex SetAssignment = new(
        @"^\s*@?set\s+(?:""(?<name>[^=""]+)=(?<qvalue>[^""]*)""|(?<name2>[^\s=""]+)=(?<value>[^\r\n]*))\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static StrategyDescriptor Parse(string filePath, StrategyParseContext context)
    {
        var id = Path.GetFileNameWithoutExtension(filePath);
        try
        {
            var text = File.ReadAllText(filePath);
            return ParseText(text, id, filePath, context);
        }
        catch (IOException ex)
        {
            return Unsupported(id, filePath, $"could not be read: {ex.Message}");
        }
    }

    /// <summary>
    /// Never throws for a strategy the manager cannot understand: an unparsable strategy becomes an
    /// unsupported entry with a reason, so one odd file never takes the catalog down.
    /// </summary>
    public static StrategyDescriptor ParseText(string text, string id, string filePath, StrategyParseContext context)
    {
        try
        {
            return ParseCore(text, id, filePath, context);
        }
        catch (StrategyParseException ex)
        {
            return Unsupported(id, filePath, ex.Message);
        }
    }

    private static StrategyDescriptor ParseCore(string text, string id, string filePath, StrategyParseContext context)
    {
        var warnings = new List<string>();
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        var commandIndex = FindCommandLine(lines);
        if (commandIndex < 0)
        {
            throw new StrategyParseException($"no {EngineExecutable} invocation found");
        }

        var assignments = CollectAssignments(lines, commandIndex);
        var joined = JoinContinuations(lines, commandIndex);
        var argumentText = StripInvocationPrefix(joined);
        var expanded = ExpandVariables(argumentText, context, assignments, id);
        var tokens = Tokenize(expanded);

        if (tokens.Count == 0)
        {
            throw new StrategyParseException($"{EngineExecutable} invocation carries no arguments");
        }

        var arguments = new List<string>(tokens.Count);
        var referenced = new List<string>();

        foreach (var token in tokens)
        {
            var rooted = RootRelativePaths(token, context.RootWithSeparator, referenced);
            arguments.Add(rooted);
        }

        var leftover = arguments.FirstOrDefault(a => a.Contains('%'));
        if (leftover is not null)
        {
            throw new StrategyParseException($"argument '{leftover}' still contains an unexpanded variable");
        }

        return new StrategyDescriptor
        {
            Id = id,
            DisplayName = ToDisplayName(id),
            FilePath = filePath,
            Arguments = arguments,
            ReferencedPaths = referenced,
            Warnings = warnings,
        };
    }

    /// <summary>
    /// <c>general (ALT11)</c> → <c>ALT11</c>; <c>general</c> stays as-is. Display only — the id is
    /// what gets stored and what upstream writes to its registry marker.
    /// </summary>
    public static string ToDisplayName(string id)
    {
        var open = id.IndexOf('(');
        var close = id.LastIndexOf(')');
        if (open >= 0 && close > open)
        {
            var inner = id[(open + 1)..close].Trim();
            if (inner.Length > 0) return inner;
        }

        return id;
    }

    private static StrategyDescriptor Unsupported(string id, string filePath, string reason) => new()
    {
        Id = id,
        DisplayName = ToDisplayName(id),
        FilePath = filePath,
        UnsupportedReason = reason,
    };

    private static int FindCommandLine(string[] lines)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            if (IsComment(lines[i])) continue;
            if (lines[i].Contains(EngineExecutable, StringComparison.OrdinalIgnoreCase)) return i;
        }

        return -1;
    }

    private static bool IsComment(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith('@')) trimmed = trimmed[1..].TrimStart();

        return trimmed.StartsWith("::", StringComparison.Ordinal)
            || trimmed.Equals("rem", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("rem ", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Collects <c>set</c> assignments that precede the invocation, so <c>%BIN%</c> and
    /// <c>%LISTS%</c> resolve from the file itself rather than from a hardcoded table.
    /// </summary>
    private static Dictionary<string, string> CollectAssignments(string[] lines, int upToExclusive)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < upToExclusive && i < lines.Length; i++)
        {
            if (IsComment(lines[i])) continue;

            var match = SetAssignment.Match(lines[i]);
            if (!match.Success) continue;

            var name = match.Groups["name"].Success ? match.Groups["name"].Value : match.Groups["name2"].Value;
            var value = match.Groups["name"].Success ? match.Groups["qvalue"].Value : match.Groups["value"].Value;
            map[name.Trim()] = value;
        }

        return map;
    }

    /// <summary>
    /// Joins <c>^</c>-continued physical lines the way <c>cmd.exe</c> does: the caret and the
    /// newline disappear, and whatever whitespace preceded the caret is kept as the separator.
    /// </summary>
    private static string JoinContinuations(string[] lines, int startIndex)
    {
        var builder = new StringBuilder();

        for (var i = startIndex; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd();

            if (line.EndsWith('^') && !line.EndsWith("^^", StringComparison.Ordinal))
            {
                builder.Append(line[..^1]);
                continue;
            }

            builder.Append(line);
            break;
        }

        return builder.ToString();
    }

    /// <summary>Drops everything up to and including the <c>winws.exe</c> token.</summary>
    private static string StripInvocationPrefix(string commandLine)
    {
        var index = commandLine.IndexOf(EngineExecutable, StringComparison.OrdinalIgnoreCase);
        if (index < 0) throw new StrategyParseException($"no {EngineExecutable} invocation found");

        var position = index + EngineExecutable.Length;
        if (position < commandLine.Length && commandLine[position] == '"') position++;

        return commandLine[position..];
    }

    private static string ExpandVariables(
        string text,
        StrategyParseContext context,
        Dictionary<string, string> assignments,
        string strategyId,
        int depth = 0)
    {
        if (depth > 8) throw new StrategyParseException("variable expansion recursion is too deep");

        var builder = new StringBuilder(text.Length + 64);

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '%')
            {
                builder.Append(text[i]);
                continue;
            }

            // %~dp0 and friends: a tilde modifier applied to argument 0, the script itself.
            if (i + 1 < text.Length && text[i + 1] == '~')
            {
                var end = text.IndexOf('0', i + 2);
                if (end > 0)
                {
                    var modifiers = text[(i + 2)..end];
                    builder.Append(ExpandScriptReference(modifiers, context, strategyId));
                    i = end;
                    continue;
                }
            }

            var close = text.IndexOf('%', i + 1);
            if (close < 0)
            {
                // A lone percent sign. Batch would drop it; keeping it would leak into argv, and
                // the caller treats a leftover '%' as a parse failure, which is the safe outcome.
                builder.Append(text[i]);
                continue;
            }

            var name = text[(i + 1)..close];
            builder.Append(ResolveVariable(name, context, assignments, strategyId, depth));
            i = close;
        }

        return builder.ToString();
    }

    private static string ExpandScriptReference(string modifiers, StrategyParseContext context, string strategyId) =>
        modifiers switch
        {
            "dp" => context.RootWithSeparator,
            "d" => Path.GetPathRoot(context.RuntimeRoot) ?? context.RootWithSeparator,
            "p" => context.RootWithSeparator,
            "n" => strategyId,
            "nx" => strategyId + ".bat",
            "f" => Path.Combine(context.RootWithSeparator, strategyId + ".bat"),
            _ => throw new StrategyParseException($"unsupported script reference '%~{modifiers}0%'"),
        };

    private static string ResolveVariable(
        string name,
        StrategyParseContext context,
        Dictionary<string, string> assignments,
        string strategyId,
        int depth)
    {
        switch (name.ToLowerInvariant())
        {
            case "gamefilter":
                return context.GameFilter.AnyPorts;
            case "gamefiltertcp":
                return context.GameFilter.TcpPorts;
            case "gamefilterudp":
                return context.GameFilter.UdpPorts;
        }

        if (assignments.TryGetValue(name, out var value))
        {
            return ExpandVariables(value, context, assignments, strategyId, depth + 1);
        }

        // Never guess. An unknown variable makes the strategy unsupported, with the name shown.
        throw new StrategyParseException(
            $"variable '%{name}%' is not defined by the strategy and is not known to the manager");
    }

    /// <summary>
    /// Splits on whitespace outside double quotes, dropping the quotes — what <c>cmd.exe</c> hands
    /// to the process. Commas are ordinary characters.
    /// </summary>
    internal static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var hasContent = false;

        foreach (var c in text)
        {
            switch (c)
            {
                case '"':
                    inQuotes = !inQuotes;
                    hasContent = true;
                    break;

                case ' ' or '\t' when !inQuotes:
                    if (hasContent)
                    {
                        tokens.Add(current.ToString());
                        current.Clear();
                        hasContent = false;
                    }
                    break;

                case '^' when !inQuotes && current.Length == 0:
                    // A stray continuation caret that survived line joining.
                    break;

                default:
                    current.Append(c);
                    hasContent = true;
                    break;
            }
        }

        if (hasContent) tokens.Add(current.ToString());

        return tokens;
    }

    /// <summary>
    /// Makes relative file references absolute against the runtime root, mirroring what upstream's
    /// installer does for quoted arguments, and records every path-valued argument so the caller can
    /// validate existence.
    /// </summary>
    private static string RootRelativePaths(string token, string rootWithSeparator, List<string> referenced)
    {
        var separatorIndex = token.IndexOf('=');
        var name = separatorIndex > 0 ? token[..separatorIndex] : string.Empty;
        var value = separatorIndex > 0 ? token[(separatorIndex + 1)..] : token;

        if (value.Length == 0) return token;

        var listPrefix = value[0] == '@';
        var candidate = listPrefix ? value[1..] : value;

        if (!LooksLikePath(candidate)) return token;

        var absolute = Path.IsPathRooted(candidate)
            ? Path.GetFullPath(candidate)
            : Path.GetFullPath(Path.Combine(rootWithSeparator, candidate));

        referenced.Add(absolute);

        var rebuilt = (listPrefix ? "@" : string.Empty) + absolute;
        return separatorIndex > 0 ? name + "=" + rebuilt : rebuilt;
    }

    private static bool LooksLikePath(string value)
    {
        if (value.Length == 0) return false;
        if (value.Contains('\\') || value.Contains('/')) return true;

        // A bare file name only counts as a path when it carries a file extension, so values like
        // "discord.media" (a hostname) are left alone: they have no separator and no known extension.
        var extension = Path.GetExtension(value);
        return extension is ".txt" or ".bin" or ".exe" or ".dll" or ".log";
    }
}
