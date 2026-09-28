using System.Net;
using System.Text;

namespace Momomi.Core.Services;

/// <summary>
/// 本地 PAC 服务器：监听随机回环端口，返回指向本机混合端口的 PAC 脚本，
/// 供系统代理的 PAC 模式使用。比固定 127.0.0.1 更稳（端口随机、无占用冲突）。
/// </summary>
public sealed class PacServer : IDisposable
{
    private HttpListener? _listener;
    private string? _pacText;
    private readonly object _gate = new();
    private volatile bool _running;

    /// <summary>当前监听端口；未启动为 0。</summary>
    public int Port { get; private set; }

    /// <summary>PAC 脚本地址；未启动为 null。</summary>
    public string? PacUrl { get; private set; }

    /// <summary>启动监听。mixedPort 为内核混合端口（HTTP/SOCKS5 均在此端口）。</summary>
    public bool Start(int mixedPort)
    {
        Stop();
        try
        {
            _pacText = BuildPacScript(mixedPort);
            // 尝试多个随机回环端口，避免偶发占用。
            for (var attempt = 0; attempt < 10; attempt++)
            {
                var port = Random.Shared.Next(20000, 60000);
                var prefix = $"http://127.0.0.1:{port}/";
                try
                {
                    var listener = new HttpListener();
                    listener.Prefixes.Add(prefix);
                    listener.Start();
                    _listener = listener;
                    Port = port;
                    PacUrl = prefix + "proxy.pac";
                    _running = true;
                    _ = Task.Run(ListenLoop);
                    return true;
                }
                catch (HttpListenerException)
                {
                    _listener?.Close();
                    _listener = null;
                }
            }
            return false;
        }
        catch
        {
            Stop();
            return false;
        }
    }

    private async Task ListenLoop()
    {
        while (_running && _listener?.IsListening == true)
        {
            HttpListenerContext? ctx = null;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch
            {
                break;
            }
            try
            {
                var pac = _pacText;
                var bytes = Encoding.UTF8.GetBytes(pac ?? "");
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "application/x-ns-proxy-autoconfig";
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
            catch
            {
                ctx?.Response.Abort();
            }
        }
    }

    private static string BuildPacScript(int mixedPort)
        => $$"""
            function FindProxyForURL(url, host) {
                if (isPlainHostName(host) || dnsDomainIs(host, "localhost")) return "DIRECT";
                return "PROXY 127.0.0.1:{{mixedPort}}; SOCKS5 127.0.0.1:{{mixedPort}}; DIRECT";
            }
            """;

    public void Stop()
    {
        _running = false;
        lock (_gate)
        {
            _listener?.Stop();
            _listener?.Close();
            _listener = null;
        }
        Port = 0;
        PacUrl = null;
        _pacText = null;
    }

    public void Dispose() => Stop();
}
