namespace Momomi.Core.Services;

/// <summary>高亮类别。颜色由 UI 层决定，Core 只负责"这段是什么"。</summary>
public enum YamlTokenKind
{
    /// <summary>普通文本，不着色。</summary>
    Plain,

    /// <summary>注释（# 到行尾）。</summary>
    Comment,

    /// <summary>键名（含修饰符前的裸名）。</summary>
    Key,

    /// <summary>字符串字面量（双引号 / 单引号包裹）。</summary>
    String,

    /// <summary>数字。</summary>
    Number,

    /// <summary>布尔值 / null。</summary>
    Keyword,

    /// <summary>列表项符号 "-"。</summary>
    SequenceMarker,

    /// <summary>锚点 / 别名（&amp;name、*name）。</summary>
    Anchor,
}

/// <summary>一段带类别的高亮范围，[Start, Start+Length)。</summary>
public readonly record struct YamlToken(YamlTokenKind Kind, int Start, int Length)
{
    public int End => Start + Length;
}

/// <summary>
/// 轻量 YAML 词法着色器。
///
/// 刻意不做完整 YAML 解析：编辑器要在每次按键后立刻重算，必须是 O(n) 且无分配压力。
/// 这里只按行扫描，识别引号字符串、注释、键名、列表符号、数字与关键字——
/// 对配置文件的阅读体验已经够用，而且绝不会因为"语法还没写完"而崩掉。
/// </summary>
public static class YamlHighlighter
{
    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "true", "false", "null", "yes", "no", "on", "off", "~",
    };

    /// <summary>对整段文本做着色。</summary>
    public static IReadOnlyList<YamlToken> Tokenize(string text)
    {
        var tokens = new List<YamlToken>();
        if (string.IsNullOrEmpty(text)) return tokens;

        var lineStart = 0;
        for (var i = 0; i <= text.Length; i++)
        {
            if (i == text.Length || text[i] == '\n')
            {
                if (i > lineStart)
                    TokenizeLine(text, lineStart, i, tokens);
                lineStart = i + 1;
            }
        }
        return tokens;
    }

    private static void TokenizeLine(string text, int start, int end, List<YamlToken> tokens)
    {
        var i = start;

        // 行首缩进跳过。
        while (i < end && (text[i] == ' ' || text[i] == '\t' || text[i] == '\r')) i++;

        // 注释行：整行都是注释。
        if (i < end && text[i] == '#')
        {
            tokens.Add(new YamlToken(YamlTokenKind.Comment, i, end - i));
            return;
        }

        // 列表项符号。
        while (i < end && (text[i] == '-' && (i + 1 >= end || text[i + 1] == ' ')))
        {
            tokens.Add(new YamlToken(YamlTokenKind.SequenceMarker, i, 1));
            i++;
            while (i < end && text[i] == ' ') i++;
        }

        // 尝试识别 "key:" —— 必须跟冒号且冒号后是空格或行尾才算键。
        var keyEnd = FindKeyEnd(text, i, end);
        if (keyEnd > i)
        {
            tokens.Add(new YamlToken(YamlTokenKind.Key, i, keyEnd - i));
            i = keyEnd;
            // 跳过冒号与其后空格。
            if (i < end && text[i] == ':') i++;
            while (i < end && text[i] == ' ') i++;
        }

        // 行内剩余部分：值。
        while (i < end)
        {
            var ch = text[i];

            if (ch == '#')
            {
                // 值里的 # 只有在前面是空白时才是注释起点。
                var isCommentStart = i == start || text[i - 1] == ' ' || text[i - 1] == '\t';
                if (isCommentStart)
                {
                    tokens.Add(new YamlToken(YamlTokenKind.Comment, i, end - i));
                    return;
                }
                i++;
                continue;
            }

            if (ch == '"' || ch == '\'')
            {
                var close = FindClosingQuote(text, i, end, ch);
                var length = close - i + 1;
                tokens.Add(new YamlToken(YamlTokenKind.String, i, length));
                i = close + 1;
                continue;
            }

            if (ch == '&' || ch == '*')
            {
                var j = i + 1;
                while (j < end && !char.IsWhiteSpace(text[j]) && text[j] != ',' && text[j] != ']') j++;
                if (j > i + 1)
                {
                    tokens.Add(new YamlToken(YamlTokenKind.Anchor, i, j - i));
                    i = j;
                    continue;
                }
            }

            if (char.IsDigit(ch) || (ch == '-' && i + 1 < end && char.IsDigit(text[i + 1]) && IsValueStart(text, i, start)))
            {
                var j = i + 1;
                while (j < end && (char.IsDigit(text[j]) || text[j] == '.' || text[j] == ':' && j + 1 < end && char.IsDigit(text[j + 1]))) j++;
                tokens.Add(new YamlToken(YamlTokenKind.Number, i, j - i));
                i = j;
                continue;
            }

            if (char.IsLetter(ch) && (i == start || IsValueStart(text, i, start)))
            {
                var j = i;
                while (j < end && (char.IsLetterOrDigit(text[j]) || text[j] == '_' || text[j] == '-')) j++;
                var word = text.Substring(i, j - i);
                if (Keywords.Contains(word))
                    tokens.Add(new YamlToken(YamlTokenKind.Keyword, i, j - i));
                i = j;
                continue;
            }

            i++;
        }
    }

    /// <summary>判断位置 i 是否是一个"值的开头"（前一个是空白或列表符号）。</summary>
    private static bool IsValueStart(string text, int i, int lineStart)
        => i == lineStart || text[i - 1] == ' ' || text[i - 1] == '\t' || text[i - 1] == '-' || text[i - 1] == ':';

    /// <summary>
    /// 找 "key:" 里键名的结束位置（不含冒号）。找不到返回 -1。
    /// </summary>
    private static int FindKeyEnd(string text, int start, int end)
    {
        var i = start;
        var inQuote = '\0';

        while (i < end)
        {
            var ch = text[i];

            if (inQuote != '\0')
            {
                if (ch == inQuote) inQuote = '\0';
                i++;
                continue;
            }

            if (ch == '"' || ch == '\'')
            {
                inQuote = ch;
                i++;
                continue;
            }

            if (ch == '#' && (i == start || text[i - 1] == ' '))
                return -1;

            if (ch == ':')
            {
                // 冒号后必须是空白或行尾，才是键分隔符（避免把 "http://..." 当成键）。
                if (i + 1 >= end || text[i + 1] == ' ' || text[i + 1] == '\r')
                    return i;
                i++;
                continue;
            }

            i++;
        }
        return -1;
    }

    private static int FindClosingQuote(string text, int openIndex, int end, char quote)
    {
        var i = openIndex + 1;
        while (i < end)
        {
            if (text[i] == '\\' && quote == '"')
            {
                i += 2;
                continue;
            }
            // 单引号里 '' 表示转义的单引号。
            if (text[i] == quote)
            {
                if (quote == '\'' && i + 1 < end && text[i + 1] == '\'')
                {
                    i += 2;
                    continue;
                }
                return i;
            }
            i++;
        }
        // 没有闭合：整段当字符串，避免闪烁。
        return end - 1;
    }
}
