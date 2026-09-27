using System.Net.Http.Headers;
using System.Text;
using Momomi.Data;

namespace Momomi.Core.Services;

public sealed record ProfileItem(
    long Id,
    string Name,
    string Kind,
    string? Source,
    string FilePath,
    string? SubscriptionUserInfo,
    bool IsActive,
    int SortOrder,
    string UpdatedAt)
{
    public string KindText => Kind switch
    {
        "url" => "订阅链接",
        "file" => "本地文件",
        "manual" => "手动配置",
        _ => Kind,
    };

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

public interface IProfileService
{
    string ProfilesDirectory { get; }
    Task<IReadOnlyList<ProfileItem>> ListAsync(CancellationToken ct = default);
    Task<ProfileItem?> GetActiveAsync(CancellationToken ct = default);
    Task<ProfileItem> ImportFromUrlAsync(string url, string? name = null, CancellationToken ct = default);
    Task<ProfileItem> ImportFromFileAsync(string sourcePath, string? name = null, CancellationToken ct = default);
    Task<ProfileItem> CreateManualAsync(string name, string content, CancellationToken ct = default);
    Task<bool> RefreshAsync(long id, CancellationToken ct = default);
    Task<bool> SetActiveAsync(long id, CancellationToken ct = default);
    Task RenameAsync(long id, string name, CancellationToken ct = default);
    Task DeleteAsync(long id, CancellationToken ct = default);
    Task<string?> ReadContentAsync(long id, CancellationToken ct = default);
    Task<bool> SaveContentAsync(long id, string content, CancellationToken ct = default);
    Task<string> GenerateRuntimeConfigAsync(long id, RuntimeYamlOptions options, CancellationToken ct = default);
}

public sealed class ProfileService : IProfileService
{
    private static readonly HttpClient Http = CreateClient();

    private readonly ProfileRepository _repo;
    private readonly ISettingsService _settings;

    public string ProfilesDirectory { get; }

    public ProfileService(MomomiDatabase database, ISettingsService settings, string profilesDirectory)
    {
        _repo = new ProfileRepository(database);
        _settings = settings;
        ProfilesDirectory = profilesDirectory;
        Directory.CreateDirectory(profilesDirectory);
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new HttpClientHandler
        {
            // 内核运行时经本机混合端口下载订阅（借力自身代理）。
            Proxy = DownloadProxy.Create(),
            UseProxy = true,
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        client.DefaultRequestHeaders.Accept.ParseAdd("*/*");
        return client;
    }

    private const string DefaultUserAgent = "clash-verge/v1.0.0 Momomi/0.1.0";
    private const int DefaultTimeoutSeconds = 60;

    /// <summary>读取订阅请求的 User-Agent 与超时，均可在设置里覆盖。</summary>
    private async Task<(string UserAgent, TimeSpan Timeout)> ReadRequestOptionsAsync(CancellationToken ct)
    {
        var ua = await _settings.GetAsync("core.subscriptionUserAgent").ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(ua)) ua = DefaultUserAgent;

        var seconds = await _settings.GetIntAsync("core.subscriptionTimeout", DefaultTimeoutSeconds).ConfigureAwait(false);
        if (seconds <= 0) seconds = DefaultTimeoutSeconds;

        return (ua!, TimeSpan.FromSeconds(seconds));
    }

    /// <summary>带 User-Agent 与超时地下载订阅内容。</summary>
    private async Task<string> FetchContentAsync(string url, CancellationToken ct)
    {
        var (ua, timeout) = await ReadRequestOptionsAsync(ct).ConfigureAwait(false);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeout);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(ua);
        using var response = await Http.SendAsync(request, linked.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
    }

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
            sb.Append(invalid.Contains(c) ? '_' : c);
        var result = sb.ToString().Trim();
        return result.Length == 0 ? "profile" : result;
    }

    public async Task<IReadOnlyList<ProfileItem>> ListAsync(CancellationToken ct = default)
    {
        var records = await _repo.ListAsync().ConfigureAwait(false);
        return records.Select(ToItem).ToList();
    }

    public async Task<ProfileItem?> GetActiveAsync(CancellationToken ct = default)
    {
        var record = await _repo.GetActiveAsync().ConfigureAwait(false);
        return record is null ? null : ToItem(record);
    }

    private static ProfileItem ToItem(ProfileRecord r)
        => new(r.Id, r.Name, r.Kind, r.Source, r.FilePath, r.SubscriptionUserInfo, r.IsActive, r.SortOrder, r.UpdatedAt);

    public async Task<ProfileItem> ImportFromUrlAsync(string url, string? name = null, CancellationToken ct = default)
    {
        var meta = await FetchSubscriptionMetaAsync(url, ct).ConfigureAwait(false);
        var displayName = string.IsNullOrWhiteSpace(name)
            ? meta.Name ?? $"订阅 {DateTime.Now:MM-dd HH:mm}"
            : name!.Trim();
        var content = await FetchContentAsync(url, ct).ConfigureAwait(false);

        var fileName = $"{SafeFileName(displayName)}-{DateTimeOffset.Now.ToUnixTimeSeconds()}.yaml";
        var filePath = Path.Combine(ProfilesDirectory, fileName);
        await File.WriteAllTextAsync(filePath, content, ct).ConfigureAwait(false);

        var id = await _repo.InsertAsync(displayName, "url", url, filePath).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(meta.UserInfo))
            await _repo.UpdateContentAsync(id, meta.UserInfo).ConfigureAwait(false);

        var list = await ListAsync(ct).ConfigureAwait(false);
        if (list.Count == 1)
            await _repo.SetActiveAsync(id).ConfigureAwait(false);

        return (await ListAsync(ct).ConfigureAwait(false)).First(p => p.Id == id);
    }

    private sealed record SubscriptionMeta(string? Name, string? UserInfo);

    /// <summary>
    /// 读取订阅响应头里的元信息：Profile-Title（base64，优先级最高）、
    /// Content-Disposition 的文件名，以及 Subscription-Userinfo 用量。
    /// </summary>
    private async Task<SubscriptionMeta> FetchSubscriptionMetaAsync(string url, CancellationToken ct)
    {
        try
        {
            var (ua, timeout) = await ReadRequestOptionsAsync(ct).ConfigureAwait(false);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(timeout);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd(ua);
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);

            string? userInfo = null;
            if (response.Headers.TryGetValues("subscription-userinfo", out var values))
                userInfo = string.Join("; ", values);

            string? name = null;
            if (response.Headers.TryGetValues("profile-title", out var titleValues))
            {
                var raw = string.Join("", titleValues).Trim();
                name = DecodeProfileTitle(raw);
            }

            if (string.IsNullOrWhiteSpace(name)
                && response.Content.Headers.TryGetValues("Content-Disposition", out var cdValues))
            {
                name = ParseFileName(string.Join(";", cdValues));
            }

            return new SubscriptionMeta(name, userInfo);
        }
        catch
        {
            return new SubscriptionMeta(null, null);
        }
    }

    private static string? DecodeProfileTitle(string raw)
    {
        if (raw.StartsWith("base64:", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var bytes = Convert.FromBase64String(raw["base64:".Length..].Trim());
                return System.Text.Encoding.UTF8.GetString(bytes).Trim();
            }
            catch
            {
                return null;
            }
        }
        return string.IsNullOrWhiteSpace(raw) ? null : raw;
    }

    private static string? ParseFileName(string contentDisposition)
    {
        foreach (var part in contentDisposition.Split(';'))
        {
            var trimmed = part.Trim();
            if (trimmed.StartsWith("filename*=", StringComparison.OrdinalIgnoreCase))
            {
                var value = trimmed["filename*=".Length..].Trim().Trim('"');
                var idx = value.IndexOf("''", StringComparison.Ordinal);
                if (idx >= 0) value = Uri.UnescapeDataString(value[(idx + 2)..]);
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
            if (trimmed.StartsWith("filename=", StringComparison.OrdinalIgnoreCase))
            {
                var value = trimmed["filename=".Length..].Trim().Trim('"');
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
        }
        return null;
    }

    public async Task<ProfileItem> ImportFromFileAsync(string sourcePath, string? name = null, CancellationToken ct = default)
    {
        var displayName = string.IsNullOrWhiteSpace(name)
            ? Path.GetFileNameWithoutExtension(sourcePath)
            : name!.Trim();
        var content = await File.ReadAllTextAsync(sourcePath, ct).ConfigureAwait(false);

        var fileName = $"{SafeFileName(displayName)}-{DateTimeOffset.Now.ToUnixTimeSeconds()}.yaml";
        var filePath = Path.Combine(ProfilesDirectory, fileName);
        await File.WriteAllTextAsync(filePath, content, ct).ConfigureAwait(false);

        var id = await _repo.InsertAsync(displayName, "file", sourcePath, filePath).ConfigureAwait(false);
        var list = await ListAsync(ct).ConfigureAwait(false);
        if (list.Count == 1)
            await _repo.SetActiveAsync(id).ConfigureAwait(false);

        return (await ListAsync(ct).ConfigureAwait(false)).First(p => p.Id == id);
    }

    public async Task<ProfileItem> CreateManualAsync(string name, string content, CancellationToken ct = default)
    {
        var displayName = string.IsNullOrWhiteSpace(name) ? "手动配置" : name.Trim();
        var fileName = $"{SafeFileName(displayName)}-{DateTimeOffset.Now.ToUnixTimeSeconds()}.yaml";
        var filePath = Path.Combine(ProfilesDirectory, fileName);
        await File.WriteAllTextAsync(filePath, content, ct).ConfigureAwait(false);

        var id = await _repo.InsertAsync(displayName, "manual", null, filePath).ConfigureAwait(false);
        var list = await ListAsync(ct).ConfigureAwait(false);
        if (list.Count == 1)
            await _repo.SetActiveAsync(id).ConfigureAwait(false);

        return (await ListAsync(ct).ConfigureAwait(false)).First(p => p.Id == id);
    }

    public async Task<bool> RefreshAsync(long id, CancellationToken ct = default)
    {
        var item = (await ListAsync(ct).ConfigureAwait(false)).FirstOrDefault(p => p.Id == id);
        if (item is null || item.Kind != "url" || string.IsNullOrEmpty(item.Source)) return false;

        try
        {
            var meta = await FetchSubscriptionMetaAsync(item.Source, ct).ConfigureAwait(false);
            var content = await FetchContentAsync(item.Source, ct).ConfigureAwait(false);
            await File.WriteAllTextAsync(item.FilePath, content, ct).ConfigureAwait(false);
            await _repo.UpdateContentAsync(id, meta.UserInfo).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> SetActiveAsync(long id, CancellationToken ct = default)
    {
        await _repo.SetActiveAsync(id).ConfigureAwait(false);
        return true;
    }

    public Task RenameAsync(long id, string name, CancellationToken ct = default)
        => _repo.RenameAsync(id, name);

    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        var item = (await ListAsync(ct).ConfigureAwait(false)).FirstOrDefault(p => p.Id == id);
        if (item is null) return;

        await _repo.DeleteAsync(id).ConfigureAwait(false);
        try
        {
            if (File.Exists(item.FilePath)) File.Delete(item.FilePath);
        }
        catch
        {
        }

        var remaining = await ListAsync(ct).ConfigureAwait(false);
        if (remaining.Count > 0 && !remaining.Any(p => p.IsActive))
            await _repo.SetActiveAsync(remaining[0].Id).ConfigureAwait(false);
    }

    public async Task<string?> ReadContentAsync(long id, CancellationToken ct = default)
    {
        var item = (await ListAsync(ct).ConfigureAwait(false)).FirstOrDefault(p => p.Id == id);
        if (item is null || !File.Exists(item.FilePath)) return null;
        return await File.ReadAllTextAsync(item.FilePath, ct).ConfigureAwait(false);
    }

    public async Task<bool> SaveContentAsync(long id, string content, CancellationToken ct = default)
    {
        var item = (await ListAsync(ct).ConfigureAwait(false)).FirstOrDefault(p => p.Id == id);
        if (item is null) return false;

        try
        {
            await File.WriteAllTextAsync(item.FilePath, content, ct).ConfigureAwait(false);
            await _repo.UpdateContentAsync(id).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<string> GenerateRuntimeConfigAsync(long id, RuntimeYamlOptions options, CancellationToken ct = default)
    {
        var content = await ReadContentAsync(id, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(content))
            return MihomoConfigBuilder.BuildFallbackYaml(options);

        return MihomoConfigBuilder.BuildRuntimeYaml(content, options);
    }
}
