using CommunityToolkit.Mvvm.ComponentModel;
using Momomi.Core.Models;

namespace Momomi.App.ViewModels;

public sealed partial class ProxyItemViewModel : ObservableObject
{
    public string Name { get; }
    public string Type { get; }
    public IReadOnlyList<string> Members { get; }
    public string GroupName { get; }

    public Action<ProxyItemViewModel>? SelectRequested { get; set; }
    public Action<ProxyItemViewModel>? TestRequested { get; set; }

    [ObservableProperty]
    public partial bool IsAlive { get; set; }

    [ObservableProperty]
    public partial int? Delay { get; set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [ObservableProperty]
    public partial bool IsTesting { get; set; }

    public bool IsGroup { get; }

    public ProxyItemViewModel(string name, string type, bool alive, int? delay, bool selected,
        IReadOnlyList<string>? members = null, bool isGroup = false, string groupName = "")
    {
        Name = name;
        Type = type;
        IsAlive = alive;
        Delay = delay;
        IsSelected = selected;
        Members = members ?? Array.Empty<string>();
        IsGroup = isGroup;
        GroupName = groupName;
    }
    public void SetSelected(bool value) => IsSelected = value;

    /// <summary>名称开头的旗帜/图标（区域指示符对或前置 emoji），单独放大渲染以便看清。</summary>
    public string IconText => SplitIcon(Name).Icon;

    /// <summary>去掉开头图标后的名称。</summary>
    public string DisplayName => SplitIcon(Name).Text;

    private static (string Icon, string Text) SplitIcon(string name)
    {
        if (string.IsNullOrEmpty(name)) return ("", name ?? "");

        var i = 0;
        var elements = System.Globalization.StringInfo.ParseCombiningCharacters(name);
        if (elements.Length == 0) return ("", name);

        // 取第一个字素簇；若是区域指示符对（旗帜）则再并上紧随的变体选择符。
        var firstEnd = elements.Length > 1 ? elements[1] : name.Length;
        var first = name[..firstEnd];

        // 区域指示符 U+1F1E6..U+1F1FF：两两成对构成国旗。
        var isFlag = first.Length >= 2
            && char.IsSurrogatePair(first, 0)
            && char.ConvertToUtf32(first, 0) is >= 0x1F1E6 and <= 0x1F1FF;

        i = firstEnd;
        if (isFlag && elements.Length > 2)
        {
            // 合并第二个区域指示符（同一字素簇通常已包含，但部分输入是分开的）。
            var secondEnd = elements.Length > 2 ? elements[3] : name.Length;
            var second = name[firstEnd..secondEnd];
            if (second.Length >= 2 && char.IsSurrogatePair(second, 0)
                && char.ConvertToUtf32(second, 0) is >= 0x1F1E6 and <= 0x1F1FF)
            {
                i = secondEnd;
            }
        }

        var icon = name[..i].Trim();
        var text = name[i..].Trim();
        return (icon, text.Length == 0 ? name : text);
    }

    public string DelayText => IsTesting ? "" : Format.Delay(Delay);
    public string DelayKey
    {
        get
        {
            if (IsTesting) return "testing";
            if (Delay is null or < 0) return "none";
            if (Delay == 0) return "timeout";
            if (Delay <= 150) return "good";
            if (Delay <= 350) return "medium";
            return "bad";
        }
    }

    public string TypeText => string.IsNullOrEmpty(Type) ? "节点" : Type.ToLowerInvariant() switch
    {
        "selector" => "选择器",
        "urltest" => "自动测速",
        "fallback" => "故障转移",
        "loadbalance" => "负载均衡",
        "relay" => "链式代理",
        "direct" => "直连",
        "reject" => "拒绝",
        "dns" => "DNS",
        var t => t,
    };

    public void Update(ProxyItem item, bool selected)
    {
        IsAlive = item.Alive;
        Delay = item.Delay;
        SetSelected(selected);
    }

    partial void OnDelayChanged(int? value)
    {
        OnPropertyChanged(nameof(DelayText));
        OnPropertyChanged(nameof(DelayKey));
    }

    partial void OnIsTestingChanged(bool value)
    {
        OnPropertyChanged(nameof(DelayText));
        OnPropertyChanged(nameof(DelayKey));
    }
}
