using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace Momomi.App.Mini;

internal static class DpiLayout
{
    private const int BaseDpi = 96;
    private const int MdtEffectiveDpi = 0;

    public static RectInt32 ComputeBottomRight(AppWindow window, int widthDip, int heightDip, int marginRightDip, int marginBottomDip)
    {
        var display = DisplayArea.GetFromWindowId(window.Id, DisplayAreaFallback.Nearest);
        if (display is null) return new RectInt32(0, 0, widthDip, heightDip);

        double scale = GetScale(display);
        var work = display.WorkArea;

        int w = (int)Math.Ceiling(widthDip * scale);
        int h = (int)Math.Ceiling(heightDip * scale);
        int mr = (int)Math.Ceiling(marginRightDip * scale);
        int mb = (int)Math.Ceiling(marginBottomDip * scale);

        int maxW = Math.Max(0, work.Width - mr);
        int maxH = Math.Max(0, work.Height - mb);
        w = Math.Min(w, maxW);
        h = Math.Min(h, maxH);

        int x = work.X + work.Width - w - mr;
        int y = work.Y + work.Height - h - mb;
        return new RectInt32(x, y, w, h);
    }

    public static RectInt32 GetCurrentRect(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out RECT r)) return default;
        return new RectInt32(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    private static double GetScale(DisplayArea display)
    {
        IntPtr hMonitor = Win32Interop.GetMonitorFromDisplayId(display.DisplayId);
        if (hMonitor == IntPtr.Zero) return 1.0;
        int hr = GetDpiForMonitor(hMonitor, MdtEffectiveDpi, out uint dpiX, out _);
        return hr >= 0 && dpiX > 0 ? dpiX / (double)BaseDpi : 1.0;
    }

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);
}
