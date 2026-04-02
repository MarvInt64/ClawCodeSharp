using System.Text;
using System.Text.RegularExpressions;

namespace CodeSharp.Cli;

internal static partial class TerminalMarkdown
{
    public static IReadOnlyList<string> Render(string markdown, int maxWidth)
    {
        var normalized = string.IsNullOrWhiteSpace(markdown)
            ? "(no output)"
            : markdown.TrimEnd().Replace("\r\n", "\n");

        var lines = normalized.Split('\n');
        var rendered = new List<string>();

        for (var i = 0; i < lines.Length; i++)
        {
            var rawLine = lines[i];
            var trimmed = rawLine.Trim();

            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                var language = trimmed[3..].Trim();
                var blockLines = new List<string>();

                while (++i < lines.Length && !lines[i].Trim().StartsWith("```", StringComparison.Ordinal))
                {
                    blockLines.Add(lines[i]);
                }

                rendered.AddRange(RenderCodeBlock(language, blockLines, maxWidth));
                continue;
            }

            if (LooksLikeTableHeader(lines, i))
            {
                var tableLines = new List<string> { lines[i], lines[i + 1] };
                i += 2;
                while (i < lines.Length && LooksLikeTableRow(lines[i]))
                {
                    tableLines.Add(lines[i]);
                    i++;
                }

                i--;
                rendered.AddRange(RenderTable(tableLines, maxWidth));
                continue;
            }

            if (string.IsNullOrWhiteSpace(trimmed))
            {
                rendered.Add(string.Empty);
                continue;
            }

            if (IsHorizontalRule(trimmed))
            {
                rendered.Add(ConsoleUi.Muted(new string('─', Math.Min(maxWidth, 48))));
                continue;
            }

            if (TryParseHeading(rawLine, out var level, out var heading))
            {
                rendered.AddRange(RenderHeading(level, heading, maxWidth));
                continue;
            }

            if (TryParseQuote(rawLine, out var quote))
            {
                rendered.AddRange(RenderWrapped(quote, maxWidth, "▎ ", "▎ ", ConsoleUi.Muted, RenderInline));
                continue;
            }

            if (TryParseList(rawLine, out var indent, out var marker, out var content))
            {
                var prefix = $"{new string(' ', indent)}{NormalizeListMarker(marker)} ";
                var continuation = new string(' ', prefix.Length);
                rendered.AddRange(RenderWrapped(content, maxWidth, prefix, continuation, ConsoleUi.Accent, RenderInline));
                continue;
            }

            rendered.AddRange(WrapText(rawLine.Trim(), maxWidth).Select(RenderInline));
        }

