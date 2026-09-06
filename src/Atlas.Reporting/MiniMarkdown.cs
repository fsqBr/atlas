using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Atlas.Reporting;

/// <summary>
/// The small Markdown subset the model is asked to produce — '#'..'###' headings,
/// '-'/'*' bullets, '1.' numbered items, **bold**, `code`, paragraphs — turned into
/// HTML with every character of text escaped. No raw HTML passes through, no links
/// are created (the plan cites nothing outside the report), unknown syntax is text.
/// </summary>
public static partial class MiniMarkdown
{
    [GeneratedRegex(@"^(#{1,6})\s+(.*)$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^\s*[-*•]\s+(.*)$")]
    private static partial Regex Bullet();

    [GeneratedRegex(@"^\s*\d{1,3}[.)]\s+(.*)$")]
    private static partial Regex Numbered();

    [GeneratedRegex(@"\*\*(.+?)\*\*|`([^`]+)`")]
    private static partial Regex Inline();

    /// <param name="headingOffset">Added to the heading level so a '##' in the text can render as h3 inside a report section.</param>
    public static string ToHtml(string? markdown, int headingOffset = 0)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(markdown.Length + 256);
        var paragraph = new List<string>();
        string? openList = null;

        void CloseList()
        {
            if (openList is not null)
            {
                sb.Append("</").Append(openList).Append('>');
                openList = null;
            }
        }

        void FlushParagraph()
        {
            if (paragraph.Count > 0)
            {
                sb.Append("<p>").Append(InlineHtml(string.Join(' ', paragraph))).Append("</p>");
                paragraph.Clear();
            }
        }

        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0)
            {
                FlushParagraph();
                CloseList();
                continue;
            }

            if (Heading().Match(line) is { Success: true } h)
            {
                FlushParagraph();
                CloseList();
                var level = Math.Clamp(h.Groups[1].Value.Length + headingOffset, 1, 6);
                sb.Append("<h").Append(level).Append('>').Append(InlineHtml(h.Groups[2].Value.TrimEnd('#', ' '))).Append("</h").Append(level).Append('>');
                continue;
            }

            if (Bullet().Match(line) is { Success: true } b)
            {
                FlushParagraph();
                OpenList("ul");
                sb.Append("<li>").Append(InlineHtml(b.Groups[1].Value)).Append("</li>");
                continue;
            }

            if (Numbered().Match(line) is { Success: true } n)
            {
                FlushParagraph();
                OpenList("ol");
                sb.Append("<li>").Append(InlineHtml(n.Groups[1].Value)).Append("</li>");
                continue;
            }

            if (openList is not null && line.StartsWith("  ", StringComparison.Ordinal))
            {
                // continuation of the previous list item
                sb.Insert(sb.Length - "</li>".Length, ' ' + InlineHtml(line.Trim()));
                continue;
            }

            CloseList();
            paragraph.Add(line.Trim());
        }

        FlushParagraph();
        CloseList();
        return sb.ToString();

        void OpenList(string tag)
        {
            if (openList != tag)
            {
                CloseList();
                sb.Append('<').Append(tag).Append('>');
                openList = tag;
            }
        }
    }

    /// <summary>Plain text (no markup) for places that cannot show HTML, such as a CSV cell or a log line.</summary>
    public static string ToPlainText(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(markdown.Length);
        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw;
            if (Heading().Match(line) is { Success: true } h)
            {
                line = h.Groups[2].Value.TrimEnd('#', ' ').ToUpperInvariant();
            }

            sb.AppendLine(Inline().Replace(line, m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value));
        }

        return sb.ToString().TrimEnd();
    }

    private static string InlineHtml(string text)
    {
        var sb = new StringBuilder(text.Length + 16);
        var last = 0;
        foreach (Match m in Inline().Matches(text))
        {
            sb.Append(WebUtility.HtmlEncode(text[last..m.Index]));
            if (m.Groups[1].Success)
            {
                sb.Append("<strong>").Append(WebUtility.HtmlEncode(m.Groups[1].Value)).Append("</strong>");
            }
            else
            {
                sb.Append("<code>").Append(WebUtility.HtmlEncode(m.Groups[2].Value)).Append("</code>");
            }

            last = m.Index + m.Length;
        }

        sb.Append(WebUtility.HtmlEncode(text[last..]));
        return sb.ToString();
    }
}
