using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;

namespace Momomi.Core.Services;

public sealed record AppUpdateInfo(
    string CurrentVersion,
    string? LatestVersion,
    bool HasUpdate,
    string? ReleaseUrl,
    string? DownloadUrl,
    string? Error);

public interface IAppUpdateService
{
    /// <summary>当前是否安装版（由 Inno Setup 安装）。便携版为 false。</summary>
    bool IsInstalled { get; }

    string CurrentVersion { get; }

    AppUpdateInfo? LastResult { get; }

    Task<AppUpdateInfo> CheckAsync(CancellationToken ct = default);

    /// <summary>下载匹配当前形态/架构的发布产物，并生成静默更新脚本，返回更新信息。</summary>
    Task<AppUpdateInfo> PrepareUpdateAsync(IProgress<double>? progress = null, CancellationToken ct = default);

    /// <summary>取走待执行的更新脚本路径（取走后清空）。无待更新脚本时返回 null。</summary>
    string? ConsumePendingScript();
}

public sealed class AppUpdateService : IAppUpdateService
{
    private const string ReleasesApi = "https://api.github.com/repos/iwvw/Momomi/releases/latest";

    private readonly HttpClient _http;
    private readonly ISettingsService? _settings;
    private string? _pendingScript;

    public AppUpdateService(ISettingsService? settings = null)
    {
        _settings = settings;
        _http = new HttpClient(new HttpClientHandler
        {
            Proxy = DownloadProxy.Create(),
            UseProxy = true,
        })
        {
            Timeout = TimeSpan.FromMinutes(5),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Momomi/" + CurrentVersion);
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public static AppUpdateInfo? LastResult { get; private set; }

    AppUpdateInfo? IAppUpdateService.LastResult => LastResult;

    public static string GetCurrentVersion()
    {
        try
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            return v is null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
        catch
        {
            return "0.0.0";
        }
    }

    public string CurrentVersion => GetCurrentVersion();

    public bool IsInstalled
        => File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe"));

    public async Task<AppUpdateInfo> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            var json = await FetchLatestAsync(ct).ConfigureAwait(false);
            var (tag, htmlUrl, assets) = ParseLatest(json);

            var latest = tag?.TrimStart('v');
            var hasUpdate = false;
            if (!string.IsNullOrEmpty(latest)
                && Version.TryParse(latest, out var latestV)
                && Version.TryParse(CurrentVersion, out var currentV))
                hasUpdate = latestV > currentV;

            var downloadUrl = SelectAssetUrl(latest!, assets);

            var result = new AppUpdateInfo(CurrentVersion, latest, hasUpdate, htmlUrl, downloadUrl, null);
            LastResult = result;
            return result;
        }
        catch (Exception ex)
        {
            var result = new AppUpdateInfo(CurrentVersion, null, false, null, null, ex.Message);
            LastResult = result;
            return result;
        }
    }

