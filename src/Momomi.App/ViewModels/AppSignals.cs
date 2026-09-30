namespace Momomi.App.ViewModels;

/// <summary>
/// 应用内跨视图/跨窗口的全局状态广播。主面板与迷你面板各自持有独立的 ViewModel，
/// 任一处的变更（模式、系统代理、TUN、代理组/节点）都通过这里广播，另一处订阅后刷新。
/// </summary>
public static class AppSignals
{
    /// <summary>代理组/节点数据需要重新拉取（订阅刷新、配置重载、节点切换等）。</summary>
    public static event EventHandler? ProxiesChanged;

    /// <summary>模式、系统代理、TUN 等运行开关发生变化。</summary>
    public static event EventHandler? SwitchesChanged;

    /// <summary>主窗口显示/隐藏（隐藏到托盘或仅用迷你面板时为 false）。页面据此暂停后台轮询。</summary>
    public static event EventHandler<bool>? MainWindowVisibilityChanged;

    public static void RaiseProxiesChanged() => ProxiesChanged?.Invoke(null, EventArgs.Empty);

    public static void RaiseSwitchesChanged() => SwitchesChanged?.Invoke(null, EventArgs.Empty);

    public static void RaiseMainWindowVisibility(bool visible) => MainWindowVisibilityChanged?.Invoke(null, visible);
}
