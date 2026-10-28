using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Windows.Graphics;

namespace Momomi.App.Mini;

internal enum SlideDirection
{
    BottomUp,
    RightToLeft,
}

internal enum SlideEasing
{
    /// <summary>展开：起步快、末段收缓（Fluent 进场曲线的量级）。</summary>
    EaseOut,

    /// <summary>收起：起步慢、加速离开。</summary>
    EaseIn,
}

internal static class SlideMath
{
    public const int Overshoot = 24;

    public static RectInt32 Hidden(RectInt32 target, SlideDirection direction)
    {
        return direction switch
        {
            SlideDirection.BottomUp => new RectInt32(
                target.X, target.Y + target.Height + Overshoot, target.Width, target.Height),
            SlideDirection.RightToLeft => new RectInt32(
                target.X + target.Width + Overshoot, target.Y, target.Width, target.Height),
            _ => target,
        };
    }

    public static double Ease(double t, SlideEasing easing) => easing switch
    {
        SlideEasing.EaseIn => EaseInQuad(t),
        _ => EaseOutCubic(t),
    };

    /// <summary>
    /// 展开用的缓动。
    ///
    /// 峰值速度是平均速度的 3 倍。
    ///
    /// 不要换成 5 次方：它在短时长下的观感是"瞬间弹上去、再慢慢爬完剩下的"，
    /// 起步 20% 的时间里已经走完 67% 的行程，眼睛只看到一次跳变。
    /// 3 次方起步同样柔和，但前段不再那么极端，整段位移能被看清。
    /// </summary>
    public static double EaseOutCubic(double t)
    {
        var inv = 1 - t;
        return 1 - inv * inv * inv;
    }

    /// <summary>收起用的缓动：起步慢、加速离开，形成"出去时先犹豫一下"的观感。</summary>
    public static double EaseInQuad(double t) => t * t;
}

/// <summary>
/// 窗口矩形动画。
///
/// 三条约束决定了这里的实现：
///
/// 1. <b>不能占用 UI 线程</b>。动画期间 UI 线程要排版、要响应点击，
///    逐帧 SetWindowPos 必须在独立线程上跑。
/// 2. <b>必须可打断</b>。用户随时会反向点击，新请求直接接管，
///    起点取窗口的<b>真实</b>位置，否则反向瞬间会跳一截。
/// 3. <b>不能刷满合成器</b>。高刷屏上 DwmFlush 每 4–5ms 就返回，
///    按它逐帧移动窗口等于每秒两百多次位移，Mica/亚克力的背景采样
///    跟着重算两百多次，帧率反而掉下来。所以固定按 120fps 出帧。
///
/// 位置一律由单调时钟插值算出，不靠"每帧加固定步长"：
/// 某一帧因为系统忙而迟到时，下一帧会自动补上，画面不会整体变慢。
/// </summary>
internal sealed class WindowSlider : IDisposable
{
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOOWNERZORDER = 0x0200;
    private const uint SWP_ASYNCWINDOWPOS = 0x4000;

    private const int MaxFrameRate = 120;
    private const double FrameIntervalMs = 1000.0 / MaxFrameRate;

    /// <summary>
    /// 打断后最短时长。
    ///
    /// 剩余行程很短时如果还按比例压到 80ms 以下，人眼会看到"跳"而不是"滑"——
    /// 短位移配短时长反而更像瞬移，因为它连减速段都来不及展示。
    /// 140ms 能保证至少走完一个可辨识的缓动。
    /// </summary>
    private const int MinDurationMs = 140;

    private readonly IntPtr _hwnd;
    private readonly DispatcherQueue _dispatcher;
    private readonly object _gate = new();
    private readonly AutoResetEvent _signal = new(false);
    private readonly Thread _worker;

    private RectInt32 _to;
    private int _fromAlpha;
    private int _toAlpha;
    private int _durationMs;
    private SlideEasing _easing;
    private Action? _onCompleted;
    private int _generation;
    private volatile bool _hasRequest;
    private volatile bool _disposed;

