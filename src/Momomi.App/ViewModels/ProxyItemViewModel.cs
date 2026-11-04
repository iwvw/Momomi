using CommunityToolkit.Mvvm.ComponentModel;
using Momomi.Core.Models;

namespace Momomi.App.ViewModels;

public sealed partial class ProxyItemViewModel : ObservableObject
{
    public string Name { get; }
    public string Type { get; }
    public IReadOnlyList<string> Members { get; }
    public string GroupName { get; }

    /// <summary>该节点是否支持 UDP 转发（来自内核 /proxies 的 udp 字段）。</summary>
    public bool SupportsUdp { get; }

    /// <summary>TCP Fast Open。</summary>
    public bool Tfo { get; }

    /// <summary>XUDP（多路复用的 UDP 转发）。</summary>
    public bool Xudp { get; }

    /// <summary>Multipath TCP。</summary>
    public bool Mptcp { get; }

    /// <summary>SMUX 多路复用。</summary>
    public bool Smux { get; }

    /// <summary>传输层：ws / grpc / h2 / http / tcp（来自 runtime.yaml 的 network）。</summary>
    public string Transport { get; }

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
        IReadOnlyList<string>? members = null, bool isGroup = false, string groupName = "",
        bool supportsUdp = false, string transport = "",
        bool tfo = false, bool xudp = false, bool mptcp = false, bool smux = false)
    {
        Name = name;
        Type = type;
        IsAlive = alive;
        Delay = delay;
        IsSelected = selected;
        Members = members ?? Array.Empty<string>();
        IsGroup = isGroup;
        GroupName = groupName;
        SupportsUdp = supportsUdp;
        Transport = transport;
        Tfo = tfo;
        Xudp = xudp;
        Mptcp = mptcp;
        Smux = smux;
    }
    public void SetSelected(bool value) => IsSelected = value;

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

    /// <summary>
    /// 协议徽标文字：vless / vmess / trojan / hysteria2 …
    ///
    /// 直接用内核给的 type 转小写，不做中文映射：这些是协议名，
    /// 写成「选择器」反而让懂行的用户要去猜它到底是不是 selector。
    /// </summary>
    public string ProtocolBadge => string.IsNullOrEmpty(Type) ? "" : Type.ToLowerInvariant();

    /// <summary>
    /// 协议特性徽标。对齐 clash-party 的做法：tfo / udp / xudp / mptcp / smux。
    ///
    /// 只列出内核明确置位的能力，不做「QUIC 协议就一定有 UDP」之类的推断——
    /// 推断出来的结论一旦和内核实际行为不符，比不显示更误导。
    /// </summary>
    public IReadOnlyList<string> FeatureBadges
    {
        get
        {
            var list = new List<string>(4);
            if (Tfo) list.Add("tfo");
            if (SupportsUdp) list.Add("udp");
            if (Xudp) list.Add("xudp");
            if (Mptcp) list.Add("mptcp");
            if (Smux) list.Add("smux");
            return list;
        }
    }

    /// <summary>是否显示协议/特性徽标（由页面的显示模式控制）。</summary>
    [ObservableProperty]
    public partial bool ShowBadges { get; set; } = true;

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
