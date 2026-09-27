namespace Momomi.Core.Models;

public sealed record CoreStatus(
    bool Running,
    string? Version,
    string? Error)
{
    public static CoreStatus Stopped { get; } = new(false, null, null);
}

public sealed record TrafficSnapshot(
    long Up,
    long Down,
    long UpTotal,
    long DownTotal);

public sealed record MemorySnapshot(
    long InUse,
    long OsLimit);

public sealed record ProxyItem(
    string Name,
    string Type,
    bool Alive,
    int? Delay,
    string? Now,
    IReadOnlyList<string>? All,
    string? TestUrl,
    string? Icon);

public sealed record ProxyGroup(
    string Name,
    string Type,
    string? Now,
    IReadOnlyList<string> All,
    string? TestUrl,
    string? Icon,
    bool Hidden);

public sealed record ConnectionMetadata(
    string Network,
    string Type,
    string SourceIP,
    string DestinationIP,
    string SourcePort,
    string DestinationPort,
    string Host,
    string Process,
    string ProcessPath,
    string Rule,
    string RulePayload);

public sealed record ConnectionItem(
    string Id,
    ConnectionMetadata Metadata,
    long Upload,
    long Download,
    DateTimeOffset Start,
    IReadOnlyList<string> Chains,
    string Rule,
    string RulePayload);

public sealed record ConnectionsSnapshot(
    long DownloadTotal,
    long UploadTotal,
    long Memory,
    IReadOnlyList<ConnectionItem> Connections);

public sealed record RuleItem(
    int Index,
    string Type,
    string Payload,
    string Proxy,
    string? Size);

public sealed record LogEntry(
    string Type,
    string Payload)
{
    public string Text => $"[{Type}] {Payload}";
}

public sealed record RuntimeConfig(
    int Port,
    int SocksPort,
    int MixedPort,
    string Mode,
    string LogLevel,
    bool AllowLan,
    bool Ipv6);
