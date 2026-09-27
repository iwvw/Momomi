using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Momomi.Core.Models;

namespace Momomi.Core.Services;

public interface IMihomoApiClient : IDisposable
{
    string? Secret { get; }
    Task<string?> GetVersionAsync(CancellationToken ct = default);
    Task<RuntimeConfig?> GetConfigsAsync(CancellationToken ct = default);
    Task PatchConfigsAsync(object patch, CancellationToken ct = default);
    Task ReloadConfigAsync(string path, bool force = true, CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, ProxyItem>> GetProxiesAsync(CancellationToken ct = default);
    Task SelectProxyAsync(string group, string name, CancellationToken ct = default);
    Task<Dictionary<string, int>> GroupDelayAsync(string group, string url, int timeoutMs, CancellationToken ct = default);
    Task<int> ProxyDelayAsync(string name, string url, int timeoutMs, CancellationToken ct = default);
    Task<IReadOnlyList<RuleItem>> GetRulesAsync(CancellationToken ct = default);
    Task<ConnectionsSnapshot?> GetConnectionsAsync(CancellationToken ct = default);
    Task CloseConnectionAsync(string id, CancellationToken ct = default);
    Task CloseAllConnectionsAsync(CancellationToken ct = default);
    Task StartTrafficStreamAsync(Func<TrafficSnapshot, Task> onData, CancellationToken ct);
    Task StartMemoryStreamAsync(Func<MemorySnapshot, Task> onData, CancellationToken ct);
    Task StartConnectionsStreamAsync(Func<ConnectionsSnapshot, Task> onData, CancellationToken ct);
    Task StartLogStreamAsync(Func<LogEntry, Task> onData, CancellationToken ct);
    Task FlushFakeIpAsync(CancellationToken ct = default);
    Task FlushDnsAsync(CancellationToken ct = default);
    Task RestartAsync(CancellationToken ct = default);
}

public sealed class MihomoApiClient : IMihomoApiClient, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly Uri _baseUri;

    public string? Secret { get; }

