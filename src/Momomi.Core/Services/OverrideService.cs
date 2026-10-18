using System.Text;
using Momomi.Data;

namespace Momomi.Core.Services;

/// <summary>覆写文件列表项。</summary>
public sealed record OverrideItem(
    long Id,
    string Name,
    string Kind,
    string? Source,
    string FilePath,
    bool IsGlobal,
    int SortOrder,
    string UpdatedAt)
{
    public string UpdatedText
    {
        get
        {
            if (DateTimeOffset.TryParse(UpdatedAt, out var ts))
                return ts.LocalDateTime.ToString("yyyy-MM-dd HH:mm");
            return UpdatedAt;
        }
    }
}

/// <summary>新建覆写时可选的预制模板。</summary>
public sealed record OverrideTemplate(string Key, string Name, string Description, string Content);

public interface IOverrideService
{
    string OverridesDirectory { get; }

    Task<IReadOnlyList<OverrideItem>> ListAsync(CancellationToken ct = default);
    Task<OverrideItem?> GetAsync(long id, CancellationToken ct = default);
    Task<string?> ReadContentAsync(long id, CancellationToken ct = default);
    Task<bool> SaveContentAsync(long id, string content, CancellationToken ct = default);

    Task<OverrideItem> CreateAsync(string name, string? content = null, bool isGlobal = false, CancellationToken ct = default);
    Task<OverrideItem> ImportFromFileAsync(string sourcePath, string? name = null, CancellationToken ct = default);
    Task<OverrideItem> ImportFromUrlAsync(string url, string? name = null, CancellationToken ct = default);
    Task RenameAsync(long id, string name, CancellationToken ct = default);
    Task SetGlobalAsync(long id, bool isGlobal, CancellationToken ct = default);
    Task DeleteAsync(long id, CancellationToken ct = default);

    Task<IReadOnlyList<long>> GetProfileOverridesAsync(long profileId, CancellationToken ct = default);
    Task SetProfileOverridesAsync(long profileId, IReadOnlyList<long> overrideIds, CancellationToken ct = default);

    /// <summary>
    /// 按"全局覆写 → 订阅挂载覆写"的顺序，取出当前生效的所有覆写内容。
    /// </summary>
    Task<IReadOnlyList<OverrideDocument>> ResolveActiveDocumentsAsync(
        long? profileId, CancellationToken ct = default);

    IReadOnlyList<OverrideTemplate> Templates { get; }
}

