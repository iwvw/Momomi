namespace Momomi.Core.Services;

/// <summary>差异行的类型。</summary>
public enum DiffLineKind
{
    /// <summary>两侧一致。</summary>
    Same,

    /// <summary>只在合并结果里出现（覆写新增的行）。</summary>
    Added,

    /// <summary>只在原配置里出现（被覆写删除或替换掉的行）。</summary>
    Removed,
}

/// <summary>一行差异。</summary>
/// <param name="Kind">行类型。</param>
/// <param name="Text">该行内容（不含换行符）。</param>
/// <param name="SourceLine">在原配置中的行号（从 1 开始）；新增行为 0。</param>
/// <param name="ResultLine">在合并结果中的行号（从 1 开始）；删除行为 0。</param>
public sealed record DiffLine(DiffLineKind Kind, string Text, int SourceLine, int ResultLine);

/// <summary>
/// 行级文本差异，用于展示"覆写到底改了什么"。
///
/// 用的是 LCS（最长公共子序列）动态规划，对本场景足够：runtime.yaml 通常几百到
/// 一两千行，O(n×m) 的内存在高亮场景里可以接受。超过 <see cref="MaxCellCount"/>
/// 时退化为"整体替换"的粗略结果，避免极端情况下卡住界面。
/// </summary>
public static class LineDiff
{
    /// <summary>
    /// DP 表的单元格上限，超过则跳过精细比对。
    /// 5000 行 × 5000 行 ≈ 25M 单元格（100 MB int 数组），仍在可接受范围；
    /// 这里放到 4000 万，覆盖 6000 行左右的真实配置。再大就退化为整体替换。
    /// </summary>
    private const long MaxCellCount = 40_000_000;

    /// <summary>计算两份文本的行级差异。</summary>
    public static IReadOnlyList<DiffLine> Compute(string source, string result)
    {
        var a = SplitLines(source);
        var b = SplitLines(result);
        return Compute(a, b);
    }

    public static IReadOnlyList<DiffLine> Compute(IReadOnlyList<string> source, IReadOnlyList<string> result)
    {
        var n = source.Count;
        var m = result.Count;

        if (n == 0 && m == 0) return Array.Empty<DiffLine>();
        if (n == 0) return AllMarked(result, DiffLineKind.Added);
        if (m == 0) return AllMarked(source, DiffLineKind.Removed);

        // 极端规模直接放弃精细比对：给出"原文全部删除 + 结果全部新增"。
        if ((long)(n + 1) * (m + 1) > MaxCellCount)
        {
            var coarse = new List<DiffLine>(n + m);
            for (var i = 0; i < n; i++) coarse.Add(new DiffLine(DiffLineKind.Removed, source[i], i + 1, 0));
            for (var j = 0; j < m; j++) coarse.Add(new DiffLine(DiffLineKind.Added, result[j], 0, j + 1));
            return coarse;
        }

        // lcs[i, j] = source[i..] 与 result[j..] 的最长公共子序列长度。
        var width = m + 1;
        var lcs = new int[(n + 1) * width];
        for (var i = n - 1; i >= 0; i--)
        {
            var row = i * width;
            var nextRow = (i + 1) * width;
            for (var j = m - 1; j >= 0; j--)
            {
                lcs[row + j] = string.Equals(source[i], result[j], StringComparison.Ordinal)
                    ? lcs[nextRow + j + 1] + 1
                    : Math.Max(lcs[nextRow + j], lcs[row + j + 1]);
            }
        }

        var lines = new List<DiffLine>(Math.Max(n, m));
        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (string.Equals(source[x], result[y], StringComparison.Ordinal))
            {
                lines.Add(new DiffLine(DiffLineKind.Same, source[x], x + 1, y + 1));
                x++;
                y++;
            }
            else if (lcs[(x + 1) * width + y] >= lcs[x * width + y + 1])
            {
                lines.Add(new DiffLine(DiffLineKind.Removed, source[x], x + 1, 0));
                x++;
            }
            else
            {
                lines.Add(new DiffLine(DiffLineKind.Added, result[y], 0, y + 1));
                y++;
            }
        }
        while (x < n) { lines.Add(new DiffLine(DiffLineKind.Removed, source[x], x + 1, 0)); x++; }
        while (y < m) { lines.Add(new DiffLine(DiffLineKind.Added, result[y], 0, y + 1)); y++; }

        return lines;
    }

    /// <summary>统计新增/删除行数，供页头显示摘要。</summary>
    public static (int Added, int Removed) Summarize(IEnumerable<DiffLine> lines)
    {
        var added = 0;
        var removed = 0;
        foreach (var line in lines)
        {
            if (line.Kind == DiffLineKind.Added) added++;
            else if (line.Kind == DiffLineKind.Removed) removed++;
        }
        return (added, removed);
    }

    /// <summary>折叠未改动的上下文，只保留改动行和其前后若干行。</summary>
    public static IReadOnlyList<DiffLine> WithContext(IReadOnlyList<DiffLine> lines, int context = 2)
    {
        var keep = new bool[lines.Count];
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Kind == DiffLineKind.Same) continue;
            var from = Math.Max(0, i - context);
            var to = Math.Min(lines.Count - 1, i + context);
            for (var k = from; k <= to; k++) keep[k] = true;
        }

        var result = new List<DiffLine>();
        for (var i = 0; i < lines.Count; i++)
        {
            if (keep[i]) result.Add(lines[i]);
        }
        return result;
    }

    private static IReadOnlyList<DiffLine> AllMarked(IReadOnlyList<string> lines, DiffLineKind kind)
    {
        var list = new List<DiffLine>(lines.Count);
        for (var i = 0; i < lines.Count; i++)
            list.Add(kind == DiffLineKind.Added
                ? new DiffLine(kind, lines[i], 0, i + 1)
                : new DiffLine(kind, lines[i], i + 1, 0));
        return list;
    }

    private static IReadOnlyList<string> SplitLines(string text)
    {
        if (string.IsNullOrEmpty(text)) return Array.Empty<string>();
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        // 末尾换行不产生一条空行，符合编辑器直觉。
        if (normalized.EndsWith('\n')) normalized = normalized[..^1];
        return normalized.Split('\n');
    }
}
