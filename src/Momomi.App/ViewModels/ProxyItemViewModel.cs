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

    /// <summary>
    /// 把「开头的图标」和「真正的名字」分开。
    ///
    /// 只有两种情况算图标：成对区域指示符（国旗）或 emoji（星平面符号）。
    /// 普通字母/数字/汉字一律<b>不能</b>被当成图标剥离——历史上这里对任何名字都
    /// 无条件把第一个字素簇切给 Icon，于是 "DIRECT" 显示成图标 "D" + 名字 "IRECT"，
    /// 中文名节点也会丢掉首字。
    /// </summary>
    private static (string Icon, string Text) SplitIcon(string name)
    {
        if (string.IsNullOrEmpty(name)) return ("", name ?? "");

        var elements = System.Globalization.StringInfo.ParseCombiningCharacters(name);
        if (elements.Length == 0) return ("", name);

        // 取第一个字素簇；若是区域指示符对（旗帜）则再并上紧随的变体选择符。
        var firstEnd = elements.Length > 1 ? elements[1] : name.Length;
        var first = name[..firstEnd];

        // 区域指示符 U+1F1E6..U+1F1FF：两两成对构成国旗。
        var isFlag = first.Length >= 2
            && char.IsSurrogatePair(first, 0)
            && char.ConvertToUtf32(first, 0) is >= 0x1F1E6 and <= 0x1F1FF;

        // 星平面 emoji（含 🚀 之类的常见组名前缀）。只认代理节点名里真实出现过的范围，
        // 避免把普通字符误判成图标。
        var isEmoji = first.Length >= 2
            && char.IsSurrogatePair(first, 0)
            && char.ConvertToUtf32(first, 0) is >= 0x1F300 and <= 0x1FAFF;

        if (!isFlag && !isEmoji) return ("", name);

        var i = firstEnd;
        if (isFlag && elements.Length > 2)
        {
            // 合并第二个区域指示符（同一字素簇通常已包含，但部分输入是分开的）。
            //
            // 这里必须用 Length > 3 判断：名字恰好只有三个字素簇时（例如「🇺🇸 1」
            // —— 旗帜、空格、数字），下标 3 根本不存在，直接用会抛
            // IndexOutOfRangeException。而节点名来自订阅、完全不可控，
            // 等于打开代理页就崩。
            var secondEnd = elements.Length > 3 ? elements[3] : name.Length;
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