    public MihomoApiClient(string baseAddress, string? secret)
    {
        Secret = secret;
        _baseUri = new Uri(baseAddress.TrimEnd('/') + "/");
        _http = new HttpClient
        {
            BaseAddress = _baseUri,
            Timeout = TimeSpan.FromSeconds(10),
        };
        if (!string.IsNullOrEmpty(secret))
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        }
    }

    public async Task<string?> GetVersionAsync(CancellationToken ct = default)
    {
        var doc = await GetJsonAsync("version", ct).ConfigureAwait(false);
        return doc?.RootElement.TryGetProperty("version", out var v) == true ? v.GetString() : null;
    }

    public async Task<RuntimeConfig?> GetConfigsAsync(CancellationToken ct = default)
    {
        var doc = await GetJsonAsync("configs", ct).ConfigureAwait(false);
        if (doc is null) return null;
        var root = doc.RootElement;
        return new RuntimeConfig(
            GetInt(root, "port"),
            GetInt(root, "socks-port"),
            GetInt(root, "mixed-port"),
            GetString(root, "mode") ?? "rule",
            GetString(root, "log-level") ?? "info",
            GetBool(root, "allow-lan"),
            GetBool(root, "ipv6"));
    }

    public async Task PatchConfigsAsync(object patch, CancellationToken ct = default)
    {
        using var content = JsonContent.Create(patch);
        using var request = new HttpRequestMessage(HttpMethod.Patch, "configs") { Content = content };
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public async Task ReloadConfigAsync(string path, bool force = true, CancellationToken ct = default)
    {
        var body = new { path, payload = "" };
        using var content = JsonContent.Create(body);
        using var request = new HttpRequestMessage(HttpMethod.Put, $"configs?force={(force ? "true" : "false")}")
        {
            Content = content,
        };
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public async Task<IReadOnlyDictionary<string, ProxyItem>> GetProxiesAsync(CancellationToken ct = default)
    {
        var doc = await GetJsonAsync("proxies", ct).ConfigureAwait(false);
        var result = new Dictionary<string, ProxyItem>();
        if (doc is null) return result;
        if (!doc.RootElement.TryGetProperty("proxies", out var proxies)) return result;

        foreach (var entry in proxies.EnumerateObject())
        {
            var v = entry.Value;
            int? delay = null;
            if (v.TryGetProperty("history", out var history) && history.GetArrayLength() > 0)
            {
                var last = history[history.GetArrayLength() - 1];
                if (last.TryGetProperty("delay", out var d) && d.ValueKind == JsonValueKind.Number)
                    delay = d.GetInt32();
            }

            List<string>? all = null;
            if (v.TryGetProperty("all", out var allEl) && allEl.ValueKind == JsonValueKind.Array)
                all = allEl.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList();

            result[entry.Name] = new ProxyItem(
                entry.Name,
                GetString(v, "type") ?? "",
                GetBool(v, "alive"),
                delay,
                GetString(v, "now"),
                all,
                GetString(v, "testUrl"),
                GetString(v, "icon"));
        }
        return result;
    }

    public async Task SelectProxyAsync(string group, string name, CancellationToken ct = default)
    {
        var body = new { name };
        using var content = JsonContent.Create(body);
        using var request = new HttpRequestMessage(HttpMethod.Put, $"proxies/{Uri.EscapeDataString(group)}")
        {
            Content = content,
        };
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public async Task<Dictionary<string, int>> GroupDelayAsync(string group, string url, int timeoutMs, CancellationToken ct = default)
    {
        var path = $"group/{Uri.EscapeDataString(group)}/delay?url={Uri.EscapeDataString(url)}&timeout={timeoutMs}";
        var doc = await GetJsonAsync(path, ct).ConfigureAwait(false);
        var result = new Dictionary<string, int>();
        if (doc is null) return result;
        foreach (var entry in doc.RootElement.EnumerateObject())
        {
            if (entry.Value.ValueKind == JsonValueKind.Number)
                result[entry.Name] = entry.Value.GetInt32();
        }
        return result;
    }

    public async Task<int> ProxyDelayAsync(string name, string url, int timeoutMs, CancellationToken ct = default)
    {
        var path = $"proxies/{Uri.EscapeDataString(name)}/delay?url={Uri.EscapeDataString(url)}&timeout={timeoutMs}";
        var doc = await GetJsonAsync(path, ct).ConfigureAwait(false);
        if (doc?.RootElement.TryGetProperty("delay", out var d) == true && d.ValueKind == JsonValueKind.Number)
            return d.GetInt32();
        return -1;
    }

    public async Task<IReadOnlyList<RuleItem>> GetRulesAsync(CancellationToken ct = default)
    {
        var doc = await GetJsonAsync("rules", ct).ConfigureAwait(false);
        var list = new List<RuleItem>();
        if (doc?.RootElement.TryGetProperty("rules", out var rules) != true) return list;
        foreach (var r in rules.EnumerateArray())
        {
            list.Add(new RuleItem(
                GetInt(r, "index"),
                GetString(r, "type") ?? "",
                GetString(r, "payload") ?? "",
                GetString(r, "proxy") ?? "",
                r.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64().ToString() : null));
        }
        return list;
    }

    public async Task<ConnectionsSnapshot?> GetConnectionsAsync(CancellationToken ct = default)
    {
        var doc = await GetJsonAsync("connections", ct).ConfigureAwait(false);
        return doc is null ? null : ParseConnections(doc.RootElement);
    }

    public async Task CloseConnectionAsync(string id, CancellationToken ct = default)
    {
        using var response = await _http.DeleteAsync($"connections/{Uri.EscapeDataString(id)}", ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public async Task CloseAllConnectionsAsync(CancellationToken ct = default)
    {
        using var response = await _http.DeleteAsync("connections", ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public Task StartTrafficStreamAsync(Func<TrafficSnapshot, Task> onData, CancellationToken ct)
        => StreamAsync("traffic", root =>
        {
            var snap = new TrafficSnapshot(
                GetLong(root, "up"),
                GetLong(root, "down"),
                GetLong(root, "upTotal"),
                GetLong(root, "downTotal"));
            return onData(snap);
        }, ct);

    public Task StartMemoryStreamAsync(Func<MemorySnapshot, Task> onData, CancellationToken ct)
        => StreamAsync("memory", root =>
        {
            var snap = new MemorySnapshot(GetLong(root, "inuse"), GetLong(root, "oslimit"));
            return onData(snap);
        }, ct);

    public Task StartConnectionsStreamAsync(Func<ConnectionsSnapshot, Task> onData, CancellationToken ct)
        => StreamAsync("connections", root => onData(ParseConnections(root)), ct);

    public Task StartLogStreamAsync(Func<LogEntry, Task> onData, CancellationToken ct)
        => StreamAsync("logs?level=info", root =>
        {
            var entry = new LogEntry(GetString(root, "type") ?? "info", GetString(root, "payload") ?? "");
            return onData(entry);
        }, ct);

    public async Task FlushFakeIpAsync(CancellationToken ct = default)
    {
        using var response = await _http.PostAsync("cache/fakeip/flush", null, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public async Task FlushDnsAsync(CancellationToken ct = default)
    {
        using var response = await _http.PostAsync("cache/dns/flush", null, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public async Task RestartAsync(CancellationToken ct = default)
    {
        using var response = await _http.PostAsync("restart", null, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    private async Task<JsonDocument?> GetJsonAsync(string path, CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(path, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private async Task StreamAsync(string path, Func<JsonElement, Task> onItem, CancellationToken ct)
    {
        var url = new Uri(_baseUri, path);
        if (!string.IsNullOrEmpty(Secret))
            url = new Uri(url + (path.Contains('?') ? "&" : "?") + "token=" + Uri.EscapeDataString(Secret));

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream);

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) break;
            if (line.Length == 0) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                await onItem(doc.RootElement.Clone()).ConfigureAwait(false);
            }
            catch (JsonException)
            {
            }
        }
    }

    private static ConnectionsSnapshot ParseConnections(JsonElement root)
    {
        var list = new List<ConnectionItem>();
        if (root.TryGetProperty("connections", out var conns) && conns.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in conns.EnumerateArray())
            {
                var m = c.TryGetProperty("metadata", out var meta) ? meta : default;
                var metadata = new ConnectionMetadata(
                    GetString(m, "network") ?? "",
                    GetString(m, "type") ?? "",
                    GetString(m, "sourceIP") ?? "",
                    GetString(m, "destinationIP") ?? "",
                    GetString(m, "sourcePort") ?? "",
                    GetString(m, "destinationPort") ?? "",
                    GetString(m, "host") ?? "",
                    GetString(m, "process") ?? "",
                    GetString(m, "processPath") ?? "",
                    GetString(m, "rule") ?? "",
                    GetString(m, "rulePayload") ?? "");

                var chains = new List<string>();
                if (c.TryGetProperty("chains", out var ch) && ch.ValueKind == JsonValueKind.Array)
                    chains = ch.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList();

                var start = DateTimeOffset.Now;
                if (c.TryGetProperty("start", out var st) && st.ValueKind == JsonValueKind.String &&
                    DateTimeOffset.TryParse(st.GetString(), out var parsed))
                    start = parsed;

                list.Add(new ConnectionItem(
                    GetString(c, "id") ?? "",
                    metadata,
                    GetLong(c, "upload"),
                    GetLong(c, "download"),
                    start,
                    chains,
                    GetString(c, "rule") ?? "",
                    GetString(c, "rulePayload") ?? ""));
            }
        }

        return new ConnectionsSnapshot(
            GetLong(root, "downloadTotal"),
            GetLong(root, "uploadTotal"),
            GetLong(root, "memory"),
            list);
    }

    private static string? GetString(JsonElement el, string name)
        => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static int GetInt(JsonElement el, string name)
        => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32()
            : 0;

    private static long GetLong(JsonElement el, string name)
        => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt64()
            : 0;

    private static bool GetBool(JsonElement el, string name)
        => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    public void Dispose() => _http.Dispose();
}
