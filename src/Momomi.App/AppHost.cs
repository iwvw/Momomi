using Momomi.Core.Services;

namespace Momomi.App;

public static class AppHost
{
    private static MomomiHost? _host;

    public static MomomiHost Host => _host ?? throw new InvalidOperationException("AppHost 尚未初始化");

    public static bool IsReady => _host is not null;

    public static async Task<MomomiHost> InitializeAsync()
    {
        if (_host is not null) return _host;
        var host = new MomomiHost();
        await host.InitializeAsync().ConfigureAwait(false);
        // Core 层的配置重载事件桥接到 App 层全局广播，供各视图订阅。
        host.ProxiesReloaded += (_, _) => ViewModels.AppSignals.RaiseProxiesChanged();
        _host = host;
        return host;
    }

    public static void Shutdown()
    {
        _host?.Dispose();
        _host = null;
    }
}
