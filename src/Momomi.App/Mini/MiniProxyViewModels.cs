using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Momomi.Core.Models;

namespace Momomi.App.Mini;

public sealed partial class MiniProxyNodeViewModel : ObservableObject
{
    public string Name { get; }

    [ObservableProperty]
    public partial int? Delay { get; set; }

    public string DelayText => Delay switch
    {
        null or < 0 => "",
        0 => "超时",
        _ => $"{Delay} ms",
    };

    public string DelayKey => Delay switch
    {
        null or < 0 => "none",
        0 => "timeout",
        <= 150 => "good",
        <= 350 => "medium",
        _ => "bad",
    };

    public MiniProxyNodeViewModel(string name) => Name = name;

    partial void OnDelayChanged(int? value)
    {
        OnPropertyChanged(nameof(DelayText));
        OnPropertyChanged(nameof(DelayKey));
    }

    public override string ToString() => Name;
}

public sealed partial class MiniProxyGroupViewModel : ObservableObject
{
    public string Name { get; }

    public ObservableCollection<MiniProxyNodeViewModel> Nodes { get; } = new();

    [ObservableProperty]
    public partial MiniProxyNodeViewModel? Selected { get; set; }

    [ObservableProperty]
    public partial bool IsTesting { get; set; }

    public MiniProxyGroupViewModel(string name) => Name = name;

    public void Fill(IReadOnlyList<string> members, IReadOnlyDictionary<string, ProxyItem> all, string? now)
    {
        var names = new List<string>();
        foreach (var member in members)
        {
            if (all.ContainsKey(member)) names.Add(member);
        }

        var unchanged = names.Count == Nodes.Count;
        if (unchanged)
        {
            for (var i = 0; i < names.Count; i++)
            {
                if (!string.Equals(Nodes[i].Name, names[i], StringComparison.Ordinal))
                {
                    unchanged = false;
                    break;
                }
            }
        }

        if (!unchanged)
        {
            Nodes.Clear();
            foreach (var name in names) Nodes.Add(new MiniProxyNodeViewModel(name));
        }

        foreach (var node in Nodes)
        {
            if (all.TryGetValue(node.Name, out var item)) node.Delay = item.Delay;
        }

        // 以 API 的当前选中项为准同步选择（跨面板切换节点时生效）；
        // now 无效时保留已有选择，避免重排/刷新把选择清空。测速走 ApplyDelays，不经过这里。
        var target = now is null ? null : Nodes.FirstOrDefault(n => n.Name == now);
        if (target is not null)
            Selected = target;
        else if (Selected is null || !Nodes.Contains(Selected))
            Selected = Nodes.FirstOrDefault();

        SortByDelay();
    }

    /// <summary>按延迟排序：有效延迟在前且升序，超时/未测速在后。</summary>
    public void SortByDelay()
    {
        var sorted = Nodes
            .OrderBy(n => n.Delay is > 0 ? 0 : 1)
            .ThenBy(n => n.Delay is > 0 ? n.Delay!.Value : int.MaxValue)
            .ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var inOrder = sorted.Count == Nodes.Count;
        if (inOrder)
        {
            for (var i = 0; i < sorted.Count; i++)
            {
                if (!ReferenceEquals(Nodes[i], sorted[i]))
                {
                    inOrder = false;
                    break;
                }
            }
        }
        if (inOrder) return;

        for (var i = 0; i < sorted.Count; i++)
        {
            var current = Nodes.IndexOf(sorted[i]);
            if (current != i) Nodes.Move(current, i);
        }

        // Move 会让 ComboBox 丢失 SelectedItem；强制重推同一引用以恢复选择。
        OnPropertyChanged(nameof(Selected));
    }

    public void ApplyDelays(IReadOnlyDictionary<string, int> delays)
    {
        foreach (var node in Nodes)
            node.Delay = delays.TryGetValue(node.Name, out var d) && d > 0 ? d : 0;

        SortByDelay();
    }
}
