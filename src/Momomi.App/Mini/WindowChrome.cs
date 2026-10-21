using System.Runtime.InteropServices;
using System.Text;

namespace Momomi.App.Mini;

internal static class WindowChrome
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_APPWINDOW = 0x00040000;
    private const int WS_EX_LAYERED = 0x00080000;

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOSENDCHANGING = 0x0400;
    private const uint LWA_ALPHA = 0x00000002;

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    private const uint WM_NCCALCSIZE = 0x0083;

    /// <summary>
    /// 每个窗口自己的子类化委托。
    ///
    /// 原来是 static 字段：迷你窗口是「收起后关闭、下次重建」的，
    /// 新窗口 SetWindowSubclass 时会覆盖掉旧委托，而旧窗口若尚未真正销毁，
    /// 其子类化入口仍指向那个已可回收的委托——原生回调打到已释放的 thunk 就是进程崩溃。
    /// 改成按窗口句柄各存一份，窗口销毁后再移除。
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<IntPtr, SUBCLASSPROC> SubclassProcs = new();

    public static void RemoveNonClientFrame(IntPtr hwnd)
    {
        var proc = new SUBCLASSPROC(SubclassProc);
        SubclassProcs[hwnd] = proc;
        if (!SetWindowSubclass(hwnd, proc, 1, IntPtr.Zero))
        {
            SubclassProcs.TryRemove(hwnd, out _);
        }
    }

    /// <summary>窗口关闭前解挂子类化，释放对应委托。</summary>
    public static void RemoveSubclass(IntPtr hwnd)
    {
        if (SubclassProcs.TryRemove(hwnd, out var proc))
        {
            try { _ = RemoveWindowSubclass(hwnd, proc, 1); } catch { }
        }
    }

    public static void SetToolWindow(IntPtr hwnd)
    {
        int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        ex |= WS_EX_TOOLWINDOW;
        ex &= ~WS_EX_APPWINDOW;
        SetWindowLong(hwnd, GWL_EXSTYLE, ex);
    }

    public static void SetTopmost(IntPtr hwnd)
    {
        _ = SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOSENDCHANGING);
    }

    /// <summary>
    /// 把窗口插到任务栏在 topmost 组内的正下方，让任务栏遮住它，
    /// 从而产生"从任务栏后钻出"的观感。找不到任务栏时降级为纯 topmost。
    /// 必须在 BringToForeground 之后调用，否则激活会把窗口重新提到任务栏上方。
    /// </summary>
    public static void PlaceBelowTaskbar(IntPtr hwnd)
    {
        IntPtr taskbar = FindTaskbarForWindow(hwnd);
        if (taskbar == IntPtr.Zero)
        {
            SetTopmost(hwnd);
            return;
        }

        // 必须先在 topmost 组内，插入才会生效。
        SetTopmost(hwnd);
        _ = SetWindowPos(hwnd, taskbar, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOSENDCHANGING);
    }

    /// <summary>
    /// 找到与窗口同一显示器的任务栏：主屏为 Shell_TrayWnd，副屏为 Shell_SecondaryTrayWnd。
    /// </summary>
    private static IntPtr FindTaskbarForWindow(IntPtr hwnd)
    {
        IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);

        IntPtr primary = FindWindowW("Shell_TrayWnd", null);
        if (primary != IntPtr.Zero && MonitorFromWindow(primary, MONITOR_DEFAULTTONEAREST) == monitor)
            return primary;

        IntPtr found = IntPtr.Zero;
        EnumWindows((candidate, _) =>
        {
            var sb = new StringBuilder(64);
            GetClassNameW(candidate, sb, sb.Capacity);
            if (sb.ToString() != "Shell_SecondaryTrayWnd") return true;
            if (MonitorFromWindow(candidate, MONITOR_DEFAULTTONEAREST) != monitor) return true;
            found = candidate;
            return false;
        }, IntPtr.Zero);

        return found != IntPtr.Zero ? found : primary;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    public static void SetWindowAlpha(IntPtr hwnd, int alpha)
    {
        int clamped = Math.Clamp(alpha, 0, 255);
        int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        if ((ex & WS_EX_LAYERED) == 0)
            SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_LAYERED);
        _ = SetLayeredWindowAttributes(hwnd, 0, (byte)clamped, LWA_ALPHA);
    }

    public static int GetWindowAlpha(IntPtr hwnd)
    {
        int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        if ((ex & WS_EX_LAYERED) == 0) return 255;
        if (!GetLayeredWindowAttributes(hwnd, out _, out byte alpha, out _)) return 255;
        return alpha;
    }

    private static IntPtr SubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint id, IntPtr data)
    {
        if (msg == WM_NCCALCSIZE && wParam != IntPtr.Zero)
            return IntPtr.Zero;
        return DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    public static void SetRoundCorner(IntPtr hwnd)
    {
        int pref = DWMWCP_ROUND;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
    }

    public static void BringToForeground(IntPtr hwnd)
    {
        _ = SetForegroundWindow(hwnd);
        if (GetForegroundWindow() == hwnd) return;

        uint targetThread = GetWindowThreadProcessId(hwnd, IntPtr.Zero);
        uint foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero);
        if (targetThread == foregroundThread)
        {
            _ = SetForegroundWindow(hwnd);
            return;
        }

        _ = AttachThreadInput(targetThread, foregroundThread, true);
        _ = SetForegroundWindow(hwnd);
        _ = AttachThreadInput(targetThread, foregroundThread, false);
    }

    private delegate IntPtr SUBCLASSPROC(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint uIdSubclass, IntPtr dwRefData);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetLayeredWindowAttributes(IntPtr hWnd, out uint crKey, out byte bAlpha, out uint dwFlags);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SUBCLASSPROC pfnSubclass, uint uIdSubclass, IntPtr dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool RemoveWindowSubclass(IntPtr hWnd, SUBCLASSPROC pfnSubclass, uint uIdSubclass);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr pid);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowW(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    private const uint MONITOR_DEFAULTTONEAREST = 2;
}
