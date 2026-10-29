using YamlDotNet.RepresentationModel;

namespace Momomi.Core.Services;

/// <summary>节点的可直连地址，供 TCP 延迟探测使用。</summary>
public sealed record ProxyEndpoint(string Server, int Port);

public sealed record RuntimeYamlOptions(
    int MixedPort,
    int ControllerPort,
    string Secret,
    string Mode,
    string LogLevel,
    bool AllowLan,
    bool Ipv6,
    bool TunEnabled,
    string TunStack = "mixed",
    bool TunAutoRoute = true,
    bool TunAutoRedirect = false,
    bool TunAutoDetectInterface = true,
    bool TunStrictRoute = false,
    int TunMtu = 0,
    IReadOnlyList<string>? TunDnsHijack = null,
    IReadOnlyList<string>? TunRouteExcludeAddress = null);

public static class MihomoConfigBuilder
{
    public static string BuildRuntimeYaml(string subscriptionYaml, RuntimeYamlOptions options)
    {
        var yaml = new YamlStream();
        using var reader = new StringReader(subscriptionYaml);
        yaml.Load(reader);

        var root = yaml.Documents.Count > 0
            ? yaml.Documents[0].RootNode as YamlMappingNode
            : null;

        if (root is null)
        {
            root = new YamlMappingNode();
            yaml.Documents.Clear();
            yaml.Documents.Add(new YamlDocument(root));
        }

        var keysToStrip = new[]
        {
            "external-controller", "secret", "port", "socks-port", "mixed-port",
            "allow-lan", "mode", "log-level", "ipv6", "tun", "external-controller-cors",
            "dns", "unified-delay", "profile", "sniffer",
        };
        foreach (var key in keysToStrip)
            root.Children.Remove(new YamlScalarNode(key));

        root.Children[new YamlScalarNode("mixed-port")] = new YamlScalarNode(options.MixedPort.ToString());
        root.Children[new YamlScalarNode("allow-lan")] = new YamlScalarNode(options.AllowLan ? "true" : "false");
        root.Children[new YamlScalarNode("mode")] = new YamlScalarNode(options.Mode);
        root.Children[new YamlScalarNode("log-level")] = new YamlScalarNode(options.LogLevel);
        root.Children[new YamlScalarNode("ipv6")] = new YamlScalarNode(options.Ipv6 ? "true" : "false");
        root.Children[new YamlScalarNode("external-controller")] = new YamlScalarNode($"127.0.0.1:{options.ControllerPort}");
        root.Children[new YamlScalarNode("secret")] = new YamlScalarNode(options.Secret);

        // 持久化策略组选择与 fake-ip 映射：
        // 不显式开启时，重启内核后策略组会回到订阅里的第一个节点，
        // 用户手动选的节点丢失；fake-ip 映射也重新分配，已建立的连接域名对不上。
        root.Children[new YamlScalarNode("profile")] = new YamlMappingNode
        {
            { "store-selected", new YamlScalarNode("true") },
            { "store-fake-ip", new YamlScalarNode("true") },
        };

        // 统一延迟：内核测速时扣掉 TLS/TCP 握手耗时，只报真实往返时间。
        // 不开的话同一个节点在测速接口里会凭空多出一次握手的开销（几十到几百毫秒），
        // 数字比实际使用时的体验大得多。clash-party 也默认打开这一项。
        root.Children[new YamlScalarNode("unified-delay")] = new YamlScalarNode("true");

        root.Children[new YamlScalarNode("dns")] = BuildDnsNode(options.TunEnabled);
        root.Children[new YamlScalarNode("sniffer")] = BuildSnifferNode();

        var cors = new YamlMappingNode
        {
            { "allow-origins", new YamlSequenceNode(new YamlScalarNode("*")) },
            { "allow-private-network", new YamlScalarNode("true") },
        };
        root.Children[new YamlScalarNode("external-controller-cors")] = cors;

        if (options.TunEnabled)
        {
            var tun = new YamlMappingNode
            {
                { "enable", new YamlScalarNode("true") },
                { "stack", new YamlScalarNode(string.IsNullOrWhiteSpace(options.TunStack) ? "mixed" : options.TunStack) },
                { "auto-route", new YamlScalarNode(options.TunAutoRoute ? "true" : "false") },
                { "auto-redirect", new YamlScalarNode(options.TunAutoRedirect ? "true" : "false") },
                { "auto-detect-interface", new YamlScalarNode(options.TunAutoDetectInterface ? "true" : "false") },
            };

            if (options.TunStrictRoute)
                tun.Add("strict-route", new YamlScalarNode("true"));

            if (options.TunMtu is > 0)
                tun.Add("mtu", new YamlScalarNode(options.TunMtu.ToString()));

            var dnsHijack = options.TunDnsHijack is { Count: > 0 }
                ? options.TunDnsHijack
                : new[] { "any:53" };
            tun.Add("dns-hijack", new YamlSequenceNode(dnsHijack.Select(v => new YamlScalarNode(v))));

            if (options.TunRouteExcludeAddress is { Count: > 0 })
                tun.Add("route-exclude-address", new YamlSequenceNode(options.TunRouteExcludeAddress.Select(v => new YamlScalarNode(v))));

            root.Children[new YamlScalarNode("tun")] = tun;
        }
        else
        {
            root.Children.Remove(new YamlScalarNode("tun"));
        }

        using var writer = new StringWriter();
        yaml.Save(writer, assignAnchors: false);
        return writer.ToString();
    }

