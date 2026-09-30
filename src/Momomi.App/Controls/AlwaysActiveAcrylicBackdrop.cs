using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Momomi.App.Controls;

/// <summary>
/// 始终活跃的亚克力背景：把 IsInputActive 固定为 true，
/// 使窗口失焦或被其他窗口遮挡时依然采样并模糊下方窗口内容，
/// 而不是像默认那样降级为壁纸/纯色。
/// </summary>
public sealed class AlwaysActiveAcrylicBackdrop : SystemBackdrop, IDisposable
{
    private readonly Dictionary<ICompositionSupportsSystemBackdrop, Target> _targets = new();
    private bool _disposed;

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);

        var config = BuildConfig(xamlRoot);
        var controller = new DesktopAcrylicController { Kind = DesktopAcrylicKind.Base };
        controller.SetSystemBackdropConfiguration(config);
        controller.AddSystemBackdropTarget(connectedTarget);

        _targets[connectedTarget] = new Target(controller, config);
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        base.OnTargetDisconnected(disconnectedTarget);

        if (_targets.Remove(disconnectedTarget, out var target))
        {
            try
            {
                target.Controller.RemoveSystemBackdropTarget(disconnectedTarget);
            }
            catch
            {
            }
            target.Controller.Dispose();
        }
    }

    /// <summary>
    /// 基类在主题/输入状态变化时回调。必须重写：默认实现会用当前目标重建默认配置，
    /// 目标在材质切换后已失效时会抛 ArgumentException（参数错误 target）。
    /// 这里改为按最新主题更新既有控制器的配置。
    /// </summary>
    protected override void OnDefaultSystemBackdropConfigurationChanged(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        if (_targets.TryGetValue(target, out var entry))
        {
            entry.Config.Theme = ResolveTheme(xamlRoot);
        }
    }

    /// <summary>释放所有仍连接的控制器。WinUI 替换 SystemBackdrop 时不会自动断开旧目标，
    /// 必须显式调用，否则每次切换背景材质都会泄漏一个 DesktopAcrylicController。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var kv in _targets)
        {
            try
            {
                kv.Value.Controller.RemoveSystemBackdropTarget(kv.Key);
            }
            catch
            {
            }
            kv.Value.Controller.Dispose();
        }
        _targets.Clear();
    }

    private static SystemBackdropConfiguration BuildConfig(XamlRoot xamlRoot) => new()
    {
        IsInputActive = true,
        Theme = ResolveTheme(xamlRoot),
    };

    private static SystemBackdropTheme ResolveTheme(XamlRoot xamlRoot) =>
        xamlRoot.Content is FrameworkElement fe
            ? fe.ActualTheme switch
            {
                ElementTheme.Dark => SystemBackdropTheme.Dark,
                ElementTheme.Light => SystemBackdropTheme.Light,
                _ => SystemBackdropTheme.Default,
            }
            : SystemBackdropTheme.Default;

    private sealed class Target
    {
        public Target(DesktopAcrylicController controller, SystemBackdropConfiguration config)
        {
            Controller = controller;
            Config = config;
        }

        public DesktopAcrylicController Controller { get; }

        public SystemBackdropConfiguration Config { get; }
    }
}
