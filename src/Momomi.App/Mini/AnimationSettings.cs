using System.Runtime.InteropServices;

namespace Momomi.App.Mini;

/// <summary>
/// 系统动画相关设置的读取。
///
/// Windows 允许用户在「辅助功能 → 视觉效果」里关闭动画，
/// 也允许改动画播放速度。自绘动画（本程序的窗口位移）不受系统自动接管，
/// 必须自己读这两个设置，否则关掉动画的用户仍会看到滑动，
/// 而开了「动画速度 = 慢」的用户会觉得程序反应很迟钝。
/// </summary>
internal static class AnimationSettings
{
    private const uint SPI_GETCLIENTAREAANIMATION = 0x1042;
    private const uint SPI_GETANIMATION = 0x0048;

    [StructLayout(LayoutKind.Sequential)]
    private struct ANIMATIONINFO
    {
        public uint cbSize;
        public int iMinAnimate;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfoW(uint action, uint param, out bool value, uint winIni);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfoW(uint action, uint param, ref ANIMATIONINFO value, uint winIni);

    /// <summary>
    /// 是否应当播放动画。任一处设置说"关"就不播。
    ///
    /// 带 5 秒缓存：这个函数在每次展开/收起时都会被调用，
    /// 而 SystemParametersInfo 可能落到注册表读。用户改设置后
    /// 最多 5 秒生效，感知不到。
    /// </summary>
    public static bool Enabled
    {
        get
        {
            var now = Environment.TickCount64;
            if (_cachedAt != 0 && now - _cachedAt < CacheMs) return _cached;

            _cached = ReadEnabled();
            _cachedAt = now;
            return _cached;
        }
    }

    private const int CacheMs = 5000;
    private static bool _cached = true;
    private static long _cachedAt;

    private static bool ReadEnabled()
    {
        try
        {
            if (SystemParametersInfoW(SPI_GETCLIENTAREAANIMATION, 0, out bool clientArea, 0)
                && !clientArea)
            {
                return false;
            }

            var info = new ANIMATIONINFO { cbSize = (uint)Marshal.SizeOf<ANIMATIONINFO>() };
            if (SystemParametersInfoW(SPI_GETANIMATION, info.cbSize, ref info, 0)
                && info.iMinAnimate == 0)
            {
                return false;
            }
        }
        catch
        {
            // 读不到就按"开"处理：动画是默认行为。
        }

        return true;
    }

    /// <summary>
    /// 按系统设置缩放时长。关闭动画返回 0（调用方据此直接落位）。
    /// </summary>
    public static int Scale(int durationMs) => Enabled ? durationMs : 0;
}