    /// <summary>
    /// TUN 模式下 dns-hijack 会把 DNS 查询交给内核，必须同时提供 dns 段，
    /// 否则域名无法解析。普通模式用 fake-ip 以避免 DNS 泄漏并支持域名规则。
    /// </summary>
    private static YamlMappingNode BuildDnsNode(bool tunEnabled)
    {
        // 国内可达的 DoH/DoT。不要用 1.1.1.1 / 8.8.8.8 的 DoH：
        // 这两个在国内被墙，会导致代理服务器域名（如 CDN 域名）解析超时，节点连不上。
        var nameservers = new YamlSequenceNode(
            new YamlScalarNode("https://223.5.5.5/dns-query"),
            new YamlScalarNode("https://doh.pub/dns-query"));

        // 代理服务器域名必须能直连解析，否则节点无法建立连接。
        var proxyNameservers = new YamlSequenceNode(
            new YamlScalarNode("223.5.5.5"),
            new YamlScalarNode("119.29.29.29"));

        // fake-ip-filter 必须用 + 前缀，不能用 *：
        // mihomo 的 * 一次只匹配一级域名（*.lan 匹配不到 nas.home.lan，
        // 且不匹配 lan 本身），+ 才是多级后缀匹配。用 * 会让多级内网域名
        // 拿到 198.18.x.x 假 IP，表现为局域网设备访问失败。
        // 与 clash-party 的默认值保持一致（*、+.lan、+.local 加时间同步类域名）。
        var fakeIpFilter = new YamlSequenceNode(
            new YamlScalarNode("*"),
            new YamlScalarNode("+.lan"),
            new YamlScalarNode("+.local"),
            new YamlScalarNode("+.home.arpa"),
            new YamlScalarNode("time.*.com"),
            new YamlScalarNode("ntp.*.com"),
            new YamlScalarNode("localhost.ptlogin2.qq.com"));

        var dns = new YamlMappingNode
        {
            { "enable", new YamlScalarNode("true") },
            { "ipv6", new YamlScalarNode(tunEnabled ? "true" : "false") },
            { "enhanced-mode", new YamlScalarNode("fake-ip") },
            { "fake-ip-range", new YamlScalarNode("198.18.0.1/16") },
            { "fake-ip-filter", fakeIpFilter },
            { "default-nameserver", new YamlSequenceNode(
                new YamlScalarNode("223.5.5.5"),
                new YamlScalarNode("119.29.29.29")) },
            { "nameserver", nameservers },
            { "proxy-server-nameserver", proxyNameservers },
        };

        return dns;
    }

