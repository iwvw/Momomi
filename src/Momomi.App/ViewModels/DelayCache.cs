namespace Momomi.App.ViewModels;

/// <summary>
/// 全局测速结果缓存。主面板与迷你面板共用的唯一数据源。
///
/// 为什么需要它：两个面板各自持有一份 ProxyItemViewModel / MiniProxyNodeViewModel 实例，
/// 而内核的 /proxies 接口不返回策略组的延迟（组的 history 恒为空），客户端测出来的值
/// 只存在于测速那一刻所在的面板里。于是「在迷你面板测的速，主面板看不到」，
/// 反之亦然。
///
/// 这里把「节点名 → 延迟」集中存一份，两个面板渲染时都先查它，
/// 谁测的都不重要，结果对所有视图一致。
///
/// 只存真实节点（含 DIRECT）：策略组的延迟是派生的，由各面板沿 now 链自行解析，
/// 不在这里重复记录——否则切换组内节点后旧值会残留。
/// </summary>
public static class DelayCache
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, int> Values = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, bool> Alives = new(StringComparer.Ordinal);

    /// <summary>某个节点有新结果时触发。参数为节点名，订阅方据此刷新对应行。</summary>
    public static event EventHandler<string>? Updated;

    /// <summary>取节点延迟；从未测过返回 null。</summary>
    public static int? Get(string name)
    {
        lock (Gate) return Values.TryGetValue(name, out var v) ? v : null;
    }

    /// <summary>取节点存活状态；从未测过返回 null。</summary>
    public static bool? GetAlive(string name)
    {
        lock (Gate) return Alives.TryGetValue(name, out var v) ? v : null;
    }

    /// <summary>
    /// 记录一次测速结果（0 表示超时/失败，与内核语义一致）。
    /// 值没变时不触发广播，避免无意义的整页刷新。
    /// </summary>
    public static void Set(string name, int delay)
    {
        var value = delay > 0 ? delay : 0;
        bool changed;
        lock (Gate)
        {
            changed = !Values.TryGetValue(name, out var old) || old != value;
            Values[name] = value;
            Alives[name] = value > 0;
        }
        if (changed) Updated?.Invoke(null, name);
    }

    /// <summary>批量记录，只在末尾广播一次。</summary>
    public static void SetMany(IReadOnlyDictionary<string, int> results)
    {
        if (results.Count == 0) return;

        var changed = new List<string>();
        lock (Gate)
        {
            foreach (var kv in results)
            {
                var value = kv.Value > 0 ? kv.Value : 0;
                if (Values.TryGetValue(kv.Key, out var old) && old == value) continue;
                Values[kv.Key] = value;
                Alives[kv.Key] = value > 0;
                changed.Add(kv.Key);
            }
        }

        foreach (var name in changed) Updated?.Invoke(null, name);
    }

    /// <summary>
    /// 内核重载/订阅切换后清空，避免拿旧配置的节点值。
    ///
    /// 不需要额外通知界面：清空后两个面板都会因 ProxiesChanged 重建，
    /// 重建时 Get 返回 null，自然回落到内核快照里的新值。
    /// </summary>
    public static void Clear()
    {
        lock (Gate)
        {
            Values.Clear();
            Alives.Clear();
        }
    }
}
