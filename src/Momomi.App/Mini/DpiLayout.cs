using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace Momomi.App.Mini;

internal static class DpiLayout
{
    private const int BaseDpi = 96;
    private const int MdtEffectiveDpi = 0;
    private const uint MonitorDefaultToNearest = 2;

    /// <summary>
    /// 计算迷你面板在目标屏幕右下角的位置（物理像素）。
    ///
    /// 目标屏幕必须显式传入，不能从窗口自身推导：面板隐藏时只是挪到原屏工作区下方，
    /// 窗口始终归属于原屏，于是换到另一块屏呼出时它还是弹回原屏——这就是
    /// "不跟随图标所在屏幕"。传 IntPtr.Zero 时才退回窗口当前所在显示器。
    /// </summary>
    public static RectInt32 ComputeBottomRight(
        AppWindow window, int widthDip, int heightDip,
        int marginRightDip, int marginBottomDip, IntPtr monitor)
    {
        if (monitor == IntPtr.Zero)
            monitor = MonitorForWindow(window);

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
            return new RectInt32(0, 0, widthDip, heightDip);

        // 用工作区（已扣掉任务栏），不是整块屏幕。
        var work = info.rcWork;
        double scale = GetScale(monitor);

        int workWidth = work.Right - work.Left;
        int workHeight = work.Bottom - work.Top;

        int w = (int)Math.Ceiling(widthDip * scale);
        int h = (int)Math.Ceiling(heightDip * scale);
        int mr = (int)Math.Ceiling(marginRightDip * scale);
        int mb = (int)Math.Ceiling(marginBottomDip * scale);

        int maxW = Math.Max(0, workWidth - mr);
        int maxH = Math.Max(0, workHeight - mb);
        w = Math.Min(w, maxW);
        h = Math.Min(h, maxH);

        int x = work.Left + workWidth - w - mr;
        int y = work.Top + workHeight - h - mb;
        return new RectInt32(x, y, w, h);
    }

    public static RectInt32 GetCurrentRect(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out RECT r)) return default;
        return new RectInt32(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }

    /// <summary>
    /// 光标当前所在显示器。托盘图标被点击的瞬间光标就在图标上，因此这就是"图标所在的屏幕"。
    /// </summary>
    public static IntPtr MonitorForCursor()
    {
        try
        {
            return GetCursorPos(out var p) ? MonitorFromPoint(p, MonitorDefaultToNearest) : IntPtr.Zero;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>
    /// 主任务栏所在显示器。通知区域（托盘图标）只存在于主任务栏上，
    /// 所以通过热键呼出、或开机只显示迷你面板时，用它作为"图标所在屏幕"。
    /// </summary>
    public static IntPtr MonitorForTrayTaskbar()
    {
        try
        {
            var taskbar = FindWindowW("Shell_TrayWnd", null);
            if (taskbar != IntPtr.Zero)
            {
                var monitor = MonitorFromWindow(taskbar, MonitorDefaultToNearest);
                if (monitor != IntPtr.Zero) return monitor;
            }
        }
        catch
        {
        }
        return MonitorForCursor();
    }

    /// <summary>
    /// 前台窗口所在显示器。用于热键呼出：此时既没有托盘图标被点击，光标也可能停在别处，
    /// 以用户正在操作的窗口为准最符合直觉。
    /// </summary>
    public static IntPtr MonitorForForegroundWindow()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd != IntPtr.Zero)
            {
                var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
                if (monitor != IntPtr.Zero) return monitor;
            }
        }
        catch
        {
        }
        return MonitorForCursor();
    }

    /// <summary>窗口当前所在显示器。</summary>
    public static IntPtr MonitorForWindow(AppWindow window)
    {
        try
        {
            var hwnd = Win32Interop.GetWindowFromWindowId(window.Id);
            return hwnd == IntPtr.Zero ? IntPtr.Zero : MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>目标显示器的工作区（已扣掉任务栏）。取不到返回 false。</summary>
    public static bool TryGetWorkArea(IntPtr monitor, out RectInt32 work)
    {
        work = default;
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return false;

        var r = info.rcWork;
        work = new RectInt32(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
        return true;
    }

    /// <summary>矩形中心是否落在该显示器的工作区内（用于判断窗口此刻是否已在目标屏幕上）。</summary>
    public static bool IsWithinWorkArea(IntPtr monitor, RectInt32 rect)
    {
        if (!TryGetWorkArea(monitor, out var work)) return true;

        var cx = rect.X + rect.Width / 2;
        var cy = rect.Y + rect.Height / 2;
        return cx >= work.X && cx < work.X + work.Width
            && cy >= work.Y && cy < work.Y + work.Height;
    }

    private static double GetScale(IntPtr monitor)
    {
        if (monitor == IntPtr.Zero) return 1.0;
        int hr = GetDpiForMonitor(monitor, MdtEffectiveDpi, out uint dpiX, out _);
        return hr >= 0 && dpiX > 0 ? dpiX / (double)BaseDpi : 1.0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowW(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);
}
