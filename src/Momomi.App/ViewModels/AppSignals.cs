namespace Momomi.App.ViewModels;

/// <summary>内核安装进度（首装自动下载时用）。Text 为状态文案，Percent 为 0-100（null 表示不确定进度）。</summary>
public sealed record KernelInstallProgress(string Text, double? Percent);

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

    /// <summary>内核安装进度变化（首装自动下载内核/地理数据时）。</summary>
    public static event EventHandler<KernelInstallProgress>? KernelInstallProgressChanged;

    /// <summary>当前内核安装状态；未在安装时为 null。</summary>
    public static KernelInstallProgress? KernelInstall { get; private set; }

    /// <summary>应用有新版本可用（null 表示没有更新）。</summary>
    public static event EventHandler<AppUpdateNotice?>? AppUpdateAvailableChanged;

    /// <summary>最近一次检查到的应用更新；无更新或未检查时为 null。</summary>
    public static AppUpdateNotice? AppUpdateAvailable { get; private set; }

    public static void RaiseProxiesChanged() => ProxiesChanged?.Invoke(null, EventArgs.Empty);

    public static void RaiseSwitchesChanged() => SwitchesChanged?.Invoke(null, EventArgs.Empty);

    public static void RaiseMainWindowVisibility(bool visible) => MainWindowVisibilityChanged?.Invoke(null, visible);

    public static void RaiseKernelInstall(KernelInstallProgress? progress)
    {
        KernelInstall = progress;
        KernelInstallProgressChanged?.Invoke(null, progress ?? new KernelInstallProgress("", null));
    }

    /// <summary>广播「有新版本」状态，供顶栏胶囊标签显示/隐藏。</summary>
    public static void RaiseAppUpdateNotice(AppUpdateNotice? notice)
    {
        AppUpdateAvailable = notice;
        AppUpdateAvailableChanged?.Invoke(null, notice);
    }
}

/// <summary>可更新版本的通知信息（顶栏与设置页展示用）。</summary>
public sealed record AppUpdateNotice(
    string Version,
    string? Notes = null,
    string? ReleaseUrl = null);

