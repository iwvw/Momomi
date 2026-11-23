using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace Momomi.App.ViewModels;

/// <summary>
/// 提取进程可执行文件的大图标,供连接列表显示。
///
/// 代价较高的 Shell 查询(SHGetFileInfo)与 GDI+ PNG 编码放到后台线程;
/// 图标解码与 <see cref="BitmapImage"/> 创建留在 UI 线程(有线程亲和性)。
/// 按路径缓存,同一路径并发请求只提取一次、只解码一份。
/// </summary>
public static class ProcessIconProvider
{
    private sealed class Entry
    {
        public readonly Lazy<byte[]?> Png;
        public ImageSource? Icon;
        public long LastAccessTicks;

        public Entry(string path)
        {
            Png = new Lazy<byte[]?>(() =>
            {
                try { return EncodePng(path); }
                catch { return null; }
            }, LazyThreadSafetyMode.ExecutionAndPublication);
        }
    }

    private static readonly ConcurrentDictionary<string, Entry> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, byte> Pending = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, List<Action<string, ImageSource?>>> Waiters =
        new(StringComparer.OrdinalIgnoreCase);
    // 等待者登记与完成回调共用这把锁,消除"完成回调摘除列表后才登记"的竞态。
    private static readonly object Sync = new();
    // 已解析但无图标的路径:记录终态,避免每个引用它的新行都重复提取一次。
    private static readonly ConcurrentDictionary<string, byte> Failed = new(StringComparer.OrdinalIgnoreCase);

    private const int MaxEntries = 256;
    private static readonly long IdleTimeoutTicks = TimeSpan.FromMinutes(10).Ticks;
    private static long _lastSweepUtcTicks;

    /// <summary>清空图标缓存。图标是解码后的原生位图,长期不用可整体释放。</summary>
    public static void ClearCache()
    {
        Cache.Clear();
        Pending.Clear();
        Waiters.Clear();
        Failed.Clear();
    }

    /// <summary>只返回已缓存好的图标;未就绪时返回 null,由 <see cref="Resolve"/> 异步补上。</summary>
    public static ImageSource? Get(string? processPath)
    {
        if (string.IsNullOrWhiteSpace(processPath)) return null;
        if (!Cache.TryGetValue(processPath, out var entry)) return null;
        entry.LastAccessTicks = DateTime.UtcNow.Ticks;
        return entry.Icon;
    }

    /// <summary>
    /// 取图标:已缓存则立即回调;否则后台提取 PNG,再回到 <paramref name="dispatcher"/> 上解码并回调。
    /// 同一路径并发调用只提取、解码一次,结果广播给所有等待者。
    /// </summary>
    public static void Resolve(string? processPath, DispatcherQueue dispatcher, Action<string, ImageSource?> onReady)
    {
        if (string.IsNullOrWhiteSpace(processPath))
        {
            onReady(processPath ?? "", null);
            return;
        }

        var now = DateTime.UtcNow.Ticks;
        var entry = Cache.GetOrAdd(processPath, static p => new Entry(p));
        entry.LastAccessTicks = now;
        MaybeSweep(now);

        if (entry.Icon is not null)
        {
            onReady(processPath, entry.Icon);
            return;
        }

        if (Failed.ContainsKey(processPath))
        {
            onReady(processPath, null);
            return;
        }

        bool start;
        lock (Sync)
        {
            var waiters = Waiters.GetOrAdd(processPath, static _ => new List<Action<string, ImageSource?>>());
            waiters.Add(onReady);
            start = Pending.TryAdd(processPath, 0);
        }
        if (!start) return;

        _ = Task.Run(() =>
        {
            byte[]? bytes;
            try
            {
                bytes = entry.Png.Value;
            }
            catch
            {
                bytes = null;
            }
            finally
            {
                Pending.TryRemove(processPath, out _);
            }

            dispatcher.TryEnqueue(() =>
            {
                ImageSource? image = null;
                if (bytes is { Length: > 0 })
                {
                    try { image = DecodePng(bytes); }
                    catch { image = null; }
                }

                if (image is not null) entry.Icon = image;
                else Failed[processPath] = 0;

                List<Action<string, ImageSource?>> copy;
                lock (Sync)
                {
                    // 用同一把锁摘除等待者列表:否则可能在登记前后摘除,导致等待者永久收不到通知。
                    Waiters.TryRemove(processPath, out var list);
                    copy = list is null
                        ? new List<Action<string, ImageSource?>>()
                        : new List<Action<string, ImageSource?>>(list);
                }

                foreach (var callback in copy)
                {
                    try { callback(processPath, image); } catch { }
                }
            });
        });
    }

    private static void MaybeSweep(long now)
    {
        var last = Interlocked.Read(ref _lastSweepUtcTicks);
        if (now - last < TimeSpan.FromMinutes(1).Ticks) return;
        if (Interlocked.CompareExchange(ref _lastSweepUtcTicks, now, last) != last) return;

        var cutoff = now - IdleTimeoutTicks;
        foreach (var kv in Cache)
        {
            if (kv.Value.LastAccessTicks < cutoff) Cache.TryRemove(kv.Key, out _);
        }

        while (Cache.Count > MaxEntries)
        {
            string? oldestKey = null;
            var oldest = long.MaxValue;
            foreach (var kv in Cache)
            {
                if (kv.Value.LastAccessTicks >= oldest) continue;
                oldest = kv.Value.LastAccessTicks;
                oldestKey = kv.Key;
            }
            if (oldestKey is null) break;
            Cache.TryRemove(oldestKey, out _);
        }
    }

    /// <summary>后台线程:SHGetFileInfo 取 HICON,再编码为 PNG 字节(不触碰任何 WinUI 对象)。</summary>
    private static byte[]? EncodePng(string path)
    {
        // 注意:不检查 File.Exists —— 连接记录里的进程路径常指向已退出进程/虚拟路径,
        // SHGFI_USEFILEATTRIBUTES 允许在这种情况下仍按扩展名返回关联图标。
        var info = new SHFILEINFO();
        var flags = SHGFI_ICON | SHGFI_LARGEICON | SHGFI_USEFILEATTRIBUTES;
        var result = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), flags);
        if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;

        try
        {
            using var icon = System.Drawing.Icon.FromHandle(info.hIcon);
            using var bitmap = icon.ToBitmap();
            using var stream = new MemoryStream();
            bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
            return stream.ToArray();
        }
        finally
        {
            DestroyIcon(info.hIcon);
        }
    }

    /// <summary>UI 线程:把 PNG 字节解码成 <see cref="BitmapImage"/>。</summary>
    private static ImageSource DecodePng(byte[] bytes)
    {
        var image = new BitmapImage();
        var ras = new MemoryStream(bytes).AsRandomAccessStream();
        // SetSourceAsync 是异步的,这里不能在方法返回前释放流;
        // 所有权交给 BitmapImage:ImageOpened / ImageFailed 里统一收尾,两条路径都释放。
        image.ImageOpened += (_, _) => { try { ras.Dispose(); } catch { } };
        image.ImageFailed += (_, _) => { try { ras.Dispose(); } catch { } };
        _ = image.SetSourceAsync(ras);
        return image;
    }

    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_LARGEICON = 0x000000000;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
        ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