    /// <summary>
    /// 域名嗅探：TUN 模式下部分流量只带目标 IP 不带域名（QUIC、直连 IP 的程序），
    /// 嗅探出真实域名后才能按域名规则正确分流，否则这些流量会落到 MATCH 兜底规则。
    ///
    /// 与 clash-party 的默认值保持一致：
    /// - override-destination 为 false，嗅探结果只用于规则匹配，不改写连接目标
    /// - skip-domain 排除 Apple 推送，嗅探会破坏它的长连接
    /// - skip-dst-address 排除 Telegram 全部网段，嗅探其 MTProto 会握手失败
    /// </summary>
    private static YamlMappingNode BuildSnifferNode()
    {
        var sniff = new YamlMappingNode
        {
            { "HTTP", new YamlMappingNode { { "ports", new YamlSequenceNode(
                new YamlScalarNode("80"), new YamlScalarNode("443")) } } },
            { "TLS", new YamlMappingNode { { "ports", new YamlSequenceNode(
                new YamlScalarNode("443")) } } },
        };

        var skipDst = new YamlSequenceNode(
            new YamlScalarNode("91.105.192.0/23"),
            new YamlScalarNode("91.108.4.0/22"),
            new YamlScalarNode("91.108.8.0/21"),
            new YamlScalarNode("91.108.16.0/21"),
            new YamlScalarNode("91.108.56.0/22"),
            new YamlScalarNode("95.161.64.0/20"),
            new YamlScalarNode("149.154.160.0/20"),
            new YamlScalarNode("185.76.151.0/24"),
            new YamlScalarNode("2001:67c:4e8::/48"),
            new YamlScalarNode("2001:b28:f23c::/47"),
            new YamlScalarNode("2001:b28:f23f::/48"),
            new YamlScalarNode("2a0a:f280:203::/48"));

        return new YamlMappingNode
        {
            { "enable", new YamlScalarNode("true") },
            { "parse-pure-ip", new YamlScalarNode("true") },
            { "force-dns-mapping", new YamlScalarNode("true") },
            { "override-destination", new YamlScalarNode("false") },
            { "sniff", sniff },
            { "skip-domain", new YamlSequenceNode(new YamlScalarNode("+.push.apple.com")) },
            { "skip-dst-address", skipDst },
        };
    }

    public static string BuildFallbackYaml(RuntimeYamlOptions options)
    {
        return BuildRuntimeYaml("""
            proxies: []
            proxy-groups: []
            rules:
              - MATCH,DIRECT
            """, options);
    }

