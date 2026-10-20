using System.Text;

namespace RambiBox.Core.Updates;

public enum MdInlineKind { Text, Bold, Italic, Code, Link }

public sealed record MdInline(MdInlineKind Kind, string Text, string? Url = null);

public enum MdBlockKind { Heading, Bullet, Paragraph, Code }

/// <param name="Level">Heading level (1–6) or bullet nesting depth (0 = top).</param>
public sealed record MdBlock(MdBlockKind Kind, int Level, IReadOnlyList<MdInline> Inlines);

/// <summary>
/// Just enough Markdown for release notes: headings, bullets (- * +), paragraphs, fenced code, **bold**, *italic*,
/// `code`, [links](https://…) and bare https links. Everything else is shown as text. Only https links are kept.
/// </summary>
public static class SimpleMarkdown
{
    public static IReadOnlyList<MdBlock> Parse(string? markdown)
    {
        var blocks = new List<MdBlock>();
        if (string.IsNullOrWhiteSpace(markdown)) return blocks;
        var para = new StringBuilder();
        var code = new StringBuilder();
        bool inCode = false;

        void FlushPara()
        {
            if (para.Length == 0) return;
            blocks.Add(new MdBlock(MdBlockKind.Paragraph, 0, ParseInlines(para.ToString())));
            para.Clear();
        }

        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                if (inCode)
                {
                    blocks.Add(new MdBlock(MdBlockKind.Code, 0, [new MdInline(MdInlineKind.Code, code.ToString().TrimEnd('\n'))]));
                    code.Clear();
                }
                else FlushPara();
                inCode = !inCode;
                continue;
            }
            if (inCode)
            {
                code.Append(line).Append('\n');
                continue;
            }
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0 || trimmed is "---" or "***" or "___")
            {
                FlushPara();
                continue;
            }
            if (trimmed.StartsWith('#'))
            {
                int level = trimmed.TakeWhile(c => c == '#').Count();
                if (level <= 6 && trimmed.Length > level && trimmed[level] == ' ')
                {
                    FlushPara();
                    blocks.Add(new MdBlock(MdBlockKind.Heading, level, ParseInlines(trimmed[(level + 1)..].Trim().TrimEnd('#').Trim())));
                    continue;
                }
            }
            if (trimmed.Length > 2 && trimmed[0] is '-' or '*' or '+' && trimmed[1] == ' ')
            {
                FlushPara();
                int indent = line.Length - trimmed.Length;
                blocks.Add(new MdBlock(MdBlockKind.Bullet, Math.Min(indent / 2, 3), ParseInlines(trimmed[2..].Trim())));
                continue;
            }
            if (para.Length > 0) para.Append(' ');
            para.Append(trimmed);
        }
        if (inCode && code.Length > 0)
            blocks.Add(new MdBlock(MdBlockKind.Code, 0, [new MdInline(MdInlineKind.Code, code.ToString().TrimEnd('\n'))]));
        FlushPara();
        return blocks;
    }

    public static IReadOnlyList<MdInline> ParseInlines(string text)
    {
        var result = new List<MdInline>();
        var sb = new StringBuilder();

        void Flush()
        {
            if (sb.Length == 0) return;
            result.Add(new MdInline(MdInlineKind.Text, sb.ToString()));
            sb.Clear();
        }

        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '\\' && i + 1 < text.Length && "\\`*_[]()#+-.!<>".Contains(text[i + 1]))
            {
                sb.Append(text[i + 1]);
                i += 2;
                continue;
            }
            if ((c == '*' || c == '_') && i + 1 < text.Length && text[i + 1] == c)
            {
                var marker = new string(c, 2);
                int end = text.IndexOf(marker, i + 2, StringComparison.Ordinal);
                if (end > i + 2)
                {
                    Flush();
                    result.Add(new MdInline(MdInlineKind.Bold, text[(i + 2)..end]));
                    i = end + 2;
                    continue;
                }
            }
            if (c == '*' && i + 1 < text.Length && text[i + 1] != ' ')
            {
                int end = text.IndexOf('*', i + 1);
                if (end > i + 1)
                {
                    Flush();
                    result.Add(new MdInline(MdInlineKind.Italic, text[(i + 1)..end]));
                    i = end + 1;
                    continue;
                }
            }
            if (c == '`')
            {
                int end = text.IndexOf('`', i + 1);
                if (end > i)
                {
                    Flush();
                    result.Add(new MdInline(MdInlineKind.Code, text[(i + 1)..end]));
                    i = end + 1;
                    continue;
                }
            }
            if (c == '[')
            {
                int close = text.IndexOf("](", i + 1, StringComparison.Ordinal);
                int end = close < 0 ? -1 : text.IndexOf(')', close + 2);
                if (close > i && end > close)
                {
                    var label = text[(i + 1)..close];
                    var url = text[(close + 2)..end].Trim();
                    Flush();
                    result.Add(IsSafeUrl(url) ? new MdInline(MdInlineKind.Link, label, url) : new MdInline(MdInlineKind.Text, label));
                    i = end + 1;
                    continue;
                }
            }
            if (c == 'h' && string.CompareOrdinal(text, i, "https://", 0, 8) == 0 && (i == 0 || !char.IsLetterOrDigit(text[i - 1])))
            {
                int end = i;
                while (end < text.Length && !char.IsWhiteSpace(text[end]) && text[end] is not ('<' or '>' or '"')) end++;
                while (end > i && text[end - 1] is '.' or ',' or ')' or ';' or ':') end--;
                var url = text[i..end];
                Flush();
                result.Add(new MdInline(MdInlineKind.Link, url, url));
                i = end;
                continue;
            }
            sb.Append(c);
            i++;
        }
        Flush();
        return result;
    }

    private static bool IsSafeUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps;
}
