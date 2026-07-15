using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Navigation;
using NtfyDesktop.Domain;

namespace NtfyDesktop.Features.Feed.Markdown;

/// <summary>
/// A small, hand-written renderer for the subset of Markdown ntfy bodies use:
/// bold, italic, inline code, fenced code blocks, headings, horizontal rules, blockquotes,
/// lists, tables, and links. Output is a WPF <see cref="FlowDocument"/> containing native
/// block elements (paragraphs, tables, blockquotes, etc.) so the feed can host them in a
/// copy-selectable <see cref="RichTextBox"/>.
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
    /// Builds a FlowDocument representation of the markdown text, for hosting in a RichTextBox so the
    /// message body supports native text selection. Reuses the same inline/fence parsing so
    /// links, emphasis, code spans, and escaping stay in lockstep.
    /// </summary>
    public static FlowDocument RenderFlowDocument(string? text)
    {
        var doc = new FlowDocument
        {
            PagePadding = new Thickness(0),
        };

        // Bind FlowDocument's FontFamily and FontSize to the hosting RichTextBox
        // so that it inherits the application theme's styling instead of reverting to Times New Roman.
        doc.SetBinding(FlowDocument.FontFamilyProperty, new Binding("FontFamily") { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(RichTextBox), 1) });
        doc.SetBinding(FlowDocument.FontSizeProperty, new Binding("FontSize") { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(RichTextBox), 1) });

        if (string.IsNullOrEmpty(text)) return doc;

        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        var i = 0;
        while (i < lines.Length)
        {
            var line = lines[i];

            if (line.Trim().Length == 0) { i++; continue; }

            Block block;

            if (TryHeading(line, out var level, out var headingText))
            {
                block = HeadingBlock(headingText, level);
                i++;
            }
            else if (TryHorizontalRule(line))
            {
                block = HorizontalRuleBlock();
                i++;
            }
            else if (TryBlockquote(line, out var quoteContent))
            {
                var quoteLines = new List<string> { quoteContent };
                i++;
                while (i < lines.Length && TryBlockquote(lines[i], out var nextQuoteContent))
                {
                    quoteLines.Add(nextQuoteContent);
                    i++;
                }
                block = BlockquoteBlock(quoteLines);
            }
            else if (TryListItem(line, out var itemType, out var itemContent, out var itemPrefix))
            {
                block = ListItemBlock(itemType, itemContent, itemPrefix);
                i++;
            }
            else if (IsTableRow(line) && i + 1 < lines.Length && IsTableSeparatorRow(lines[i + 1]))
            {
                var headerCells = SplitTableCells(line);
                var alignments = ParseAlignments(lines[i + 1]);
                var bodyRows = new List<List<string>>();
                
                i += 2; // skip header and separator
                while (i < lines.Length && IsTableRow(lines[i]))
                {
                    bodyRows.Add(SplitTableCells(lines[i]));
                    i++;
                }
                block = TableBlock(headerCells, bodyRows, alignments);
            }
            else if (TryFenceMarker(line, out var fence))
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
                // Paragraph: consecutive non-blank, non-fence, non-heading, non-list, non-blockquote, non-table lines. Soft newlines inside become
                // line breaks (intuitive for short notification bodies).
                var para = new List<string>();
                while (i < lines.Length && lines[i].Trim().Length > 0 && 
                       !TryFenceMarker(lines[i], out _) && 
                       !TryHeading(lines[i], out _, out _) && 
                       !TryHorizontalRule(lines[i]) && 
                       !TryBlockquote(lines[i], out _) && 
                       !TryListItem(lines[i], out _, out _, out _) &&
                       !(IsTableRow(lines[i]) && i + 1 < lines.Length && IsTableSeparatorRow(lines[i + 1])))
                {
                    para.Add(lines[i++]);
                }

                block = ParagraphBlock(para);
            }

            // Align spacing exactly with the original TextBlock margins:
            // First block has no top margin; subsequent blocks are spaced by BlockSpacing.
            if (doc.Blocks.Count > 0)
            {
                var currentMargin = block.Margin;
                block.Margin = new Thickness(currentMargin.Left, BlockSpacing, currentMargin.Right, currentMargin.Bottom);
            }
            else
            {
                // Leave base margins alone
            }

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

            if (TryHeading(lines[i], out _, out var headingText))
            {
                var sbHeading = new StringBuilder();
                AppendPlainInline(sbHeading, headingText);
                output.Add(sbHeading.ToString());
                i++;
                continue;
            }

            if (TryHorizontalRule(lines[i]))
            {
                output.Add("---");
                i++;
                continue;
            }

            if (TryBlockquote(lines[i], out var quoteContent))
            {
                var quoteLines = new List<string> { quoteContent };
                i++;
                while (i < lines.Length && TryBlockquote(lines[i], out var nextQuoteContent))
                {
                    quoteLines.Add(nextQuoteContent);
                    i++;
                }
                output.Add(ToPlainText(string.Join("\n", quoteLines)));
                continue;
            }

            if (TryListItem(lines[i], out var itemType, out var itemContent, out var itemPrefix))
            {
                var marker = itemType switch
                {
                    ListItemType.CheckboxUnchecked => "[ ] ",
                    ListItemType.CheckboxChecked => "[x] ",
                    ListItemType.Numbered => itemPrefix + " ",
                    _ => "• "
                };
                var sbItem = new StringBuilder();
                AppendPlainInline(sbItem, itemContent);
                output.Add(marker + sbItem.ToString());
                i++;
                continue;
            }

            if (IsTableRow(lines[i]) && i + 1 < lines.Length && IsTableSeparatorRow(lines[i + 1]))
            {
                var headerCells = SplitTableCells(lines[i]);
                var sbTable = new StringBuilder();
                sbTable.AppendLine(string.Join("\t", headerCells.Select(c => {
                    var s = new StringBuilder();
                    AppendPlainInline(s, c);
                    return s.ToString();
                })));
                
                i += 2; // skip header and separator
                while (i < lines.Length && IsTableRow(lines[i]))
                {
                    var cells = SplitTableCells(lines[i]);
                    sbTable.AppendLine(string.Join("\t", cells.Select(c => {
                        var s = new StringBuilder();
                        AppendPlainInline(s, c);
                        return s.ToString();
                    })));
                    i++;
                }
                output.Add(sbTable.ToString().TrimEnd());
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

            if (c == '~' && TryStrikethrough(text, i, out var innerText, out var afterStrike))
            {
                AppendPlainInline(sb, innerText);
                i = afterStrike;
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

    private static bool TryHeading(string line, out int level, out string text)
    {
        level = 0;
        text = string.Empty;

        var trimmed = line.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] != '#') return false;

        var hashCount = 0;
        while (hashCount < trimmed.Length && trimmed[hashCount] == '#') hashCount++;

        if (hashCount > 0 && hashCount <= 6 && hashCount < trimmed.Length && trimmed[hashCount] == ' ')
        {
            level = hashCount;
            text = trimmed[hashCount..].Trim();
            return true;
        }

        return false;
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

            if (c == '~' && TryStrikethrough(text, i, out var innerText, out var afterStrike))
            {
                Flush();
                var span = new Span();
                span.TextDecorations = TextDecorations.Strikethrough;
                foreach (var innerInline in ParseInlines(innerText))
                    span.Inlines.Add(innerInline);
                result.Add(span);
                i = afterStrike;
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

    private static bool TryStrikethrough(string text, int start, out string inner, out int after)
    {
        inner = string.Empty;
        after = start;

        var n = text.Length;
        if (start + 1 >= n || text[start] != '~' || text[start + 1] != '~') return false;

        var contentStart = start + 2;
        if (contentStart >= n || char.IsWhiteSpace(text[contentStart])) return false;

        var j = contentStart;
        while (j < n)
        {
            if (j + 1 < n && text[j] == '~' && text[j + 1] == '~')
            {
                if (j > contentStart && !char.IsWhiteSpace(text[j - 1]))
                {
                    inner = text.Substring(contentStart, j - contentStart);
                    after = j + 2;
                    return inner.Length > 0;
                }
            }
            j++;
        }

        return false;
    }

    private static bool TryHorizontalRule(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length < 3) return false;

        var c = trimmed[0];
        if (c != '-' && c != '*' && c != '_') return false;

        foreach (var ch in trimmed)
        {
            if (ch != c && ch != ' ') return false;
        }
        return true;
    }

    private static BlockUIContainer HorizontalRuleBlock()
    {
        var border = new Border
        {
            Height = 1,
            Margin = new Thickness(0, 8, 0, 8),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        border.SetResourceReference(Border.BackgroundProperty, "ControlStrokeColorDefaultBrush");
        return new BlockUIContainer(border);
    }

    private static bool TryBlockquote(string line, out string content)
    {
        content = string.Empty;
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith(">"))
        {
            var len = 1;
            while (len < trimmed.Length && trimmed[len] == '>') len++;
            if (len < trimmed.Length && trimmed[len] == ' ') len++;
            content = trimmed[len..];
            return true;
        }
        return false;
    }

    private static Table BlockquoteBlock(List<string> quoteLines)
    {
        var table = new Table
        {
            Margin = new Thickness(4, 4, 0, 4),
            CellSpacing = 0
        };
        table.Columns.Add(new TableColumn());
        
        var rowGroup = new TableRowGroup();
        var row = new TableRow();
        var cell = new TableCell();
        cell.BorderThickness = new Thickness(3, 0, 0, 0);
        cell.SetResourceReference(TableCell.BorderBrushProperty, "AccentTextFillColorPrimaryBrush");
        cell.Padding = new Thickness(12, 4, 0, 4);

        var quoteText = string.Join("\n", quoteLines);
        var subDoc = RenderFlowDocument(quoteText);
        
        while (subDoc.Blocks.Count > 0)
        {
            var b = subDoc.Blocks.FirstBlock;
            subDoc.Blocks.Remove(b);
            cell.Blocks.Add(b);
        }

        row.Cells.Add(cell);
        rowGroup.Rows.Add(row);
        table.RowGroups.Add(rowGroup);

        return table;
    }

    private enum ListItemType
    {
        None,
        Bullet,
        Numbered,
        CheckboxUnchecked,
        CheckboxChecked
    }

    private static bool TryListItem(string line, out ListItemType type, out string content, out string prefix)
    {
        type = ListItemType.None;
        content = string.Empty;
        prefix = string.Empty;

        var trimmed = line.TrimStart();
        if (trimmed.Length == 0) return false;

        if (trimmed.StartsWith("- [ ] ") || trimmed.StartsWith("* [ ] ") || trimmed.StartsWith("+ [ ] "))
        {
            type = ListItemType.CheckboxUnchecked;
            content = trimmed[6..];
            return true;
        }
        if (trimmed.StartsWith("- [x] ") || trimmed.StartsWith("* [x] ") || trimmed.StartsWith("+ [x] ") ||
            trimmed.StartsWith("- [X] ") || trimmed.StartsWith("* [X] ") || trimmed.StartsWith("+ [X] "))
        {
            type = ListItemType.CheckboxChecked;
            content = trimmed[6..];
            return true;
        }

        if (trimmed.StartsWith("- ") || trimmed.StartsWith("* ") || trimmed.StartsWith("+ "))
        {
            type = ListItemType.Bullet;
            content = trimmed[2..];
            return true;
        }

        var dotIndex = trimmed.IndexOf(". ");
        if (dotIndex > 0 && dotIndex < 10)
        {
            var numPart = trimmed[..dotIndex];
            if (int.TryParse(numPart, out _))
            {
                type = ListItemType.Numbered;
                content = trimmed[(dotIndex + 2)..];
                prefix = numPart + ".";
                return true;
            }
        }

        return false;
    }

    private static Paragraph ListItemBlock(ListItemType type, string content, string prefix)
    {
        var p = new Paragraph
        {
            Margin = new Thickness(28, 2, 0, 2),
            TextIndent = -28
        };
        p.SetResourceReference(TextElement.ForegroundProperty, PrimaryTextBrush);

        if (type == ListItemType.CheckboxUnchecked || type == ListItemType.CheckboxChecked)
        {
            var cb = new System.Windows.Controls.CheckBox
            {
                IsChecked = type == ListItemType.CheckboxChecked,
                IsHitTestVisible = false,
                Focusable = false,
                VerticalAlignment = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Left,
                MinWidth = 20,
                MinHeight = 20,
                Width = 20,
                Height = 20,
                Padding = new Thickness(0),
                Margin = new Thickness(2, 1, 6, 0)
            };
            p.Inlines.Add(new InlineUIContainer(cb));
        }
        else
        {
            var prefixText = type == ListItemType.Numbered ? prefix : "•";
            var runPrefix = new Run(prefixText) { FontWeight = FontWeights.Bold };
            p.Inlines.Add(runPrefix);
            p.Inlines.Add(new Run(" ")); // space after bullet
        }

        foreach (var inline in ParseInlines(content))
            p.Inlines.Add(inline);

        return p;
    }

    private static bool IsTableRow(string line)
    {
        var trimmed = line.Trim();
        return trimmed.StartsWith("|") && trimmed.EndsWith("|") && trimmed.Length > 1;
    }

    private static bool IsTableSeparatorRow(string line)
    {
        var trimmed = line.Trim();
        if (!trimmed.StartsWith("|") || !trimmed.EndsWith("|") || trimmed.Length <= 2) return false;
        var content = trimmed[1..^1].Replace(" ", "").Replace("-", "").Replace(":", "").Replace("|", "");
        return content.Length == 0;
    }

    private static List<string> SplitTableCells(string row)
    {
        var trimmed = row.Trim();
        if (trimmed.StartsWith("|")) trimmed = trimmed[1..];
        if (trimmed.EndsWith("|")) trimmed = trimmed[..^1];

        return trimmed.Split('|').Select(c => c.Trim()).ToList();
    }

    private static List<HorizontalAlignment> ParseAlignments(string separatorRow)
    {
        var cells = SplitTableCells(separatorRow);
        var alignments = new List<HorizontalAlignment>();
        foreach (var cell in cells)
        {
            var left = cell.StartsWith(":");
            var right = cell.EndsWith(":");
            if (left && right)
                alignments.Add(HorizontalAlignment.Center);
            else if (right)
                alignments.Add(HorizontalAlignment.Right);
            else
                alignments.Add(HorizontalAlignment.Left);
        }
        return alignments;
    }

    private static BlockUIContainer TableBlock(List<string> headerCells, List<List<string>> bodyRows, List<HorizontalAlignment> alignments)
    {
        var colCount = headerCells.Count;
        var rowCount = 1 + bodyRows.Count;

        var grid = new Grid
        {
            Margin = new Thickness(0, 4, 0, 4),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        for (var c = 0; c < colCount; c++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        for (var r = 0; r < rowCount; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        for (var c = 0; c < colCount; c++)
        {
            var border = new Border
            {
                Padding = new Thickness(8, 6, 8, 6),
                BorderThickness = new Thickness(0, 0, 0, 2)
            };
            border.SetResourceReference(Border.BorderBrushProperty, "ControlStrokeColorDefaultBrush");
            border.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");

            var align = c < alignments.Count ? alignments[c] : HorizontalAlignment.Left;
            var tb = new Wpf.Ui.Controls.TextBlock
            {
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = align,
                TextWrapping = TextWrapping.Wrap
            };
            tb.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, PrimaryTextBrush);
            foreach (var inline in ParseInlines(headerCells[c]))
                tb.Inlines.Add(inline);

            border.Child = tb;
            Grid.SetRow(border, 0);
            Grid.SetColumn(border, c);
            grid.Children.Add(border);
        }

        for (var r = 0; r < bodyRows.Count; r++)
        {
            var cells = bodyRows[r];
            for (var c = 0; c < colCount; c++)
            {
                var border = new Border
                {
                    Padding = new Thickness(8, 6, 8, 6),
                    BorderThickness = new Thickness(0, 0, 0, 1)
                };
                border.SetResourceReference(Border.BorderBrushProperty, "ControlStrokeColorDefaultBrush");

                if (r % 2 != 0)
                {
                    border.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");
                }

                var cellText = c < cells.Count ? cells[c] : string.Empty;
                var align = c < alignments.Count ? alignments[c] : HorizontalAlignment.Left;
                var tb = new Wpf.Ui.Controls.TextBlock
                {
                    HorizontalAlignment = align,
                    TextWrapping = TextWrapping.Wrap
                };
                tb.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, PrimaryTextBrush);
                foreach (var inline in ParseInlines(cellText))
                    tb.Inlines.Add(inline);

                border.Child = tb;
                Grid.SetRow(border, r + 1);
                Grid.SetColumn(border, c);
                grid.Children.Add(border);
            }
        }

        var outerBorder = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Child = grid,
            Margin = new Thickness(0, 6, 0, 6)
        };
        outerBorder.SetResourceReference(Border.BorderBrushProperty, "ControlStrokeColorDefaultBrush");

        return new BlockUIContainer(outerBorder);
    }

    private static Paragraph HeadingBlock(string text, int level)
    {
        var p = new Paragraph();
        p.SetResourceReference(TextElement.ForegroundProperty, PrimaryTextBrush);
        p.Margin = new Thickness(0, 8, 0, 4);
        
        switch (level)
        {
            case 1:
                p.FontSize = 20;
                p.FontWeight = FontWeights.Bold;
                break;
            case 2:
                p.FontSize = 18;
                p.FontWeight = FontWeights.Bold;
                break;
            case 3:
                p.FontSize = 16;
                p.FontWeight = FontWeights.SemiBold;
                break;
            case 4:
                p.FontSize = 14;
                p.FontWeight = FontWeights.SemiBold;
                break;
            default:
                p.FontSize = 12;
                p.FontWeight = FontWeights.Normal;
                break;
        }

        foreach (var inline in ParseInlines(text))
            p.Inlines.Add(inline);

        return p;
    }
}
