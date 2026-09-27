using Momomi.Core.Services;

namespace Momomi.App.ViewModels;

/// <summary>
/// 记住用户为每个代理组手动选择的节点，内核启动后自动恢复。
/// 存储在设置表里（键 core.proxySelection），格式为每行 "组名\t节点名"。
/// </summary>
public static class ProxySelectionStore
{
    private const string Key = "core.proxySelection";

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
        var map = await LoadAsync(settings).ConfigureAwait(false);
        map[group] = node;
        await SaveAsync(settings, map).ConfigureAwait(false);
    }
}
