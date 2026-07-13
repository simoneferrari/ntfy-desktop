using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Navigation;
using NtfyDesktop.Domain;

namespace NtfyDesktop.Features.Feed.Markdown;

/// <summary>
/// A small, hand-written renderer for the <em>subset</em> of Markdown ntfy bodies use:
/// <b>bold</b>, <i>italic</i>, <c>inline code</c>, fenced code blocks, links, and line
/// breaks. Deliberately not a full CommonMark implementation — headings, lists, tables,
/// blockquotes and the rest of the long tail are left as plain text. Output is a flat list
/// of WPF block elements (paragraph <see cref="TextBlock"/>s and code-block borders) so the
/// feed can drop them straight into a vertical panel.
///
/// Only rendered when ntfy flagged the message as <c>text/markdown</c> — we never auto-detect
/// markdown in a plain body (that would mangle e.g. <c>_underscored_filenames_</c>).
/// </summary>
internal static class MarkdownRenderer
{
    // Comma-separated fallback list — WPF picks the first installed family.
    private static readonly FontFamily MonoFont = new("Cascadia Mono, Consolas, Courier New");

    private const string PrimaryTextBrush = "TextFillColorPrimaryBrush";
    private const string CodeBackgroundBrush = "SubtleFillColorSecondaryBrush";
    private const string LinkBrush = "AccentTextFillColorPrimaryBrush";

    // Vertical gap between consecutive blocks (paragraphs / code blocks).
    private const double BlockSpacing = 6;

    /// <summary>
    /// Builds the rendered block elements for a markdown body. Returns an empty list for
    /// null/blank input.
    /// </summary>
    /// <summary>
    /// Builds a FlowDocument twin of the renderer, for hosting in a RichTextBox so the
    /// message body supports native text selection. Reuses the same inline/fence parsing so
    /// links, emphasis, code spans, and escaping stay in lockstep.
    /// </summary>
    public static FlowDocument RenderFlowDocument(string? text)
    {
        var doc = new FlowDocument
        {
            PagePadding = new Thickness(0),
        };

        if (string.IsNullOrEmpty(text)) return doc;

        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        var i = 0;
        while (i < lines.Length)
        {
            var line = lines[i];

            if (line.Trim().Length == 0) { i++; continue; }

            Block block;
            if (TryFenceMarker(line, out var fence))
            {
                var code = new List<string>();
                i++;
                while (i < lines.Length && !TryFenceMarker(lines[i], out _, fence))
                    code.Add(lines[i++]);
                if (i < lines.Length) i++;

                block = CodeBlockParagraph(string.Join("\n", code));
            }
            else
            {
                var para = new List<string>();
                while (i < lines.Length && lines[i].Trim().Length > 0 && !TryFenceMarker(lines[i], out _))
                    para.Add(lines[i++]);

                block = ParagraphBlock(para);
            }

            // Align spacing exactly with the original TextBlock margins:
            // First block has no top margin; subsequent blocks are spaced by BlockSpacing.
            if (doc.Blocks.Count > 0)
                block.Margin = new Thickness(0, BlockSpacing, 0, 0);
            else
                block.Margin = new Thickness(0);

            doc.Blocks.Add(block);
        }

        return doc;
    }

    /// <summary>
    /// Flattens a markdown body to clean plain text — strips emphasis/code markers, unwraps
    /// links to their label, and drops code fences (keeping the code lines). Used for the
    /// Windows toast, whose template can't render markup, so it shows readable text instead of
    /// literal <c>**bold**</c>. Real newlines are preserved. Returns "" for null/blank input.
    /// </summary>
    public static string ToPlainText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var output = new List<string>();

        var i = 0;
        while (i < lines.Length)
        {
            if (TryFenceMarker(lines[i], out var fence))
            {
                i++;
                while (i < lines.Length && !TryFenceMarker(lines[i], out _, fence))
                    output.Add(lines[i++]); // code lines pass through verbatim
                if (i < lines.Length) i++; // consume closing fence
                continue;
            }

            var sb = new StringBuilder();
            AppendPlainInline(sb, lines[i]);
            output.Add(sb.ToString());
            i++;
        }

