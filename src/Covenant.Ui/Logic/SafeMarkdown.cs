using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Covenant.Ui.Logic;

/// <summary>A deliberately small Markdown → HTML renderer for model output. Safety comes from the order of
/// operations, not from filtering: the ENTIRE input is HTML-encoded first (so no input byte can ever become a tag,
/// an attribute or an entity), and only then are a handful of Markdown constructs turned into tags that this class
/// writes itself. Supported: paragraphs (single newlines become &lt;br&gt;), headings # to ###, **bold** / __bold__,
/// *italic* / _italic_, `inline code`, ``` fenced code blocks, "-", "*", "+" unordered and "1." / "1)" ordered
/// lists (one level), and [text](url) links whose URL starts with http:// or https:// (anything else stays literal
/// text). Links open in a new tab with rel="noopener noreferrer". No raw HTML, images, tables or nesting.
/// The console renders model output ONLY through this class (MarkupString); server/provider error text is never
/// rendered as markup.</summary>
public static class SafeMarkdown
{
    private static readonly Regex Heading = new(@"^(#{1,3})[ \t]+(.+?)[ \t#]*$", RegexOptions.CultureInvariant);
    private static readonly Regex Unordered = new(@"^[ \t]{0,3}[-*+][ \t]+(.*)$", RegexOptions.CultureInvariant);
    private static readonly Regex Ordered = new(@"^[ \t]{0,3}\d{1,9}[.)][ \t]+(.*)$", RegexOptions.CultureInvariant);
    private static readonly Regex Fence = new(@"^[ \t]{0,3}```", RegexOptions.CultureInvariant);

    // Inline: code spans and links are found first (their contents are not emphasis-processed).
    private static readonly Regex CodeOrLink = new(@"`([^`\n]+)`|\[([^\]\n]+)\]\(([^)\s]+)\)", RegexOptions.CultureInvariant);
    private static readonly Regex BoldStar = new(@"\*\*(?=\S)(.+?)(?<=\S)\*\*", RegexOptions.CultureInvariant);
    private static readonly Regex BoldUnderscore = new(@"(?<![\w])__(?=\S)(.+?)(?<=\S)__(?![\w])", RegexOptions.CultureInvariant);
    private static readonly Regex ItalicStar = new(@"(?<![\w*])\*(?=[^\s*])(.+?)(?<=[^\s*])\*(?![\w*])", RegexOptions.CultureInvariant);
    private static readonly Regex ItalicUnderscore = new(@"(?<![\w])_(?=[^\s_])(.+?)(?<=[^\s_])_(?![\w])", RegexOptions.CultureInvariant);

    private enum ListKind { None, Unordered, Ordered }

    /// <summary>Renders <paramref name="markdown"/> to an HTML fragment that contains only the tags listed in the
    /// class summary. Null/empty input renders as an empty string.</summary>
    public static string ToHtml(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown)) return "";

        // 1. Encode everything. After this line no '<', '>', '"', '\'' or '&' from the input survives unescaped.
        var encoded = Encode(markdown.Replace("\r\n", "\n").Replace('\r', '\n'));
        var lines = encoded.Split('\n');

        var html = new StringBuilder(encoded.Length + 64);
        var paragraph = new List<string>();
        var list = ListKind.None;

        void FlushParagraph()
        {
            if (paragraph.Count == 0) return;
            html.Append("<p>");
            for (int i = 0; i < paragraph.Count; i++)
            {
                if (i > 0) html.Append("<br>");
                html.Append(Inline(paragraph[i]));
            }
            html.Append("</p>");
            paragraph.Clear();
        }

        void CloseList()
        {
            if (list == ListKind.Unordered) html.Append("</ul>");
            else if (list == ListKind.Ordered) html.Append("</ol>");
            list = ListKind.None;
        }

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            if (Fence.IsMatch(line))
            {
                FlushParagraph();
                CloseList();
                var code = new StringBuilder();
                int j = i + 1;
                bool first = true;
                for (; j < lines.Length && !Fence.IsMatch(lines[j]); j++)
                {
                    if (!first) code.Append('\n');
                    code.Append(lines[j]);
                    first = false;
                }
                // Unclosed fence (e.g. a stream still in progress): everything to the end is code.
                html.Append("<pre><code>").Append(code).Append("</code></pre>");
                i = j; // skips the closing fence (or ends the loop)
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                FlushParagraph();
                CloseList();
                continue;
            }

            var h = Heading.Match(line);
            if (h.Success)
            {
                FlushParagraph();
                CloseList();
                int level = h.Groups[1].Value.Length; // 1..3 → h3..h5: the page owns h1/h2
                var tag = "h" + (level + 2).ToString(System.Globalization.CultureInfo.InvariantCulture);
                html.Append('<').Append(tag).Append('>').Append(Inline(h.Groups[2].Value)).Append("</").Append(tag).Append('>');
                continue;
            }

            var u = Unordered.Match(line);
            var o = u.Success ? Match.Empty : Ordered.Match(line);
            if (u.Success || o.Success)
            {
                FlushParagraph();
                var kind = u.Success ? ListKind.Unordered : ListKind.Ordered;
                if (list != kind)
                {
                    CloseList();
                    html.Append(kind == ListKind.Unordered ? "<ul>" : "<ol>");
                    list = kind;
                }
                html.Append("<li>").Append(Inline((u.Success ? u : o).Groups[1].Value)).Append("</li>");
                continue;
            }

            CloseList();
            paragraph.Add(line.Trim());
        }

        FlushParagraph();
        CloseList();
        return html.ToString();
    }

    /// <summary>HTML-encodes text for element content and double-quoted attributes.</summary>
    public static string Encode(string text) => WebUtility.HtmlEncode(text);

    /// <summary>True for an absolute http(s) URL (checked on the ENCODED text; quotes and angle brackets cannot
    /// occur there unescaped, so the value cannot break out of the href attribute).</summary>
    public static bool IsSafeUrl(string encodedUrl)
    {
        if (!(encodedUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
              || encodedUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
            return false;
        var decoded = WebUtility.HtmlDecode(encodedUrl);
        if (!Uri.TryCreate(decoded, UriKind.Absolute, out var uri)) return false;
        return uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
    }

    private static string Inline(string encoded)
    {
        var sb = new StringBuilder(encoded.Length + 16);
        int last = 0;
        foreach (Match m in CodeOrLink.Matches(encoded))
        {
            sb.Append(Emphasis(encoded[last..m.Index]));
            if (m.Groups[1].Success)
            {
                sb.Append("<code>").Append(m.Groups[1].Value).Append("</code>");
            }
            else
            {
                var text = m.Groups[2].Value;
                var url = m.Groups[3].Value;
                if (IsSafeUrl(url))
                    sb.Append("<a href=\"").Append(url).Append("\" target=\"_blank\" rel=\"noopener noreferrer\">")
                      .Append(Emphasis(text)).Append("</a>");
                else
                    sb.Append(Emphasis(m.Value)); // not a permitted link: literal (already encoded) text
            }
            last = m.Index + m.Length;
        }
        sb.Append(Emphasis(encoded[last..]));
        return sb.ToString();
    }

    private static string Emphasis(string encoded)
    {
        if (encoded.Length == 0) return encoded;
        var s = BoldStar.Replace(encoded, "<strong>$1</strong>");
        s = BoldUnderscore.Replace(s, "<strong>$1</strong>");
        s = ItalicStar.Replace(s, "<em>$1</em>");
        s = ItalicUnderscore.Replace(s, "<em>$1</em>");
        return s;
    }
}
