using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using WinRT;

namespace Momomi.App.Controls;

/// <summary>
/// 始终活跃的亚克力背景：把 IsInputActive 固定为 true，
/// 使窗口失焦或被其他窗口遮挡时依然采样并模糊下方窗口内容，
/// 而不是像默认那样降级为壁纸/纯色。
/// </summary>
public sealed class AlwaysActiveAcrylicBackdrop : SystemBackdrop
{
    private readonly Dictionary<ICompositionSupportsSystemBackdrop, Target> _targets = new();

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);

        var config = new SystemBackdropConfiguration
        {
            IsInputActive = true,
            Theme = ResolveTheme(xamlRoot),
        };

        var controller = new DesktopAcrylicController { Kind = DesktopAcrylicKind.Base };
        controller.SetSystemBackdropConfiguration(config);
        controller.AddSystemBackdropTarget(connectedTarget);

        var target = new Target(controller, config, xamlRoot);
        _targets[connectedTarget] = target;

        if (xamlRoot.Content is FrameworkElement fe)
        {
            fe.ActualThemeChanged += target.OnThemeChanged;
        }
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        base.OnTargetDisconnected(disconnectedTarget);

        if (_targets.Remove(disconnectedTarget, out var target))
        {
            if (target.XamlRoot.Content is FrameworkElement fe)
            {
                fe.ActualThemeChanged -= target.OnThemeChanged;
            }

            target.Controller.RemoveSystemBackdropTarget(disconnectedTarget);
            target.Controller.Dispose();
        }
    }

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
        public Target(DesktopAcrylicController controller, SystemBackdropConfiguration config, XamlRoot xamlRoot)
        {
            Controller = controller;
            Config = config;
            XamlRoot = xamlRoot;
        }

        public DesktopAcrylicController Controller { get; }

        public SystemBackdropConfiguration Config { get; }

        public XamlRoot XamlRoot { get; }

        public void OnThemeChanged(FrameworkElement sender, object args) =>
            Config.Theme = ResolveTheme(XamlRoot);
    }
}