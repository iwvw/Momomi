using System.Runtime.InteropServices;

namespace Momomi.App.Services;

/// <summary>
/// 全局快捷键服务。基于 Win32 RegisterHotKey + 一个仅消息窗口接收 WM_HOTKEY，
/// 触发时回调到注册的动作。支持注册/替换/注销，重复或被占用会返回失败。
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int WM_DESTROY = 0x0002;

    private readonly object _gate = new();
    private readonly Dictionary<int, (HotkeyAction Action, Action Callback)> _handlers = new();
    private readonly Dictionary<HotkeyAction, (uint Mods, uint Vk)> _registered = new();
    private readonly WndProcDelegate _wndProc;
    private readonly string _className;
    private IntPtr _hwnd;
    private int _nextId = 1;
    private bool _disposed;

    public HotkeyService()
    {
        _wndProc = WndProc;
        _className = "MomomiHotkeyWindow_" + Guid.NewGuid().ToString("N");
        CreateMessageWindow();
    }

    /// <summary>为动作注册快捷键；替换旧绑定。返回是否成功（被占用返回 false）。</summary>
    public bool Register(HotkeyAction action, uint modifiers, uint vk, Action callback)
    {
        if (_disposed || _hwnd == IntPtr.Zero) return false;
        if (modifiers == 0 || vk == 0) return false;

        lock (_gate)
        {
            // 先注销该动作的旧绑定。
            UnregisterLocked(action);

            var id = _nextId++;
            var mods = modifiers | HotkeyParser.MOD_NOREPEAT;
            if (!RegisterHotKey(_hwnd, id, mods, vk)) return false;

            _handlers[id] = (action, callback);
            _registered[action] = (modifiers, vk);
            return true;
        }
    }

    public void Unregister(HotkeyAction action)
    {
        lock (_gate) UnregisterLocked(action);
    }

    private void UnregisterLocked(HotkeyAction action)
    {
        if (!_registered.TryGetValue(action, out var _)) return;
        foreach (var kv in _handlers.Where(h => h.Value.Action == action).ToList())
        {
            _ = UnregisterHotKey(_hwnd, kv.Key);
            _handlers.Remove(kv.Key);
        }
        _registered.Remove(action);
    }

    private void CreateMessageWindow()
    {
        var hInstance = GetModuleHandle(null);
        var wndClass = new WNDCLASS
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = hInstance,
            lpszClassName = _className,
        };
        RegisterClass(ref wndClass);
        _hwnd = CreateWindowEx(0, _className, _className, 0, 0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_HOTKEY)
        {
            var id = wParam.ToInt32();
            Action? cb = null;
            lock (_gate)
            {
                if (_handlers.TryGetValue(id, out var entry)) cb = entry.Callback;
            }
            cb?.Invoke();
            return IntPtr.Zero;
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_gate)
        {
            foreach (var id in _handlers.Keys.ToList())
                _ = UnregisterHotKey(_hwnd, id);
            _handlers.Clear();
            _registered.Clear();
        }
        if (_hwnd != IntPtr.Zero)
        {
            _ = DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
        _ = UnregisterClass(_className, GetModuleHandle(null));
    }

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASS
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClass(ref WNDCLASS lpWndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UnregisterClass(string lpClassName, IntPtr hInstance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int dwExStyle, string lpClassName, string lpWindowName,
        int dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
