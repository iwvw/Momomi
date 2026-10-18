using YamlDotNet.RepresentationModel;

namespace Momomi.Core.Services;

/// <summary>
/// YAML 覆写（Override）合并器。
///
/// 语法对齐 Mihomo Party / Clash Verge Rev 的覆写约定，让用户可以直接粘贴
/// 社区里现成的覆写文件：
///
/// <list type="bullet">
/// <item><c>key: value</c>  简单值直接覆盖；嵌套映射递归合并。</item>
/// <item><c>key!:</c>       强制整体替换该键，不做递归合并。</item>
/// <item><c>rules:</c>      数组整体替换。</item>
/// <item><c>+rules:</c>     数组前置（插到原数组最前面）。</item>
/// <item><c>rules+:</c>     数组追加（接在原数组最后）。</item>
/// <item><c>&lt;+xxx&gt;:</c>  键名本身以 + 开头/结尾时用尖括号转义，避免歧义。</item>
/// </list>
///
/// 合并顺序：应用自有全局项 → 全局启用的覆写 → 当前订阅挂载的覆写（按列表顺序）。
/// 后应用的覆写优先级更高，与 Mihomo Party 的"最后开启的生效"一致。
/// </summary>
public static class YamlOverrideMerger
{
    /// <summary>合并操作：普通覆盖 / 前置 / 追加 / 强制整体替换。</summary>
    private enum MergeMode
    {
        Override,
        Prepend,
        Append,
        Replace,
    }

    /// <summary>
    /// 把若干覆写文件依次合并进基础配置。
    /// </summary>
    /// <param name="baseYaml">基础配置（订阅 + 应用全局项的产物）。</param>
    /// <param name="overrides">覆写 YAML，按优先级从低到高排列。</param>
    /// <returns>合并后的 YAML 文本；任一覆写解析失败会抛出 <see cref="YamlOverrideException"/>。</returns>
    public static string Apply(string baseYaml, IEnumerable<OverrideDocument> overrides)
    {
        var root = ParseMapping(baseYaml, "基础配置");
        foreach (var doc in overrides)
        {
            if (string.IsNullOrWhiteSpace(doc.Content)) continue;
            var patch = ParseMapping(doc.Content, doc.Name);
            MergeMapping(root, patch, doc.Name);
        }
        return Serialize(root);
    }