    /// <summary>
    /// 从运行时配置读取 proxy-groups 的声明顺序。
    /// mihomo 的 /proxies 返回 JSON 对象，键顺序不可靠，需要以此还原配置顺序。
    ///
    /// 结果按「路径 + 最后写入时间 + 文件长度」缓存：这个方法在代理页/首页/迷你面板
    /// 每次重建列表时都会被调用，而它要读并解析整份 runtime.yaml（可能上千行）。
    /// 配置重写后时间戳变化，缓存自然失效，不需要额外的失效通知。
    /// </summary>
    public static IReadOnlyList<string> ReadProxyGroupOrder(string yamlPath)
    {
        try
        {
            var info = new FileInfo(yamlPath);
            if (!info.Exists) return Array.Empty<string>();

            var key = (yamlPath, info.LastWriteTimeUtc, info.Length);
            lock (GroupOrderCacheGate)
            {
                if (GroupOrderCacheKey == key && GroupOrderCache is not null)
                    return GroupOrderCache;
            }

            var order = ParseProxyGroupOrder(yamlPath);

            lock (GroupOrderCacheGate)
            {
                GroupOrderCacheKey = key;
                GroupOrderCache = order;
            }
            return order;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static readonly object GroupOrderCacheGate = new();
    private static (string Path, DateTime WriteTime, long Length) GroupOrderCacheKey = default;
    private static IReadOnlyList<string>? GroupOrderCache;

    private static readonly object ProxyEndpointCacheGate = new();
    private static (string Path, DateTime WriteTime, long Length) ProxyEndpointCacheKey = default;
    private static IReadOnlyDictionary<string, ProxyEndpoint>? ProxyEndpointCache;

    /// <summary>
    /// 从运行时配置读取每个节点可直连的 server/port，供 TCP 延迟探测使用。
    ///
    /// 为什么不能走 /proxies 接口：mihomo 的 API 只返回 name/type/alive/history 等字段，
    /// 服务器地址与端口不在响应里。要做「只测 TCP 握手」就必须知道往哪连，
    /// 只能从配置文件拿。
    ///
    /// 返回值只包含「本机可直连」的节点：hysteria2 等基于 QUIC 的协议没有可用的
    /// TCP 端口，跳过后由调用方回退到内核测速。
    /// </summary>
    public static IReadOnlyDictionary<string, ProxyEndpoint> ReadProxyEndpoints(string yamlPath)
    {
        try
        {
            var info = new FileInfo(yamlPath);
            if (!info.Exists) return new Dictionary<string, ProxyEndpoint>();

            var key = (yamlPath, info.LastWriteTimeUtc, info.Length);
            lock (ProxyEndpointCacheGate)
            {
                if (ProxyEndpointCacheKey == key && ProxyEndpointCache is not null)
                    return ProxyEndpointCache;
            }

            var endpoints = ParseProxyEndpoints(yamlPath);

            lock (ProxyEndpointCacheGate)
            {
                ProxyEndpointCacheKey = key;
                ProxyEndpointCache = endpoints;
            }
            return endpoints;
        }
        catch
        {
            return new Dictionary<string, ProxyEndpoint>();
        }
    }

    private static IReadOnlyDictionary<string, ProxyEndpoint> ParseProxyEndpoints(string yamlPath)
    {
        var result = new Dictionary<string, ProxyEndpoint>(StringComparer.Ordinal);
        try
        {
            var yaml = new YamlStream();
            using var reader = new StreamReader(yamlPath);
            yaml.Load(reader);

            if (yaml.Documents.Count == 0) return result;
            if (yaml.Documents[0].RootNode is not YamlMappingNode root) return result;
            if (!root.Children.TryGetValue(new YamlScalarNode("proxies"), out var node)) return result;
            if (node is not YamlSequenceNode proxies) return result;

            foreach (var item in proxies.Children)
            {
                if (item is not YamlMappingNode proxy) continue;

                var name = GetScalar(proxy, "name");
                var server = GetScalar(proxy, "server");
                var type = GetScalar(proxy, "type");
                var portText = GetScalar(proxy, "port");
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(server)) continue;
                if (!int.TryParse(portText, out var port) || port is <= 0 or > 65535) continue;

                // QUIC 类协议（hysteria/hysteria2/tuic）走 UDP，没有可测的 TCP 握手。
                if (IsQuicOnly(type)) continue;

                result[name] = new ProxyEndpoint(server, port);
            }
        }
        catch
        {
        }
        return result;
    }

    private static bool IsQuicOnly(string? type)
        => type is not null
           && (type.Equals("hysteria", StringComparison.OrdinalIgnoreCase)
               || type.Equals("hysteria2", StringComparison.OrdinalIgnoreCase)
               || type.Equals("tuic", StringComparison.OrdinalIgnoreCase));

    private static string? GetScalar(YamlMappingNode mapping, string key)
        => mapping.Children.TryGetValue(new YamlScalarNode(key), out var value)
           && value is YamlScalarNode scalar
           && scalar.Value is { Length: > 0 } text
            ? text
            : null;

    private static IReadOnlyList<string> ParseProxyGroupOrder(string yamlPath)
    {
        try
        {
            var yaml = new YamlStream();
            using var reader = new StreamReader(yamlPath);
            yaml.Load(reader);

            if (yaml.Documents.Count == 0) return Array.Empty<string>();
            if (yaml.Documents[0].RootNode is not YamlMappingNode root) return Array.Empty<string>();
            if (!root.Children.TryGetValue(new YamlScalarNode("proxy-groups"), out var node)) return Array.Empty<string>();
            if (node is not YamlSequenceNode groups) return Array.Empty<string>();

            var order = new List<string>();
            foreach (var item in groups.Children)
            {
                if (item is not YamlMappingNode group) continue;
                if (!group.Children.TryGetValue(new YamlScalarNode("name"), out var nameNode)) continue;
                if (nameNode is YamlScalarNode scalar && scalar.Value is { Length: > 0 } name)
                    order.Add(name);
            }
            return order;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}