    public WindowSlider(IntPtr hwnd, DispatcherQueue dispatcher)
    {
        _hwnd = hwnd;
        _dispatcher = dispatcher;
        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
            Name = "Momomi.MiniSlide",
        };
        _worker.Start();
    }

    public bool IsRunning => _hasRequest;

    /// <summary>
    /// 从窗口当前位置动画到 <paramref name="to"/>。
    ///
    /// 起点不由调用方提供：调用方手里的矩形是它上次算的，
    /// 动画可能已经跑掉一半，拿它当起点会让画面往回跳。
    /// </summary>
    public void Animate(RectInt32 to, int fromAlpha, int toAlpha,
        int durationMs, SlideEasing easing, Action? onCompleted)
    {
        lock (_gate)
        {
            if (_disposed) return;

            _generation++;
            _to = to;
            _fromAlpha = fromAlpha;
            _toAlpha = toAlpha;

            // 0 表示"立刻落位"：系统关闭了动画效果时必须真的不做动画，
            // 而不是给一个最短时长。
            _durationMs = durationMs <= 0 ? 0 : Math.Max(MinDurationMs, durationMs);

            _easing = easing;
            _onCompleted = onCompleted;
            _hasRequest = true;
        }

        SetSignal();
    }

    /// <summary>
    /// 停止工作线程。
    ///
    /// 必须幂等：托盘退出（ForceClose）与拖拽收起的动画完成回调可能都会调一次，
    /// 重复 Set/Dispose 会抛 ObjectDisposedException。
    /// 另外 Join 超时时<b>不能</b>释放信号量——工作线程可能正卡在 WaitOne 上，
    /// 释放后它会在后台线程抛 ObjectDisposedException，那是未处理异常，直接把进程带走。
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        SetSignal();

        var joined = _worker.Join(500);
        if (!joined)
        {
            // 线程还活着：信号量留给它，交给进程退出时回收，绝不在这里 Dispose。
            return;
        }

        _signal.Dispose();
    }

    /// <summary>中断当前动画并清除完成回调（拖动手势接管时调用）。</summary>
    public void Cancel()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _generation++;
            _hasRequest = false;
            _onCompleted = null;
        }

        SetSignal();
    }

    private void SetSignal()
    {
        try
        {
            _signal.Set();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void WorkerLoop()
    {
        while (!_disposed)
        {
            if (!_hasRequest)
            {
                try
                {
                    _signal.WaitOne(250);
                }
                catch (ObjectDisposedException)
                {
                    // Dispose 与等待竞态时可能已被释放；直接退出即可。
                    return;
                }

                continue;
            }

            RectInt32 to;
            int fromAlpha, toAlpha, durationMs, generation;
            SlideEasing easing;
            Action? onCompleted;

            lock (_gate)
            {
                if (!_hasRequest || _disposed) continue;
                to = _to;
                fromAlpha = _fromAlpha;
                toAlpha = _toAlpha;
                durationMs = _durationMs;
                easing = _easing;
                generation = _generation;
                onCompleted = _onCompleted;
            }

            var finished = RunAnimation(generation, to, fromAlpha, toAlpha, durationMs, easing);

            if (!finished)
            {
                // 被新请求打断：清掉信号，回到循环顶部重新取参数。
                _signal.Reset();
                continue;
            }

            lock (_gate)
            {
                if (generation != _generation) continue;
                _hasRequest = false;
                _onCompleted = null;
            }

            if (onCompleted is not null)
                _ = _dispatcher.TryEnqueue(() => onCompleted());
        }
    }

    private bool RunAnimation(int generation, RectInt32 to,
        int fromAlpha, int toAlpha, int durationMs, SlideEasing easing)
    {
        var from = DpiLayout.GetCurrentRect(_hwnd);
        if (from.Width <= 0 || from.Height <= 0) from = to;

        double dx = to.X - from.X;
        double dy = to.Y - from.Y;
        double dw = to.Width - from.Width;
        double dh = to.Height - from.Height;

        var sizeFixed = dw == 0 && dh == 0;
        var alphaMoves = fromAlpha != toAlpha;

        if (Math.Abs(dx) < 1 && Math.Abs(dy) < 1 && Math.Abs(dw) < 1 && Math.Abs(dh) < 1 && !alphaMoves)
        {
            ApplyFrame(to, toAlpha, alphaMoves, async: false, sizeFixed);
            return true;
        }

        // durationMs == 0 表示"不做动画，直接落位"。
        if (durationMs <= 0)
        {
            ApplyFrame(to, toAlpha, alphaMoves, async: false, sizeFixed);
            return true;
        }

        // 打断后剩余行程可能只有满程的一小段。时长按行程比例缩短，
        // 否则反向时会"慢慢挪回去"，手感发黏。
        //
        // 基准必须取「主轴上的满程」：面板是竖向滑动的，宽度通常比高度大，
        // 用 max(宽,高) 会把基准算大，结果每次正常展开也被判成"只走了一半"，
        // 时长被压到一半以下，动画快得像瞬移。
        var vertical = Math.Abs(dy) >= Math.Abs(dx);
        double reference = vertical
            ? to.Height + SlideMath.Overshoot
            : to.Width + SlideMath.Overshoot;

        var distance = vertical ? Math.Abs(dy) : Math.Abs(dx);
        var ratio = reference > 0 ? Math.Min(1.0, distance / reference) : 1.0;
        var effective = Math.Clamp((int)Math.Round(durationMs * ratio), MinDurationMs, durationMs);

        _ = timeBeginPeriod(1);
        try
        {
            var clock = Stopwatch.StartNew();
            var frame = 0;

            while (true)
            {
                lock (_gate)
                {
                    if (generation != _generation || _disposed) return false;
                }

                double elapsed = clock.Elapsed.TotalMilliseconds;
                var progress = elapsed / effective;

                if (progress >= 1.0)
                {
                    // 末帧同步落位：异步队列里可能还有没应用的帧，
                    // 同步调用排在它们之后，保证最终位置精确。
                    ApplyFrame(to, toAlpha, alphaMoves, async: false, sizeFixed);
                    return true;
                }

                var eased = SlideMath.Ease(progress, easing);

                var rect = new RectInt32(
                    (int)Math.Round(from.X + dx * eased),
                    (int)Math.Round(from.Y + dy * eased),
                    (int)Math.Round(from.Width + dw * eased),
                    (int)Math.Round(from.Height + dh * eased));

                var alpha = (int)Math.Round(fromAlpha + (toAlpha - fromAlpha) * eased);

                ApplyFrame(rect, alpha, alphaMoves, async: true, sizeFixed);

                frame++;
                var nextDeadline = Math.Min(effective, frame * FrameIntervalMs);
                var wait = nextDeadline - clock.Elapsed.TotalMilliseconds;

                if (wait > 1.0)
                {
                    Thread.Sleep((int)Math.Round(wait));
                }
                else
                {
                    // 落后于帧计划时不要空转：空转会让后面的帧全部挤在一起，
                    // 动画看起来是"前半段卡、后半段瞬移"。
                    // 让出一小段时间，把节奏拉回来。
                    Thread.Sleep(1);
                }
            }
        }
        finally
        {
            _ = timeEndPeriod(1);
        }
    }

    /// <summary>
    /// 应用一帧。
    ///
    /// 中间帧一律带 <c>SWP_ASYNCWINDOWPOS</c>：同步的 SetWindowPos 会
    /// SendMessage 到窗口所属线程，UI 线程只要正在排版（展开首帧正是最忙的时候）
    /// 就会把这个线程一起堵住，表现是动画一顿一顿。
    ///
    /// 整段动画尺寸不变时（纯位移，即展开/收起）再带上 <c>SWP_NOSIZE</c>：
    /// 每次都声明同一个尺寸仍会让窗口走一遍 size 变更流程，
    /// 客户区重排、交换链跟着检查，纯属白做。
    ///
    /// 只在需要淡入淡出时才设置分层窗口 alpha：
    /// WS_EX_LAYERED 会让 Mica/亚克力背景失效。
    /// </summary>
    private void ApplyFrame(RectInt32 rect, int alpha, bool applyAlpha, bool async, bool sizeFixed)
    {
        var flags = SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER;
        if (async) flags |= SWP_ASYNCWINDOWPOS;
        if (sizeFixed) flags |= SWP_NOSIZE;

        _ = SetWindowPos(_hwnd, IntPtr.Zero, rect.X, rect.Y, rect.Width, rect.Height, flags);
        if (applyAlpha) WindowChrome.SetWindowAlpha(_hwnd, alpha);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint uPeriod);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint uPeriod);
}
