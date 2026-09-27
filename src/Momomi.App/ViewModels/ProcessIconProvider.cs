using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace Momomi.App.ViewModels;

/// <summary>
/// 提取进程可执行文件的大图标，供连接列表显示。按路径缓存，
/// 通过 Win32 SHGetFileInfo 取 HICON，再用 System.Drawing 转成 PNG 交给 WinUI。
/// </summary>
public static class ProcessIconProvider
{
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    public static ImageSource? Get(string? processPath)
    {
        if (string.IsNullOrWhiteSpace(processPath)) return null;

        lock (Gate)
        {
            if (Cache.TryGetValue(processPath, out var cached)) return cached;
        }

        ImageSource? source = null;
        try
        {
            source = ExtractIcon(processPath);
        }
        catch
        {
            source = null;
        }

        lock (Gate)
        {
            Cache[processPath] = source;
        }
        return source;
    }

    private static ImageSource? ExtractIcon(string path)
    {
        if (!System.IO.File.Exists(path)) return null;

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
            stream.Position = 0;

            var image = new BitmapImage();
            var ras = stream.AsRandomAccessStream();
            _ = image.SetSourceAsync(ras);
            return image;
        }
        finally
        {
            DestroyIcon(info.hIcon);
        }
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