    public async Task<AppUpdateInfo> PrepareUpdateAsync(IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var info = LastResult ?? await CheckAsync(ct).ConfigureAwait(false);
        if (info.Error is not null)
            return info with { Error = "无法获取更新信息：" + info.Error };

        if (string.IsNullOrEmpty(info.DownloadUrl))
            return info with { Error = "发布中未找到匹配的更新产物" };

        try
        {
            var workDir = Path.Combine(Path.GetTempPath(), "MomomiUpdate");
            Directory.CreateDirectory(workDir);

            var fileName = Path.GetFileName(info.DownloadUrl);
            var downloaded = Path.Combine(workDir, fileName);
            await DownloadFileAsync(info.DownloadUrl, downloaded, progress, ct).ConfigureAwait(false);

            var setupExe = IsInstalled ? downloaded : null;
            string? extractDir = null;
            if (setupExe is null)
            {
                extractDir = Path.Combine(workDir, "extract");
                if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true);
                ZipFile.ExtractToDirectory(downloaded, extractDir);
                try { File.Delete(downloaded); } catch { }
            }

            var script = CreateUpdaterScript(setupExe, extractDir);
            _pendingScript = script;

            return info with { Error = null };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return info with { Error = "准备更新失败：" + ex.Message };
        }
    }

    public string? ConsumePendingScript()
    {
        var script = _pendingScript;
        _pendingScript = null;
        return script;
    }

    /// <summary>
    /// 生成静默更新批处理：等待 Momomi 进程退出 → 安装器静默安装（安装版）
    /// 或 robocopy 覆盖（便携版）→ 以 --startcore 重新启动（更新后自动拉起内核）。
    /// </summary>
    private string CreateUpdaterScript(string? setupExe, string? extractDir)
    {
        var appDir = AppContext.BaseDirectory.TrimEnd('\\');
        var script = Path.Combine(Path.GetTempPath(), "MomomiUpdate", $"updater-{DateTime.Now:HHmmss}.cmd");
        Directory.CreateDirectory(Path.GetDirectoryName(script)!);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine("setlocal");
        sb.AppendLine(":WAIT");
        sb.AppendLine("tasklist /FI \"IMAGENAME eq Momomi.exe\" 2>nul | find /I \"Momomi.exe\" >nul");
        sb.AppendLine("if not errorlevel 1 ( timeout /t 1 /nobreak >nul & goto WAIT )");

        if (setupExe is not null)
        {
            sb.AppendLine($"\"{setupExe}\" /SILENT /SP- /NORESTART");
        }
        else if (extractDir is not null)
        {
            // /XD data：便携版覆盖更新时排除数据目录，避免把用户数据/内核当程序文件覆盖或删除。
            sb.AppendLine($"robocopy \"{extractDir}\" \"{appDir}\" /E /XD data /NFL /NDL /NJH /NJS /R:1 /W:1 >nul");
            sb.AppendLine($"rmdir /s /q \"{extractDir}\"");
        }

        sb.AppendLine($"start \"\" \"{appDir}\\Momomi.exe\" --startcore");
        sb.AppendLine($"del \"%~f0\"");
        sb.AppendLine("endlocal");

        File.WriteAllText(script, sb.ToString());
        return script;
    }

    /// <summary>按当前形态与架构挑选下载产物：安装版取 setup.exe，便携版取对应 zip。</summary>
    private static string? SelectAssetUrl(string version, IReadOnlyList<ReleaseAsset> assets)
    {
        var installed = File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe"));
        var arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
            _ => "x64",
        };

        var full = Directory.Exists(Path.Combine(AppContext.BaseDirectory, "bundled"));

        if (installed)
        {
            var setupName = $"Momomi-{version}-{arch}{(full ? "-full" : "")}-setup.exe";
            var asset = assets.FirstOrDefault(a => string.Equals(a.Name, setupName, StringComparison.OrdinalIgnoreCase))
                        ?? assets.FirstOrDefault(a => a.Name.Contains($"{arch}", StringComparison.OrdinalIgnoreCase)
                                                     && a.Name.EndsWith("-setup.exe", StringComparison.OrdinalIgnoreCase));
            return asset?.DownloadUrl;
        }

        var suffix = full ? "full" : "portable";
        var zipName = $"Momomi-{version}-{arch}-{suffix}.zip";
        var zip = assets.FirstOrDefault(a => string.Equals(a.Name, zipName, StringComparison.OrdinalIgnoreCase))
                  ?? assets.FirstOrDefault(a => a.Name.Contains($"{arch}", StringComparison.OrdinalIgnoreCase)
                                               && a.Name.Contains($"{suffix}.zip", StringComparison.OrdinalIgnoreCase)
                                               && a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        return zip?.DownloadUrl;
    }

    private async Task<string> FetchLatestAsync(CancellationToken ct)
    {
        Exception? last = null;
        foreach (var proxy in await GetProxyCandidatesAsync(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            var url = proxy is null ? ReleasesApi : ApplyProxy(proxy, ReleasesApi);
            try
            {
                using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }
        throw last ?? new HttpRequestException("无法访问 GitHub Releases API");
    }

    private static (string? Tag, string? HtmlUrl, IReadOnlyList<ReleaseAsset> Assets) ParseLatest(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
        var html = root.TryGetProperty("html_url", out var u) ? u.GetString() : null;

        var assets = new List<ReleaseAsset>();
        if (root.TryGetProperty("assets", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in arr.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                var url = asset.TryGetProperty("browser_download_url", out var b) ? b.GetString() : null;
                var size = asset.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : 0;
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(url)) continue;
                assets.Add(new ReleaseAsset(name, url, size));
            }
        }
        return (tag, html, assets);
    }

    private async Task DownloadFileAsync(string url, string destination, IProgress<double>? progress, CancellationToken ct)
    {
        Exception? last = null;
        foreach (var proxy in await GetProxyCandidatesAsync(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            var target = proxy is null ? url : ApplyProxy(proxy, url);
            try
            {
                using var response = await _http.GetAsync(target, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                var total = response.Content.Headers.ContentLength ?? -1;
                await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var file = File.Create(destination);

                var buffer = new byte[81920];
                long received = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    received += read;
                    if (total > 0) progress?.Report((double)received / total);
                }

                if (total > 0 && received < total)
                {
                    last = new IOException($"下载不完整：{received}/{total}");
                    continue;
                }
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
                try { if (File.Exists(destination)) File.Delete(destination); } catch { }
            }
        }
        throw last ?? new HttpRequestException($"下载失败：{url}");
    }

    private static readonly string[] BuiltinProxies =
    {
        "https://gh-proxy.org",
        "https://ghproxy.net",
        "https://ghfast.top",
        "https://gh.llkk.cc",
    };

    private async Task<IReadOnlyList<string?>> GetProxyCandidatesAsync(CancellationToken ct)
    {
        if (_settings is null) return new string?[] { null };

        var value = (await _settings.GetAsync("core.githubProxy").ConfigureAwait(false))?.Trim();
        if (string.Equals(value, "direct", StringComparison.OrdinalIgnoreCase))
            return new string?[] { null };

        if (string.IsNullOrEmpty(value) || string.Equals(value, "auto", StringComparison.OrdinalIgnoreCase))
        {
            var list = BuiltinProxies.Select(p => (string?)p).ToList();
            list.Add(null);
            return list;
        }

        return new string?[] { value!.TrimEnd('/'), null };
    }

    private static string ApplyProxy(string proxy, string url)
    {
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return url;
        var host = url["https://".Length..];
        var isGithub = host.StartsWith("github.com/", StringComparison.OrdinalIgnoreCase)
            || host.StartsWith("api.github.com/", StringComparison.OrdinalIgnoreCase)
            || host.StartsWith("raw.githubusercontent.com/", StringComparison.OrdinalIgnoreCase)
            || host.StartsWith("objects.githubusercontent.com/", StringComparison.OrdinalIgnoreCase)
            || host.StartsWith("codeload.github.com/", StringComparison.OrdinalIgnoreCase);
        return isGithub ? $"{proxy}/{url}" : url;
    }
}