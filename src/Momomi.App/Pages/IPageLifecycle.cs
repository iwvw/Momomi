namespace Momomi.App.Pages;

/// <summary>
/// 页面的「离开界面」回调。
///
/// 存在的理由：MainWindow 最小化到托盘时会执行 <c>NavFrame.Content = null</c> 来释放
/// 整棵可视化树，但直接给 Frame.Content 赋值<b>不会</b>触发导航流程，
/// 因此页面里的 <c>OnNavigatedFrom</c> 不会执行、ViewModel 也不会 Detach。
/// 于是每「呼出→收起」一次，就多一份仍然挂在 Core.TrafficUpdated /
/// StateChanged / AppSignals.ProxiesChanged 上的 ViewModel：
/// 它们继续每秒收流量、每次切节点都重新拉一遍 /proxies 并解析 runtime.yaml，
/// 内存和 CPU 随使用时长单调上涨。
///
/// 各个页面实现本接口，在方法里做与 OnNavigatedFrom 相同的清理。
/// </summary>
internal interface IPageLifecycle
{
    /// <summary>页面内容即将被释放：退订所有事件、停下定时器。</summary>
    void DetachFromHost();
}