        return string.Join("\n", output).Trim();
    }

    // Plain-text twin of ParseInlines: same scan, but appends stripped text instead of building
    // WPF inlines. Reuses the same span helpers so the two stay in lockstep.
    private static void AppendPlainInline(StringBuilder sb, string text)
    {
        var i = 0;
        var n = text.Length;
        while (i < n)
        {
            var c = text[i];

            if (c == '\\' && i + 1 < n && IsEscapable(text[i + 1]))
            {
                sb.Append(text[i + 1]);
                i += 2;
                continue;
            }

            if (c == '`' && TryCodeSpan(text, i, out var codeText, out var afterCode))
            {
                sb.Append(codeText);
                i = afterCode;
                continue;
            }

            if (c == '[' && TryLink(text, i, out var label, out _, out var afterLink))
            {
                AppendPlainInline(sb, label);
                i = afterLink;
                continue;
            }

            if ((c == '*' || c == '_') && TryEmphasis(text, i, out var inner, out _, out var afterEmph))
            {
                AppendPlainInline(sb, inner);
                i = afterEmph;
                continue;
            }

            sb.Append(c);
            i++;
        }
    }

    private static Paragraph ParagraphBlock(List<string> lines)
    {
        var paragraph = new Paragraph();
        paragraph.SetResourceReference(TextElement.ForegroundProperty, PrimaryTextBrush);

        for (var k = 0; k < lines.Count; k++)
        {
            if (k > 0) paragraph.Inlines.Add(new LineBreak());
            foreach (var inline in ParseInlines(lines[k]))
                paragraph.Inlines.Add(inline);
        }

        return paragraph;
    }

    private static Paragraph CodeBlockParagraph(string code)
    {
        var run = new Run(code) { FontFamily = MonoFont };
        var paragraph = new Paragraph(run)
        {
            Padding = new Thickness(8, 6, 8, 6),
        };
        paragraph.SetResourceReference(Paragraph.BackgroundProperty, CodeBackgroundBrush);
        paragraph.SetResourceReference(TextElement.ForegroundProperty, PrimaryTextBrush);
        return paragraph;
    }

    // ===== Inline parsing =====

    // Recursive-descent over a single line, recognising code spans, links, and **/__ strong,
    // */_ emphasis. Anything that doesn't form a valid span is emitted as literal text, so
    // stray markers never swallow content.
    private static List<Inline> ParseInlines(string text)
    {
        var result = new List<Inline>();
        var buffer = new StringBuilder();

        void Flush()
        {
            if (buffer.Length == 0) return;
            result.Add(new Run(buffer.ToString()));
            buffer.Clear();
        }

        var i = 0;
        var n = text.Length;
        while (i < n)
        {
            var c = text[i];

            // Backslash escape of a punctuation char → emit the char literally.
            if (c == '\\' && i + 1 < n && IsEscapable(text[i + 1]))
            {
                buffer.Append(text[i + 1]);
                i += 2;
                continue;
            }

            if (c == '`' && TryCodeSpan(text, i, out var codeText, out var afterCode))
            {
                Flush();
                result.Add(CodeRun(codeText));
                i = afterCode;
                continue;
            }

            if (c == '[' && TryLink(text, i, out var label, out var url, out var afterLink))
            {
                Flush();
                AddLink(result, label, url);
                i = afterLink;
                continue;
            }

            if ((c == '*' || c == '_') && TryEmphasis(text, i, out var inner, out var dlen, out var afterEmph))
            {
                Flush();
                Span span = dlen == 2 ? new Bold() : new Italic();
                foreach (var innerInline in ParseInlines(inner))
                    span.Inlines.Add(innerInline);
                result.Add(span);
                i = afterEmph;
                continue;
            }

            buffer.Append(c);
            i++;
        }

        Flush();
        return result;
    }

    // CommonMark treats any ASCII punctuation as escapable. Letters/digits/whitespace are not.
    private static bool IsEscapable(char c) =>
        c is (>= '!' and <= '/') or (>= ':' and <= '@') or (>= '[' and <= '`') or (>= '{' and <= '~');

    // A code span opens with a run of N backticks and closes with the next run of exactly N.
    private static bool TryCodeSpan(string text, int start, out string code, out int after)
    {
        code = string.Empty;
        after = start;

        var n = text.Length;
        var fenceLen = 0;
        while (start + fenceLen < n && text[start + fenceLen] == '`') fenceLen++;

        var contentStart = start + fenceLen;
        var j = contentStart;
        while (j < n)
        {
            if (text[j] != '`') { j++; continue; }

            var runLen = 0;
            while (j + runLen < n && text[j + runLen] == '`') runLen++;
            if (runLen == fenceLen)
            {
                var content = text.Substring(contentStart, j - contentStart);
                // CommonMark: a single leading and trailing space is stripped (lets you write
                // `` `code` `` containing a backtick), but only if the content isn't all spaces.
                if (content.Length >= 2 && content[0] == ' ' && content[^1] == ' ' && content.Trim().Length > 0)
                    content = content[1..^1];
                code = content;
                after = j + runLen;
                return true;
            }
            j += runLen;
        }

        return false; // no matching closing run — treat the backticks as literal
    }

    // [label](url) — label may contain further inline markup; url is taken up to the first
    // space (an optional "title" after it is ignored) or the closing paren.
    private static bool TryLink(string text, int start, out string label, out string url, out int after)
    {
        label = string.Empty;
        url = string.Empty;
        after = start;

        var n = text.Length;
        var close = text.IndexOf(']', start + 1);
        if (close < 0 || close + 1 >= n || text[close + 1] != '(') return false;

        var urlStart = close + 2;
        var urlEnd = text.IndexOf(')', urlStart);
        if (urlEnd < 0) return false;

        label = text.Substring(start + 1, close - start - 1);
        var rawUrl = text.Substring(urlStart, urlEnd - urlStart).Trim();

        // Drop an optional title: [t](url "title").
        var spaceIdx = rawUrl.IndexOf(' ');
        if (spaceIdx >= 0) rawUrl = rawUrl[..spaceIdx];

        url = rawUrl;
        after = urlEnd + 1;
        return label.Length > 0;
    }

    // Emphasis/strong starting at `start`. dlen is 2 for ** / __ (strong), 1 for * / _.
    // For `_` the delimiters must sit at word boundaries (CommonMark intraword rule) so
    // snake_case identifiers inside a markdown body aren't mangled; `*` is allowed intraword.
    private static bool TryEmphasis(string text, int start, out string inner, out int dlen, out int after)
    {
        inner = string.Empty;
        dlen = 0;
        after = start;

        var n = text.Length;
        var delim = text[start];

        var run = 0;
        while (start + run < n && text[start + run] == delim) run++;
        dlen = run >= 2 ? 2 : 1;

        var contentStart = start + dlen;
        if (contentStart >= n || char.IsWhiteSpace(text[contentStart])) return false; // no left-flank
        if (delim == '_' && start > 0 && char.IsLetterOrDigit(text[start - 1])) return false; // intraword _

        // Find a matching closing delimiter run of length >= dlen.
        var j = contentStart;
        while (j < n)
        {
            if (text[j] != delim) { j++; continue; }

            var closeLen = 0;
            while (j + closeLen < n && text[j + closeLen] == delim) closeLen++;

            if (closeLen >= dlen && j > contentStart && !char.IsWhiteSpace(text[j - 1]))
            {
                var afterClose = j + dlen;
                if (delim != '_' || afterClose >= n || !char.IsLetterOrDigit(text[afterClose]))
                {
                    inner = text.Substring(contentStart, j - contentStart);
                    after = afterClose;
                    return inner.Length > 0;
                }
            }

            j += closeLen;
        }

        return false; // unbalanced — caller emits the delimiter as literal text
    }

    private static Run CodeRun(string code)
    {
        var run = new Run(code) { FontFamily = MonoFont };
        run.SetResourceReference(TextElement.BackgroundProperty, CodeBackgroundBrush);
        return run;
    }

    // A safe http(s) link becomes a real Hyperlink (opened via SafeUrl); anything else falls
    // back to rendering the label text inline, so a javascript:/file: URL can't be clicked.
    private static void AddLink(List<Inline> result, string label, string url)
    {
        var labelInlines = ParseInlines(label);

        if (!SafeUrl.IsAllowed(url))
        {
            result.AddRange(labelInlines);
            return;
        }

        var link = new Hyperlink { NavigateUri = new Uri(url), ToolTip = url };
        link.SetResourceReference(TextElement.ForegroundProperty, LinkBrush);
        foreach (var inline in labelInlines) link.Inlines.Add(inline);
        link.RequestNavigate += OnRequestNavigate;
        result.Add(link);
    }

    private static void OnRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        SafeUrl.Open(e.Uri?.ToString());
        e.Handled = true;
    }

    /// <summary>True when the line is a fenced code-block marker (``` or ~~~, at least three).
    /// When <paramref name="expected"/> is given, only that exact fence char matches (so a
    /// ``` block isn't closed by a ~~~ line).</summary>
    private static bool TryFenceMarker(string line, out char fence, char? expected = null)
    {
        fence = '\0';
        var trimmed = line.TrimStart();
        if (trimmed.Length < 3) return false;

        var c = trimmed[0];
        if (c != '`' && c != '~') return false;
        if (expected is { } e && c != e) return false;

        var run = 0;
        while (run < trimmed.Length && trimmed[run] == c) run++;
        if (run < 3) return false;

        fence = c;
        return true;
    }
}