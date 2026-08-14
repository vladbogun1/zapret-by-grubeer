using System.Text;
using System.Text.RegularExpressions;

namespace Zapret.Core.Update;

/// <summary>
/// Turns GitHub release notes into plain text for native display. Release notes are untrusted input
/// authored outside the product, so nothing from them is ever interpreted: no HTML, no scripts, no
/// markup engine. Markdown syntax is reduced to text and everything else is escaped away
/// (SPEC.md §8.5).
/// </summary>
public static class ReleaseNotes
{
    private static readonly Regex HtmlTag = new(@"<[^>]{0,400}>", RegexOptions.Compiled);
    private static readonly Regex Heading = new(@"^\s{0,3}#{1,6}\s*", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex Bullet = new(@"^\s{0,3}[-*+]\s+", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex Link = new(@"\[([^\]]*)\]\(([^)\s]+)[^)]*\)", RegexOptions.Compiled);
    private static readonly Regex Image = new(@"!\[([^\]]*)\]\([^)]*\)", RegexOptions.Compiled);
    private static readonly Regex Emphasis = new(@"(\*\*|__|\*|_|`{1,3}|~~)", RegexOptions.Compiled);
    private static readonly Regex BlankLines = new(@"\n{3,}", RegexOptions.Compiled);

    public static string ToPlainText(string? markdown, int maxLength = 8000)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return string.Empty;

        var text = markdown.Replace("\r\n", "\n").Replace('\r', '\n');

        // Strip anything tag-shaped first, so no markup survives into the UI in any form.
        text = HtmlTag.Replace(text, string.Empty);

        text = Image.Replace(text, m => string.IsNullOrWhiteSpace(m.Groups[1].Value) ? string.Empty : m.Groups[1].Value);
        text = Link.Replace(text, m => string.IsNullOrWhiteSpace(m.Groups[1].Value) ? m.Groups[2].Value : $"{m.Groups[1].Value} ({m.Groups[2].Value})");
        text = Heading.Replace(text, string.Empty);
        text = Bullet.Replace(text, "• ");
        text = Emphasis.Replace(text, string.Empty);

        // Undo the HTML entities GitHub sometimes emits, after tags are already gone.
        text = text
            .Replace("&nbsp;", " ")
            .Replace("&amp;", "&")
            .Replace("&lt;", "<")
            .Replace("&gt;", ">")
            .Replace("&quot;", "\"");

        text = BlankLines.Replace(text, "\n\n").Trim();

        if (text.Length <= maxLength) return text;

        var truncated = text[..maxLength];
        var lastBreak = truncated.LastIndexOf('\n');
        if (lastBreak > maxLength / 2) truncated = truncated[..lastBreak];

        return truncated.TrimEnd() + "\n\n…";
    }

    /// <summary>A one-line summary for a notification body.</summary>
    public static string Summarize(string? markdown, int maxLength = 160)
    {
        var text = ToPlainText(markdown, maxLength * 4);
        if (text.Length == 0) return string.Empty;

        var builder = new StringBuilder();
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim().TrimStart('•', ' ');
            if (trimmed.Length == 0) continue;

            builder.Append(builder.Length == 0 ? trimmed : " " + trimmed);
            if (builder.Length >= maxLength) break;
        }

        var result = builder.ToString();
        return result.Length <= maxLength ? result : result[..maxLength].TrimEnd() + "…";
    }
}
