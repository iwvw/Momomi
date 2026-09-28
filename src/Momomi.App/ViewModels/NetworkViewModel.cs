using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Momomi.Core.Services;

namespace Momomi.App.ViewModels;

public sealed partial class LatencyTargetViewModel : ObservableObject
{
    public string Name { get; }
    public string Url { get; }

    [ObservableProperty]
    public partial string LatencyText { get; set; } = "—";

    [ObservableProperty]
    public partial bool IsTesting { get; set; }

    /// <summary>延迟毫秒；-1=超时/失败，null=未测。</summary>
    public long? LatencyMs { get; private set; }

    public LatencyTargetViewModel(string name, string url)
    {
        Name = name;
        Url = url;
    }

    public void SetResult(long? ms)
    {
        LatencyMs = ms;
        LatencyText = ms is null ? "超时" : $"{ms} ms";
    }
}

public sealed partial class NetworkViewModel : ObservableObject
{
    private readonly MomomiHost _host;
    private readonly DispatcherQueue _dispatcher;

    public ObservableCollection<LatencyTargetViewModel> Targets { get; } = new();

    [ObservableProperty]
    public partial string StatusText { get; set; } = "未检测";

    [ObservableProperty]
    public partial string ExitIp { get; set; } = "—";

    [ObservableProperty]
    public partial string ExitCountry { get; set; } = "";

    [ObservableProperty]
    public partial bool HasExitIp { get; set; }

    [ObservableProperty]
    public partial bool IsCheckingExit { get; set; }

    [ObservableProperty]
    public partial bool IsTestingAll { get; set; }

    public NetworkViewModel(MomomiHost host, DispatcherQueue dispatcher)
    {
        _host = host;
        _dispatcher = dispatcher;
        Targets.Add(new LatencyTargetViewModel("Google", "https://www.gstatic.com/generate_204"));
        Targets.Add(new LatencyTargetViewModel("Cloudflare", "https://cloudflare.com/cdn-cgi/trace"));
        Targets.Add(new LatencyTargetViewModel("GitHub", "https://github.com"));
        Targets.Add(new LatencyTargetViewModel("YouTube", "https://www.youtube.com"));
        Targets.Add(new LatencyTargetViewModel("Wikipedia", "https://www.wikipedia.org"));
    }

    public async Task RefreshAsync()
    {
        StatusText = "正在检测…";
        await CheckExitIpAsync();
        await TestAllAsync();
    }

    /// <summary>经当前代理获取出口 IP 与归属地（多源回退）。</summary>
    [RelayCommand]
    public async Task CheckExitIpAsync()
    {
        if (IsCheckingExit) return;
        IsCheckingExit = true;
        try
        {
            var ip = await QueryExitIpAsync();
            var (country, region) = await QueryGeoAsync(ip);
            _dispatcher.TryEnqueue(() =>
            {
                ExitIp = ip;
                ExitCountry = string.IsNullOrEmpty(country) ? "" : $"{country} · {region}";
                HasExitIp = true;
                StatusText = "检测完成";
            });
        }
        catch (Exception ex)
        {
            _dispatcher.TryEnqueue(() => StatusText = $"出口检测失败：{ex.Message}");
        }
        finally
        {
            IsCheckingExit = false;
        }
    }

    private async Task<string> QueryExitIpAsync()
    {
        using var client = MakeClient();
        foreach (var url in new[] { "https://api.ipify.org", "https://ipv4.icanhazip.com", "https://ifconfig.me/ip" })
        {
            try
            {
                var text = (await client.GetStringAsync(url).ConfigureAwait(false)).Trim();
                if (!string.IsNullOrEmpty(text)) return text;
            }
            catch
            {
            }
        }
        throw new InvalidOperationException("所有 IP 源均不可达");
    }

    private async Task<(string Country, string Region)> QueryGeoAsync(string ip)
    {
        try
        {
            using var client = MakeClient();
            var json = await client.GetStringAsync($"http://ip-api.com/json/{ip}").ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var country = root.TryGetProperty("country", out var c) ? c.GetString() : null;
            var region = root.TryGetProperty("regionName", out var r) ? r.GetString() : null;
            return (country ?? "", region ?? "");
        }
        catch
        {
            return ("", "");
        }
    }

    /// <summary>逐个测试所有目标延迟。</summary>
    [RelayCommand]
    public async Task TestAllAsync()
    {
        if (IsTestingAll) return;
        IsTestingAll = true;
        try
        {
            foreach (var target in Targets)
                await TestOneAsync(target).ConfigureAwait(false);
            _dispatcher.TryEnqueue(() => StatusText = "检测完成");
        }
        finally
        {
            IsTestingAll = false;
        }
    }

    private async Task TestOneAsync(LatencyTargetViewModel target)
    {
        target.IsTesting = true;
        try
        {
            using var client = MakeClient();
            var sw = Stopwatch.StartNew();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            using var response = await client.GetAsync(target.Url, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            sw.Stop();
            var ms = sw.ElapsedMilliseconds;
            _dispatcher.TryEnqueue(() => target.SetResult(ms));
        }
        catch
        {
            _dispatcher.TryEnqueue(() => target.SetResult(null));
        }
        finally
        {
            target.IsTesting = false;
        }
    }

    private HttpClient MakeClient()
    {
        var handler = new HttpClientHandler { Proxy = DownloadProxy.Create() };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
    }
}