    /// <summary>解析一段 YAML 是否是合法映射结构，供编辑时校验。</summary>
    /// <returns>成功返回 null，失败返回可读的错误信息。</returns>
    public static string? Validate(string yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml)) return "内容为空";
        try
        {
            var stream = new YamlStream();
            using var reader = new StringReader(yaml);
            stream.Load(reader);
            if (stream.Documents.Count == 0) return "没有 YAML 文档";
            if (stream.Documents[0].RootNode is not YamlMappingNode)
                return "顶层必须是键值映射（mapping），不能是列表或标量";
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// 把一份 YAML 规范化成与 <see cref="Apply"/> 输出一致的文本格式
    /// （同样的缩进、同样的 emoji 处理）。
    ///
    /// 用于拿"合并前的基线"做差异比较：不先规范化的话，基线里的
    /// \U0001F680 会和新输出里的真实 emoji 逐行对不上，差异视图里就会
    /// 冒出一大堆假的改动。
    ///
    /// 与 <see cref="Apply"/> 的唯一区别是保留 YAML 的 `...` 结束标记：
    /// editor 里的对照文本本来就有，去掉反而多出一条假改动。这份文本只用于展示，
    /// 不会写进 runtime.yaml。
    /// </summary>
    public static string Normalize(string yaml)
        => Serialize(ParseMapping(yaml, "配置"), withDocumentEnd: true);

    private static YamlMappingNode ParseMapping(string yaml, string source)
    {
        try
        {
            var stream = new YamlStream();
            using var reader = new StringReader(yaml);
            stream.Load(reader);

            if (stream.Documents.Count == 0) return new YamlMappingNode();
            return stream.Documents[0].RootNode as YamlMappingNode
                ?? throw new YamlOverrideException($"{source}：顶层必须是键值映射");
        }
        catch (YamlOverrideException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new YamlOverrideException($"{source}：YAML 解析失败 - {ex.Message}", ex);
        }
    }

    /// <summary>
    /// YamlDotNet 的 emitter 会把星平面字符（> U+FFFF，即绝大多数 emoji）写成
    /// \U0001F680 这样的转义，而中文、日文等 BMP 字符原样保留。
    /// 结果是订阅里的 emoji 组名变成「\U0001F680 手动」，在编辑器里几乎没法读，
    /// 差异视图也会把每条带 emoji 的规则都标成改动。
    ///
    /// 这里把双引号字符串内的 \U / \u 转义还原成字符。只处理双引号内的内容：
    /// 裸标量或单引号里的反斜杠是用户的字面量，不能动。
    /// </summary>
    private static string UnescapeUnicodeInDoubleQuoted(string yaml)
    {
        if (yaml.IndexOf("\\U", StringComparison.Ordinal) < 0
            && yaml.IndexOf("\\u", StringComparison.Ordinal) < 0)
            return yaml;

        var sb = new System.Text.StringBuilder(yaml.Length);
        var inDoubleQuote = false;
        for (var i = 0; i < yaml.Length; i++)
        {
            var ch = yaml[i];

            if (!inDoubleQuote)
            {
                if (ch == '"') inDoubleQuote = true;
                sb.Append(ch);
                continue;
            }

            if (ch == '"')
            {
                inDoubleQuote = false;
                sb.Append(ch);
                continue;
            }

            // 双引号内的转义：\" \\ \Uxxxxxxxx \uxxxx
            if (ch == '\\' && i + 1 < yaml.Length)
            {
                var next = yaml[i + 1];
                var digits = next == 'U' ? 8 : next == 'u' ? 4 : 0;
                if (digits > 0 && i + 2 + digits <= yaml.Length)
                {
                    var hex = yaml.Substring(i + 2, digits);
                    if (int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var code)
                        && code <= 0x10FFFF
                        && (code < 0xD800 || code > 0xDFFF))
                    {
                        sb.Append(char.ConvertFromUtf32(code));
                        i += 1 + digits;
                        continue;
                    }
                }

                // 其它转义原样保留（\" \\ \n 等交给 YAML 语义处理）。
                sb.Append(ch).Append(next);
                i++;
                continue;
            }

            sb.Append(ch);
        }

        return sb.ToString();
    }

    private static string Serialize(YamlMappingNode root, bool withDocumentEnd = false)
    {
        // 用默认设置输出：与 mihomo 自己写回配置的格式一致（序列不额外缩进）。
        //
        // 曾经尝试打开 EmitterSettings.IndentSequences 让序列缩进两格，但那会把
        // 整份配置的每一个数组都重新缩进，差异视图里显示成"全文件都被改了"，
        // 反而看不出真实改动。宁可保持默认格式，也不制造假改动。
        var stream = new YamlStream(new YamlDocument(root));
        using var writer = new StringWriter();
        stream.Save(writer, assignAnchors: false);

        // YamlDotNet 会在末尾补一行 `...`（显式文档结束标记）。写进 runtime.yaml 的文本
        // 不能带它：mihomo 加载配置时按「显式文档结束」解析，会把配置对象置空，
        // 结果是内核加载后既不报错也没有任何代理。
        //
        // 仅差异视图的基线保留结束标记，避免和编辑器里的对照文本多出假改动。
        var text = writer.ToString();
        if (withDocumentEnd) return text;

        // 先去掉尾部空白再判断：emitter 可能输出 "...\r\n" 或 "...\n\n" 之类的组合，
        // 直接匹配字面量会漏掉。保留原有的结尾换行风格。
        var trimmed = text.TrimEnd();
        if (trimmed.EndsWith("...", StringComparison.Ordinal))
        {
            var keepNewline = text.EndsWith("\n", StringComparison.Ordinal);
            text = trimmed[..^3].TrimEnd() + (keepNewline ? "\n" : "");
        }

        return UnescapeUnicodeInDoubleQuoted(text);
    }

    private static void MergeMapping(YamlMappingNode target, YamlMappingNode patch, string source)
    {
        foreach (var (rawKeyNode, value) in patch.Children)
        {
            var rawKey = (rawKeyNode as YamlScalarNode)?.Value;
            if (string.IsNullOrEmpty(rawKey))
                throw new YamlOverrideException($"{source}：存在空的键名");

            var (key, mode) = ParseKey(rawKey);
            var keyNode = new YamlScalarNode(key);

            if (mode == MergeMode.Replace || !target.Children.TryGetValue(keyNode, out var existing))
            {
                target.Children[keyNode] = value;
                continue;
            }

            if (mode is MergeMode.Prepend or MergeMode.Append)
            {
                target.Children[keyNode] = MergeSequence(existing, value, mode, key, source);
                continue;
            }

            // 普通覆盖：两边都是映射时递归合并，否则直接覆盖。
            if (existing is YamlMappingNode targetMap && value is YamlMappingNode patchMap)
                MergeMapping(targetMap, patchMap, source);
            else
                target.Children[keyNode] = value;
        }
    }

    private static YamlSequenceNode MergeSequence(
        YamlNode existing, YamlNode patch, MergeMode mode, string key, string source)
    {
        if (existing is not YamlSequenceNode targetSeq || patch is not YamlSequenceNode patchSeq)
            throw new YamlOverrideException(
                $"{source}：键 \"{key}\" 使用 + 前缀/后缀时，原值与覆写值都必须是数组");

        var result = new YamlSequenceNode();
        var (first, second) = mode == MergeMode.Prepend
            ? (patchSeq, targetSeq)
            : (targetSeq, patchSeq);

        foreach (var item in first.Children) result.Add(item);
        foreach (var item in second.Children) result.Add(item);
        return result;
    }

    /// <summary>
    /// 解析键名修饰符：
    /// <c>+key</c> 前置，<c>key+</c> 追加，<c>key!</c> 强制替换，
    /// <c>&lt;key&gt;</c> 原样使用（用于键名本身带 + 或 ! 的情况）。
    /// </summary>
    private static (string Key, MergeMode Mode) ParseKey(string raw)
    {
        // <xxx> 包裹：去掉尖括号，不做任何修饰解析。
        if (raw.Length >= 2 && raw[0] == '<' && raw[^1] == '>')
            return (raw[1..^1], MergeMode.Override);

        if (raw.Length > 1 && raw[0] == '+')
            return (raw[1..], MergeMode.Prepend);

        if (raw.Length > 1 && raw[^1] == '+')
            return (raw[..^1], MergeMode.Append);

        if (raw.Length > 1 && raw[^1] == '!')
            return (raw[..^1], MergeMode.Replace);

        return (raw, MergeMode.Override);
    }
}

/// <summary>一个待合并的覆写文档。</summary>
/// <param name="Name">文件名或显示名，用于错误提示。</param>
/// <param name="Content">YAML 内容。</param>
public sealed record OverrideDocument(string Name, string Content);

/// <summary>覆写合并失败（语法错误、结构不匹配等），消息可直接展示给用户。</summary>
public sealed class YamlOverrideException : Exception
{
    public YamlOverrideException(string message) : base(message)
    {
    }

    public YamlOverrideException(string message, Exception inner) : base(message, inner)
    {
    }
}
