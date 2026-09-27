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

internal static class SlideMath
{
    private const int Overshoot = 24;

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

    public static double EaseOutCubic(double t) => 1 - Math.Pow(1 - t, 3);
    public static double EaseInCubic(double t) => t * t * t;
}

internal sealed class WindowSlider : IDisposable
{
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    private readonly IntPtr _hwnd;
    private readonly DispatcherQueue _dispatcher;
    private readonly object _gate = new();
    private readonly AutoResetEvent _signal = new(false);
    private readonly Thread _worker;

    private RectInt32 _from;
    private RectInt32 _to;
    private int _fromAlpha;
    private int _toAlpha;
    private int _durationMs;
    private bool _easeOut;
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

    public void Animate(RectInt32 from, RectInt32 to, int fromAlpha, int toAlpha,
        int durationMs, bool easeOut, Action? onCompleted)
    {
        lock (_gate)
        {
            _generation++;
            _from = from;
            _to = to;
            _fromAlpha = fromAlpha;
            _toAlpha = toAlpha;
            _durationMs = durationMs;
            _easeOut = easeOut;
            _onCompleted = onCompleted;
            _hasRequest = true;
        }
        _signal.Set();
    }

    public void Dispose()
    {
        _disposed = true;
        _signal.Set();
        _worker.Join(500);
        _signal.Dispose();
    }

    private void WorkerLoop()
    {
        _ = timeBeginPeriod(1);
        try
        {
            while (!_disposed)
            {
                if (!_hasRequest)
                {
                    _signal.WaitOne(250);
                    continue;
                }

                RectInt32 from, to;
                int fromAlpha, toAlpha, durationMs, generation;
                bool easeOut;
                Action? onCompleted;
                lock (_gate)
                {
                    if (!_hasRequest || _disposed) continue;
                    from = _from;
                    to = _to;
                    fromAlpha = _fromAlpha;
                    toAlpha = _toAlpha;
                    durationMs = _durationMs;
                    easeOut = _easeOut;
                    generation = _generation;
                    onCompleted = _onCompleted;
                }

                bool finished = RunAnimation(generation, from, to, fromAlpha, toAlpha, durationMs, easeOut);

                if (!finished)
                {
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
                    _dispatcher.TryEnqueue(() => onCompleted());
            }
        }
        finally
        {
            _ = timeEndPeriod(1);
        }
    }

    private bool RunAnimation(int generation, RectInt32 from, RectInt32 to,
        int fromAlpha, int toAlpha, int durationMs, bool easeOut)
    {
        bool positionMoves = from.X != to.X || from.Y != to.Y;
        bool alphaMoves = fromAlpha != toAlpha;

        ApplyFrame(from, fromAlpha, alphaMoves);

        if (durationMs <= 0 || (!positionMoves && !alphaMoves))
        {
            ApplyFrame(to, toAlpha, alphaMoves);
            return true;
        }

        var sw = Stopwatch.StartNew();
        while (true)
        {
            lock (_gate)
            {
                if (generation != _generation || _disposed) return false;
            }

            double progress = Math.Min(1.0, sw.Elapsed.TotalMilliseconds / durationMs);
            double eased = easeOut ? SlideMath.EaseOutCubic(progress) : SlideMath.EaseInCubic(progress);

            int x = (int)Math.Round(from.X + (to.X - from.X) * eased);
            int y = (int)Math.Round(from.Y + (to.Y - from.Y) * eased);
            int alpha = (int)Math.Round(fromAlpha + (toAlpha - fromAlpha) * eased);
            ApplyFrame(new RectInt32(x, y, to.Width, to.Height), alpha, alphaMoves);

            if (progress >= 1.0) break;
            if (DwmFlush() != 0) Thread.Sleep(2);
        }

        ApplyFrame(to, toAlpha, alphaMoves);
        return true;
    }

    // 仅当需要淡入淡出时才设置分层窗口 alpha：WS_EX_LAYERED 会让 Mica/亚克力背景失效。
    private void ApplyFrame(RectInt32 rect, int alpha, bool applyAlpha)
    {
        _ = SetWindowPos(_hwnd, IntPtr.Zero, rect.X, rect.Y, rect.Width, rect.Height,
            SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        if (applyAlpha) WindowChrome.SetWindowAlpha(_hwnd, alpha);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint uPeriod);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint uPeriod);
}
