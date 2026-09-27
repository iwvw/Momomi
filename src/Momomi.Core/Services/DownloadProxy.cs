using System.Net;

namespace Momomi.Core.Services;

/// <summary>
/// 下载代理：内核运行时，把内核/geodata/订阅等下载请求经本机混合端口转发，
/// 借力自身代理访问 GitHub 等受限资源。
/// 优先级：手动设置的下载代理端口 &gt; 内核混合端口 &gt; 系统代理 &gt; 直连。
/// </summary>
public static class DownloadProxy
{
    private static volatile int _corePort;
    private static volatile int _manualPort;

    /// <summary>内核运行时设置其混合端口；0 表示内核未运行。</summary>
    public static void SetCorePort(int port) => _corePort = port is > 0 and < 65536 ? port : 0;

    /// <summary>用户手动指定的下载代理端口；0 表示未设置。</summary>
    public static void SetManualPort(int port) => _manualPort = port is > 0 and < 65536 ? port : 0;

    /// <summary>当前生效的下载代理端口；0 表示走系统代理或直连。</summary>
    public static int EffectivePort
    {
        get
        {
            var m = _manualPort;
            if (m > 0) return m;
            var c = _corePort;
            return c > 0 ? c : 0;
        }
    }

    public static IWebProxy Create() => new Provider();

    private sealed class Provider : IWebProxy
    {
        public ICredentials? Credentials { get; set; }

        public Uri? GetProxy(Uri destination)
        {
            // 本机回环地址永不代理，避免自代理死循环。
            if (destination.IsLoopback) return destination;

            var port = EffectivePort;
            if (port > 0) return new Uri($"http://127.0.0.1:{port}");

            // 未配置专用代理：沿用系统代理（若用户已开）。
            try
            {
                var sys = WebRequest.DefaultWebProxy;
                if (sys is not null)
                {
                    var p = sys.GetProxy(destination);
                    if (p is not null && p != destination) return p;
                }
            }
            catch
            {
            }

            return destination;
        }

        public bool IsBypassed(Uri host)
        {
            if (host.IsLoopback) return true;
            return EffectivePort <= 0;
        }
    }
}
