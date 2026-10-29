using Momomi.Core.Services;

namespace Momomi.App.ViewModels;

/// <summary>
/// 记住用户为每个代理组手动选择的节点，内核启动后自动恢复。
/// 存储在设置表里（键 core.proxySelection），格式为每行 "组名\t节点名"。
/// </summary>
public static class ProxySelectionStore
{
    private const string Key = "core.proxySelection";

    // 读改写必须串行：裁剪（内核启动时）与记录（用户切换节点时）可能并发，
    // 两个「读 → 改 → 整体写回」交错会让后写的一方覆盖前一方刚写入的条目。
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task<Dictionary<string, string>> LoadAsync(ISettingsService settings)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var raw = await settings.GetAsync(Key).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(raw)) return map;

            foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var idx = line.IndexOf('\t');
                if (idx <= 0 || idx >= line.Length - 1) continue;
                var group = line[..idx].Trim();
                var node = line[(idx + 1)..].Trim();
                if (group.Length > 0 && node.Length > 0) map[group] = node;
            }
        }
        catch
        {
        }
        return map;
    }

    public static async Task SaveAsync(ISettingsService settings, IReadOnlyDictionary<string, string> map)
    {
        try
        {
            var text = string.Join("\n", map.Select(kv => $"{kv.Key}\t{kv.Value}"));
            await settings.SetAsync(Key, text).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    /// <summary>记录一个组的选择（读改写）。</summary>
    public static async Task RememberAsync(ISettingsService settings, string group, string node)
    {
        if (string.IsNullOrEmpty(group) || string.IsNullOrEmpty(node)) return;
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var map = await LoadAsync(settings).ConfigureAwait(false);
            map[group] = node;
            await SaveAsync(settings, map).ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// 按当前配置里真实存在的组与节点裁剪记录，丢掉已失效的条目。
    ///
    /// 不裁剪的话，切换订阅后旧组名会一直累积：实测切换几次订阅后表里会残留
    /// 十几条指向已不存在组/节点的记录（含订阅自带的广告节点），只增不减。
    /// 这些脏记录虽然会被恢复逻辑的 Contains 校验跳过、不会选错节点，
    /// 但会无限增长，且下次订阅里恰好出现同名组时会错误套用旧选择。
    /// </summary>
    /// <param name="groups">当前配置的组名 → 该组可选节点名集合。</param>
    public static async Task PruneAsync(
        ISettingsService settings, IReadOnlyDictionary<string, IReadOnlyCollection<string>> groups)
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var map = await LoadAsync(settings).ConfigureAwait(false);
            if (map.Count == 0) return;

            var removed = false;
            foreach (var group in map.Keys.ToList())
            {
                if (!groups.TryGetValue(group, out var nodes) || !nodes.Contains(map[group]))
                {
                    map.Remove(group);
                    removed = true;
                }
            }
            if (removed) await SaveAsync(settings, map).ConfigureAwait(false);
        }
        catch
        {
        }
        finally
        {
            Gate.Release();
        }
    }
}