        TrimEmptyLines(rendered);
        return rendered;
    }

    private static IReadOnlyList<string> RenderHeading(int level, string heading, int maxWidth)
    {
        var wrapped = WrapText(heading, maxWidth);
        return level switch
        {
            1 => wrapped.Select(part => ConsoleUi.Strong(ConsoleUi.Brand(part))).ToList(),
            2 => wrapped.Select(part => ConsoleUi.Strong(ConsoleUi.Accent(part))).ToList(),
            _ => RenderWrapped(heading, maxWidth, "• ", "  ", ConsoleUi.Accent, RenderInline)
        };
    }

    private static IReadOnlyList<string> RenderCodeBlock(string language, IReadOnlyList<string> lines, int maxWidth)
    {
        var rendered = new List<string>();
        if (!string.IsNullOrWhiteSpace(language))
        {
            rendered.Add(ConsoleUi.Muted($"[{language}]"));
        }

        if (language is "diff" or "patch")
        {
            rendered.AddRange(RenderDiffLines(lines, maxWidth));
            return rendered;
        }

        foreach (var line in lines.DefaultIfEmpty(string.Empty))
        {
            rendered.AddRange(WrapCodeLine(line, language, maxWidth, "  │ "));
        }

        return rendered;
    }

    private static IEnumerable<string> RenderDiffLines(IEnumerable<string> lines, int maxWidth)
    {
        foreach (var line in lines.DefaultIfEmpty(string.Empty))
        {
            var prefix = "  │ ";
            Func<string, string> painter = line switch
            {
                _ when line.StartsWith('+') && !line.StartsWith("+++") => ConsoleUi.Success,
                _ when line.StartsWith('-') && !line.StartsWith("---") => ConsoleUi.Error,
                _ when line.StartsWith("@@") => ConsoleUi.Muted,
                _ when line.StartsWith("diff ") || line.StartsWith("index ") || line.StartsWith("---") || line.StartsWith("+++") => ConsoleUi.Accent,
                _ => ConsoleUi.Code
            };

            foreach (var wrapped in HardWrap(line, Math.Max(8, maxWidth - prefix.Length)))
            {
                yield return prefix + painter(wrapped);
            }
        }
    }

    private static IReadOnlyList<string> RenderTable(IReadOnlyList<string> lines, int maxWidth)
    {
        var rows = lines.Select(ParseTableRow).ToList();
        if (rows.Count < 2)
        {
            return lines.Select(RenderInline).ToList();
        }

        rows.RemoveAt(1);
        var columnCount = rows.Max(r => r.Count);
        foreach (var row in rows)
        {
            while (row.Count < columnCount)
            {
                row.Add(string.Empty);
            }
        }

        var widths = Enumerable.Range(0, columnCount)
            .Select(index => rows.Max(row => VisibleWidth(row[index])))
            .ToArray();

        var totalWidth = widths.Sum() + ((columnCount - 1) * 3);
        if (totalWidth > maxWidth)
        {
            return RenderCompactTable(rows);
        }

        var rendered = new List<string>();
        rendered.Add(RenderTableRow(rows[0], widths, isHeader: true));
        rendered.Add(ConsoleUi.Muted(string.Join("─┼─", widths.Select(width => new string('─', width)))));

        for (var i = 1; i < rows.Count; i++)
        {
            rendered.Add(RenderTableRow(rows[i], widths, isHeader: false));
        }

        return rendered;
    }

    private static IReadOnlyList<string> RenderCompactTable(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var header = rows[0];
        var rendered = new List<string>();

        for (var i = 1; i < rows.Count; i++)
        {
            var row = rows[i];
            var parts = new List<string>();
            for (var j = 0; j < header.Count && j < row.Count; j++)
            {
                if (string.IsNullOrWhiteSpace(row[j]))
                {
                    continue;
                }

                parts.Add($"{ConsoleUi.Strong(header[j])}: {RenderInline(row[j])}");
            }

            if (parts.Count > 0)
            {
                rendered.Add($"• {string.Join("  ", parts)}");
            }
        }

        return rendered;
    }

    private static string RenderTableRow(IReadOnlyList<string> cells, IReadOnlyList<int> widths, bool isHeader)
    {
        var parts = new List<string>();
        for (var i = 0; i < cells.Count; i++)
        {
            var padded = cells[i].PadRight(widths[i]);
            parts.Add(isHeader ? ConsoleUi.Strong(padded) : padded);
        }

        return string.Join(ConsoleUi.Muted(" │ "), parts.Select(RenderInline));
    }

    private static List<string> ParseTableRow(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.StartsWith('|'))
        {
            trimmed = trimmed[1..];
        }

        if (trimmed.EndsWith('|'))
        {
            trimmed = trimmed[..^1];
        }

        return trimmed.Split('|').Select(cell => cell.Trim()).ToList();
    }

    private static bool LooksLikeTableHeader(IReadOnlyList<string> lines, int index) =>
        index + 1 < lines.Count &&
        LooksLikeTableRow(lines[index]) &&
        TableSeparatorRegex().IsMatch(lines[index + 1]);

    private static bool LooksLikeTableRow(string line)
    {
        var trimmed = line.Trim();
        return !string.IsNullOrWhiteSpace(trimmed) && trimmed.Contains('|');
    }

    private static IReadOnlyList<string> RenderWrapped(
        string text,
        int maxWidth,
        string firstPrefix,
        string continuationPrefix,
        Func<string, string> prefixPainter,
        Func<string, string> contentRenderer)
    {
        var wrapped = WrapText(text.Trim(), Math.Max(8, maxWidth - firstPrefix.Length), Math.Max(8, maxWidth - continuationPrefix.Length));
        var rendered = new List<string>();
        for (var i = 0; i < wrapped.Count; i++)
        {
            var prefix = i == 0 ? firstPrefix : continuationPrefix;
            rendered.Add(prefixPainter(prefix) + contentRenderer(wrapped[i]));
        }

        return rendered;
    }

    private static IReadOnlyList<string> WrapCodeLine(string line, string language, int maxWidth, string prefix)
    {
        var width = Math.Max(8, maxWidth - prefix.Length);
        return HardWrap(line, width)
            .Select(part => prefix + HighlightCode(language, part))
            .ToList();
    }

    private static string HighlightCode(string language, string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return ConsoleUi.Code(text);
        }

        return NormalizeLanguage(language) switch
        {
            "csharp" => HighlightByPattern(text, CSharpTokenRegex(), token => token switch
            {
                _ when token.StartsWith("//", StringComparison.Ordinal) => ConsoleUi.CodeComment(token),
                _ when token.StartsWith("\"", StringComparison.Ordinal) || token.StartsWith("@\"", StringComparison.Ordinal) || token.StartsWith("'", StringComparison.Ordinal) => ConsoleUi.CodeString(token),
                _ when CSharpTypeRegex().IsMatch(token) => ConsoleUi.CodeType(token),
                _ when CSharpKeywordRegex().IsMatch(token) => ConsoleUi.CodeKeyword(token),
                _ when NumberRegex().IsMatch(token) => ConsoleUi.CodeNumber(token),
                _ => ConsoleUi.Code(token)
            }),
            "json" => HighlightByPattern(text, JsonTokenRegex(), token => token switch
            {
                _ when token.StartsWith("\"", StringComparison.Ordinal) => ConsoleUi.CodeString(token),
                _ when JsonKeywordRegex().IsMatch(token) => ConsoleUi.CodeKeyword(token),
                _ when NumberRegex().IsMatch(token) => ConsoleUi.CodeNumber(token),
                _ when JsonSymbolRegex().IsMatch(token) => ConsoleUi.CodeSymbol(token),
                _ => ConsoleUi.Code(token)
            }),
            "bash" => HighlightByPattern(text, BashTokenRegex(), token => token switch
            {
                _ when token.StartsWith("#", StringComparison.Ordinal) => ConsoleUi.CodeComment(token),
                _ when token.StartsWith("\"", StringComparison.Ordinal) || token.StartsWith("'", StringComparison.Ordinal) => ConsoleUi.CodeString(token),
                _ when token.StartsWith("$", StringComparison.Ordinal) => ConsoleUi.CodeType(token),
                _ when BashKeywordRegex().IsMatch(token) => ConsoleUi.CodeKeyword(token),
                _ when NumberRegex().IsMatch(token) => ConsoleUi.CodeNumber(token),
                _ => ConsoleUi.Code(token)
            }),
            _ => ConsoleUi.Code(text)
        };
    }

    private static string HighlightByPattern(string text, Regex pattern, Func<string, string> colorize)
    {
        var builder = new StringBuilder();
        var lastIndex = 0;

        foreach (Match match in pattern.Matches(text))
        {
            if (match.Index > lastIndex)
            {
                builder.Append(ConsoleUi.Code(text[lastIndex..match.Index]));
            }

            builder.Append(colorize(match.Value));
            lastIndex = match.Index + match.Length;
        }

        if (lastIndex < text.Length)
        {
            builder.Append(ConsoleUi.Code(text[lastIndex..]));
        }

        return builder.ToString();
    }

    private static string NormalizeLanguage(string language) => language.Trim().ToLowerInvariant() switch
    {
        "cs" => "csharp",
        "sh" => "bash",
        "shell" => "bash",
        _ => language.Trim().ToLowerInvariant()
    };

    private static IReadOnlyList<string> WrapText(string text, int firstWidth, int? continuationWidth = null)
    {
        var words = WhitespaceRegex().Split(text.Trim()).Where(part => part.Length > 0).ToArray();
        if (words.Length == 0)
        {
            return [string.Empty];
        }

        var wrapped = new List<string>();
        var line = new StringBuilder();
        var width = firstWidth;
        var nextWidth = continuationWidth ?? firstWidth;

        foreach (var word in words)
        {
            if (VisibleWidth(word) > width)
            {
                if (line.Length > 0)
                {
                    wrapped.Add(line.ToString());
                    line.Clear();
                    width = nextWidth;
                }

                var chunks = HardWrap(word, width);
                wrapped.AddRange(chunks.Take(Math.Max(0, chunks.Count - 1)));
                line.Append(chunks.Last());
                width = nextWidth;
                continue;
            }

            var candidate = line.Length == 0 ? word : $"{line} {word}";
            if (VisibleWidth(candidate) <= width)
            {
                if (line.Length > 0)
                {
                    line.Append(' ');
                }

                line.Append(word);
            }
            else
            {
                wrapped.Add(line.ToString());
                line.Clear();
                line.Append(word);
                width = nextWidth;
            }
        }

        if (line.Length > 0)
        {
            wrapped.Add(line.ToString());
        }

        return wrapped;
    }

    private static IReadOnlyList<string> HardWrap(string text, int width)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [string.Empty];
        }

        var wrapped = new List<string>();
        var offset = 0;

        while (offset < text.Length)
        {
            var length = Math.Min(width, text.Length - offset);
            wrapped.Add(text.Substring(offset, length));
            offset += length;
        }

        return wrapped;
    }

    private static int VisibleWidth(string text) => StripMarkdownSyntax(text).Length;

    private static string StripMarkdownSyntax(string text)
    {
        var stripped = LinkRegex().Replace(text, "$1");
        stripped = stripped.Replace("**", string.Empty)
            .Replace("__", string.Empty)
            .Replace("`", string.Empty)
            .Replace('*', ' ')
            .Replace('_', ' ');
        return WhitespaceRegex().Replace(stripped, " ").Trim();
    }

    private static bool IsHorizontalRule(string line) =>
        line.Length >= 3 && line.All(ch => ch == '-' || ch == '*' || ch == '_');

    private static bool TryParseHeading(string line, out int level, out string text)
    {
        var match = HeadingRegex().Match(line);
        if (!match.Success)
        {
            level = 0;
            text = string.Empty;
            return false;
        }

        level = match.Groups[1].Value.Length;
        text = match.Groups[2].Value.Trim();
        return true;
    }

    private static bool TryParseQuote(string line, out string text)
    {
        var match = QuoteRegex().Match(line);
        if (!match.Success)
        {
            text = string.Empty;
            return false;
        }

        text = match.Groups[1].Value;
        return true;
    }

    private static bool TryParseList(string line, out int indent, out string marker, out string content)
    {
        var match = ListRegex().Match(line);
        if (!match.Success)
        {
            indent = 0;
            marker = string.Empty;
            content = string.Empty;
            return false;
        }

        indent = match.Groups[1].Value.Length;
        marker = match.Groups[2].Value;
        content = match.Groups[3].Value;
        return true;
    }

    private static string NormalizeListMarker(string marker) =>
        marker is "-" or "*" or "+" ? "•" : marker;

    private static string RenderInline(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var rendered = LinkRegex().Replace(text, match =>
            $"{match.Groups[1].Value} {ConsoleUi.Muted($"<{match.Groups[2].Value}>")}");

        rendered = InlineCodeRegex().Replace(rendered, match => ConsoleUi.Code($" {match.Groups[1].Value} "));
        rendered = BoldAsteriskRegex().Replace(rendered, match => ConsoleUi.Strong(match.Groups[1].Value));
        rendered = BoldUnderscoreRegex().Replace(rendered, match => ConsoleUi.Strong(match.Groups[1].Value));
        rendered = ItalicAsteriskRegex().Replace(rendered, match => match.Groups[1].Value);
        rendered = ItalicUnderscoreRegex().Replace(rendered, match => match.Groups[1].Value);

        return rendered;
    }

    private static void TrimEmptyLines(List<string> lines)
    {
        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[0]))
        {
            lines.RemoveAt(0);
        }

        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1]))
        {
            lines.RemoveAt(lines.Count - 1);
        }
    }

    [GeneratedRegex(@"^(#{1,6})\s+(.*)$")]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"^\s*>\s?(.*)$")]
    private static partial Regex QuoteRegex();

    [GeneratedRegex(@"^(\s*)([-+*]|\d+[.)])\s+(.*)$")]
    private static partial Regex ListRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"\[([^\]]+)\]\(([^)]+)\)")]
    private static partial Regex LinkRegex();

    [GeneratedRegex(@"^\s*\|?\s*:?-+:?\s*(\|\s*:?-+:?\s*)+\|?\s*$")]
    private static partial Regex TableSeparatorRegex();

    [GeneratedRegex(@"`([^`]+)`")]
    private static partial Regex InlineCodeRegex();

    [GeneratedRegex(@"//.*|@?""(?:[^""\\]|\\.)*""|'(?:[^'\\]|\\.)*'|\b(?:abstract|as|async|await|base|break|case|catch|class|const|continue|default|do|else|enum|event|explicit|extern|false|finally|for|foreach|if|implicit|in|interface|internal|is|lock|namespace|new|null|operator|out|override|params|private|protected|public|readonly|record|required|return|sealed|static|struct|switch|this|throw|true|try|typeof|using|var|virtual|void|while|get|set|init)\b|\b(?:bool|byte|char|decimal|double|float|int|long|object|sbyte|short|string|uint|ulong|ushort)\b|\b\d+(?:\.\d+)?\b")]
    private static partial Regex CSharpTokenRegex();

    [GeneratedRegex(@"^(?:bool|byte|char|decimal|double|float|int|long|object|sbyte|short|string|uint|ulong|ushort)$")]
    private static partial Regex CSharpTypeRegex();

    [GeneratedRegex(@"^(?:abstract|as|async|await|base|break|case|catch|class|const|continue|default|do|else|enum|event|explicit|extern|false|finally|for|foreach|if|implicit|in|interface|internal|is|lock|namespace|new|null|operator|out|override|params|private|protected|public|readonly|record|required|return|sealed|static|struct|switch|this|throw|true|try|typeof|using|var|virtual|void|while|get|set|init)$")]
    private static partial Regex CSharpKeywordRegex();

    [GeneratedRegex(@"""(?:[^""\\]|\\.)*""|\b(?:true|false|null)\b|\b\d+(?:\.\d+)?\b|[{}\[\]:,]")]
    private static partial Regex JsonTokenRegex();

    [GeneratedRegex(@"^(?:true|false|null)$")]
    private static partial Regex JsonKeywordRegex();

    [GeneratedRegex(@"^[{}\[\]:,]$")]
    private static partial Regex JsonSymbolRegex();

    [GeneratedRegex(@"#.*|""(?:[^""\\]|\\.)*""|'(?:[^'\\]|\\.)*'|\$\{?[A-Za-z_][A-Za-z0-9_]*\}?|\b(?:if|then|else|elif|fi|for|while|do|done|case|esac|function|in|local|export|return)\b|\b\d+\b")]
    private static partial Regex BashTokenRegex();

    [GeneratedRegex(@"^(?:if|then|else|elif|fi|for|while|do|done|case|esac|function|in|local|export|return)$")]
    private static partial Regex BashKeywordRegex();

    [GeneratedRegex(@"^\d+(?:\.\d+)?$")]
    private static partial Regex NumberRegex();

    [GeneratedRegex(@"\*\*([^*]+)\*\*")]
    private static partial Regex BoldAsteriskRegex();

    [GeneratedRegex(@"__([^_]+)__")]
    private static partial Regex BoldUnderscoreRegex();

    [GeneratedRegex(@"(?<!\*)\*([^*]+)\*(?!\*)")]
    private static partial Regex ItalicAsteriskRegex();

    [GeneratedRegex(@"(?<!_)_([^_]+)_(?!_)")]
    private static partial Regex ItalicUnderscoreRegex();
}
