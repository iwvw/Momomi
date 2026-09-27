using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Momomi.Core.Services;

public interface ISystemProxyService
{
    bool IsEnabled();
    string? CurrentServer();
    bool Enable(string server, string bypass);
    bool Disable();
}

public sealed class SystemProxyService : ISystemProxyService
{
    private const string RegPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private const int InternetOptionSettingsChanged = 39;
    private const int InternetOptionRefresh = 37;

    public bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegPath);
            return key?.GetValue("ProxyEnable") is int value && value == 1;
        }
        catch
        {
            return false;
        }
    }

    public string? CurrentServer()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegPath);
            return key?.GetValue("ProxyServer") as string;
        }
        catch
        {
            return null;
        }
    }

    public bool Enable(string server, string bypass)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegPath, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RegPath);
            if (key is null) return false;
            key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
            key.SetValue("ProxyServer", server, RegistryValueKind.String);
            key.SetValue("ProxyOverride", bypass, RegistryValueKind.String);
            Notify();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool Disable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegPath, writable: true);
            if (key is null) return false;
            key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
            Notify();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void Notify()
    {
        InternetSetOption(IntPtr.Zero, InternetOptionSettingsChanged, IntPtr.Zero, 0);
        InternetSetOption(IntPtr.Zero, InternetOptionRefresh, IntPtr.Zero, 0);
    }

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);
}