public sealed class OverrideService : IOverrideService
{
    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        Proxy = DownloadProxy.Create(),
        UseProxy = true,
    })
    {
        Timeout = TimeSpan.FromSeconds(60),
    };

    private readonly OverrideRepository _repo;

    public string OverridesDirectory { get; }

    public OverrideService(MomomiDatabase database, string overridesDirectory)
    {
        _repo = new OverrideRepository(database);
        OverridesDirectory = overridesDirectory;
        Directory.CreateDirectory(overridesDirectory);
    }

    public IReadOnlyList<OverrideTemplate> Templates { get; } = new List<OverrideTemplate>
    {
        new("direct", "添加直连规则",
            "让指定的域名绕开代理直接连接，适合银行、政务、内网类站点。",
            """
            # 这些域名走直连。把下面几行换成你需要直连的域名即可。
            +rules:
              - "DOMAIN-SUFFIX,baidu.com,DIRECT"
              - "DOMAIN-SUFFIX,qq.com,DIRECT"
              - "DOMAIN-SUFFIX,taobao.com,DIRECT"
            """),
        new("proxy", "强制走代理",
            "订阅规则里没有的站点，强制交给代理组处理。",
            """
            # 强制走代理。GLOBAL 是 mihomo 内置的全局出口，
            # 想改用订阅里的代理组，把 GLOBAL 换成组名即可，例如 "🚀 节点选择"。
            +rules:
              - "DOMAIN-SUFFIX,openai.com,GLOBAL"
              - "DOMAIN-SUFFIX,anthropic.com,GLOBAL"
              - "DOMAIN-SUFFIX,github.com,GLOBAL"
            """),
        new("reject", "拦截广告与追踪",
            "把常见广告、遥测域名直接拒绝连接。",
            """
            +rules:
              - "DOMAIN-KEYWORD,doubleclick,REJECT"
              - "DOMAIN-SUFFIX,google-analytics.com,REJECT"
              - "DOMAIN-SUFFIX,googletagmanager.com,REJECT"
              - "DOMAIN-SUFFIX,umeng.com,REJECT"
            """),
        new("dns", "自定义 DNS",
            "替换订阅自带的 DNS 段。用 ! 强制整体覆盖，不做递归合并。",
            """
            # 键名以 ! 结尾表示整体替换，避免和订阅里的 dns 设置混合出怪结果。
            dns!:
              enable: true
              ipv6: false
              enhanced-mode: fake-ip
              fake-ip-range: 198.18.0.1/16
              default-nameserver:
                - 223.5.5.5
                - 119.29.29.29
              nameserver:
                - https://223.5.5.5/dns-query
                - https://doh.pub/dns-query
              proxy-server-nameserver:
                - 223.5.5.5
                - 119.29.29.29
            """),
        new("sniffer", "开启域名嗅探",
            "TUN 模式下按 IP 连接的流量也能识别出真实域名，规则分流更准。",
            """
            sniffer:
              enable: true
              force-dns-mapping: true
              parse-pure-ip: true
              override-destination: false
              sniff:
                HTTP:
                  ports: [80, 8080-8880]
                TLS:
                  ports: [443, 8443]
                QUIC:
                  ports: [443, 8443]
            """),
    };

    public async Task<IReadOnlyList<OverrideItem>> ListAsync(CancellationToken ct = default)
    {
        var records = await _repo.ListAsync().ConfigureAwait(false);
        return records.Select(ToItem).ToList();
    }

    public async Task<OverrideItem?> GetAsync(long id, CancellationToken ct = default)
        => (await ListAsync(ct).ConfigureAwait(false)).FirstOrDefault(p => p.Id == id);

    private static OverrideItem ToItem(OverrideRecord r)
        => new(r.Id, r.Name, r.Kind, r.Source, r.FilePath, r.IsGlobal, r.SortOrder, r.UpdatedAt);

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
            sb.Append(invalid.Contains(c) ? '_' : c);
        var result = sb.ToString().Trim();
        return result.Length == 0 ? "override" : result;
    }

    private string NewFilePath(string name)
        => Path.Combine(OverridesDirectory, $"{SafeFileName(name)}-{DateTimeOffset.Now.ToUnixTimeSeconds()}.yaml");

    public async Task<string?> ReadContentAsync(long id, CancellationToken ct = default)
    {
        var item = await GetAsync(id, ct).ConfigureAwait(false);
        if (item is null || !File.Exists(item.FilePath)) return null;
        return await File.ReadAllTextAsync(item.FilePath, ct).ConfigureAwait(false);
    }

    public async Task<bool> SaveContentAsync(long id, string content, CancellationToken ct = default)
    {
        var item = await GetAsync(id, ct).ConfigureAwait(false);
        if (item is null) return false;

        try
        {
            await File.WriteAllTextAsync(item.FilePath, content, ct).ConfigureAwait(false);
            await _repo.TouchAsync(id).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<OverrideItem> CreateAsync(
        string name, string? content = null, bool isGlobal = false, CancellationToken ct = default)
    {
        var displayName = string.IsNullOrWhiteSpace(name) ? "新覆写" : name.Trim();
        var filePath = NewFilePath(displayName);
        await File.WriteAllTextAsync(filePath, content ?? DefaultContent, ct).ConfigureAwait(false);

        var id = await _repo.InsertAsync(displayName, "yaml", null, filePath, isGlobal).ConfigureAwait(false);
        return (await GetAsync(id, ct).ConfigureAwait(false))!;
    }

    private const string DefaultContent = """
        # 覆写会叠加在订阅配置之上，订阅刷新后依然生效。
        #
        #   key: value    简单值直接覆盖，嵌套映射递归合并
        #   key!:         整体替换该键，不递归合并
        #   rules:        整个数组替换
        #   +rules:       插到原数组最前面
        #   rules+:       追加到原数组最后
        #
        # 例如：让 example.com 直连
        +rules:
          - "DOMAIN-SUFFIX,example.com,DIRECT"
        """;

    public async Task<OverrideItem> ImportFromFileAsync(
        string sourcePath, string? name = null, CancellationToken ct = default)
    {
        var displayName = string.IsNullOrWhiteSpace(name)
            ? Path.GetFileNameWithoutExtension(sourcePath)
            : name!.Trim();
        var content = await File.ReadAllTextAsync(sourcePath, ct).ConfigureAwait(false);
        AssertValid(content, displayName);
        return await CreateAsync(displayName, content, false, ct).ConfigureAwait(false);
    }

    public async Task<OverrideItem> ImportFromUrlAsync(
        string url, string? name = null, CancellationToken ct = default)
    {
        using var response = await Http.GetAsync(url, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        var displayName = name;
        if (string.IsNullOrWhiteSpace(displayName))
        {
            var fromUrl = Path.GetFileNameWithoutExtension(new Uri(url).AbsolutePath);
            displayName = string.IsNullOrWhiteSpace(fromUrl) ? "远程覆写" : fromUrl;
        }

        AssertValid(content, displayName!);
        return await CreateAsync(displayName!, content, false, ct).ConfigureAwait(false);
    }

    private static void AssertValid(string content, string name)
    {
        var error = YamlOverrideMerger.Validate(content);
        if (error is not null)
            throw new YamlOverrideException($"{name}：{error}");
    }

    public async Task RenameAsync(long id, string name, CancellationToken ct = default)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0) return;
        await _repo.RenameAsync(id, trimmed).ConfigureAwait(false);
    }

    public Task SetGlobalAsync(long id, bool isGlobal, CancellationToken ct = default)
        => _repo.SetGlobalAsync(id, isGlobal);

    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        var item = await GetAsync(id, ct).ConfigureAwait(false);
        if (item is null) return;

        await _repo.DeleteAsync(id).ConfigureAwait(false);
        try
        {
            if (File.Exists(item.FilePath)) File.Delete(item.FilePath);
        }
        catch
        {
        }
    }

    public Task<IReadOnlyList<long>> GetProfileOverridesAsync(long profileId, CancellationToken ct = default)
        => _repo.GetProfileOverridesAsync(profileId);

    public Task SetProfileOverridesAsync(long profileId, IReadOnlyList<long> overrideIds, CancellationToken ct = default)
        => _repo.SetProfileOverridesAsync(profileId, overrideIds);

    public async Task<IReadOnlyList<OverrideDocument>> ResolveActiveDocumentsAsync(
        long? profileId, CancellationToken ct = default)
    {
        var all = await ListAsync(ct).ConfigureAwait(false);
        var byId = all.ToDictionary(i => i.Id);

        // 全局启用的覆写先应用，随后是该订阅挂载的，后者优先级更高。
        var ordered = new List<OverrideItem>();
        ordered.AddRange(all.Where(i => i.IsGlobal));

        if (profileId is not null)
        {
            foreach (var id in await GetProfileOverridesAsync(profileId.Value, ct).ConfigureAwait(false))
            {
                // 已经作为全局覆写应用过的，不再重复合并。
                if (byId.TryGetValue(id, out var item) && !item.IsGlobal)
                    ordered.Add(item);
            }
        }

        var docs = new List<OverrideDocument>();
        foreach (var item in ordered)
        {
            if (!File.Exists(item.FilePath)) continue;
            var content = await File.ReadAllTextAsync(item.FilePath, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(content)) continue;
            docs.Add(new OverrideDocument(item.Name, content));
        }
        return docs;
    }
}
