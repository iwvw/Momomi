using System.Net;
using System.Text;

namespace Momomi.App.ViewModels;

/// <summary>
/// 把一条连接的关键字段拼成可直接粘贴的 Mihomo 规则文本，
/// 供连接详情弹窗"一键复制为规则"使用。
/// </summary>
public static class RuleTextBuilder
{
    public static string Build(ConnectionRowViewModel row)
    {
        var sb = new StringBuilder();
        var (host, ip, port) = SplitHostPort(row);

        if (IsIpAddress(ip))
        {
            sb.AppendLine(IsIpV6(ip)
                ? $"IP-CIDR6,{ip}/128"
                : $"IP-CIDR,{ip}/32");
        }
        else if (!string.IsNullOrEmpty(host))
        {
            // 域名优先给出精确域名与后缀两条，方便直接选用。
            sb.AppendLine($"DOMAIN,{host}");
            sb.AppendLine($"DOMAIN-SUFFIX,{host}");
        }

        if (port > 0)
            sb.AppendLine($"DST-PORT,{port}");

        if (!string.IsNullOrEmpty(row.Process))
            sb.AppendLine($"PROCESS-NAME,{row.Process}");

        var text = sb.ToString().TrimEnd();
        return text.Length == 0 ? "（无法从该连接提取可用的规则字段）" : text;
    }

    private static (string Host, string Ip, int Port) SplitHostPort(ConnectionRowViewModel row)
    {
        // Host 可能形如 "example.com" 或 "1.2.3.4:8080"；优先解析 Host，回退到 Destination。
        var raw = row.Host;
        var (host, port) = SplitHostPortString(raw);
        if (string.IsNullOrEmpty(host))
        {
            var dst = SplitHostPortString(row.Destination);
            host = dst.Host;
            if (port <= 0) port = dst.Port;
        }
        return (host, IsIpAddress(host) ? host : row.Destination.Split(':')[0], port);
    }

    private static (string Host, int Port) SplitHostPortString(string? value)
    {
        if (string.IsNullOrEmpty(value)) return ("", 0);
        var colon = value.LastIndexOf(':');
        if (colon <= 0) return (value, 0);
        var host = value[..colon];
        if (int.TryParse(value[(colon + 1)..], out var port)) return (host, port);
        return (value, 0);
    }

    private static bool IsIpAddress(string? value)
        => value is not null && IPAddress.TryParse(value, out _);

    private static bool IsIpV6(string value)
        => value.Contains(':');
}
