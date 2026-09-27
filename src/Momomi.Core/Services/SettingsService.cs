using System.Diagnostics;
using Microsoft.Win32;
using Momomi.Data;

namespace Momomi.Core.Services;

public interface IStartupService
{
    bool IsEnabled();
    bool SetEnabled(bool enabled);
}

public sealed class StartupService : IStartupService
{
    private const string TaskName = "Momomi";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Momomi";

    // 主程序以管理员身份运行，注册表 Run 键无法带提权启动，因此自启改用计划任务
    // （RunLevel=Highest，登录时以最高权限静默运行，不弹 UAC）。

    public bool IsEnabled()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/Query /TN \"{TaskName}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return false;
            proc.WaitForExit(5000);
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public bool SetEnabled(bool enabled)
    {
        try
        {
            var exe = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exe))
                exe = Path.Combine(AppContext.BaseDirectory, "Momomi.exe");

            if (enabled)
            {
                // 先删旧任务，再创建：登录时以最高权限运行（/RL HIGHEST），静默启动。
                RunSchtasks($"/Delete /TN \"{TaskName}\" /F");
                var create = $"/Create /TN \"{TaskName}\" /TR \"\\\"{exe}\\\" --minimized\" " +
                             "/SC ONLOGON /RL HIGHEST /F";
                return RunSchtasks(create);
            }

            return RunSchtasks($"/Delete /TN \"{TaskName}\" /F");
        }
        catch
        {
            return false;
        }
    }

    private static bool RunSchtasks(string args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return false;
            proc.WaitForExit(15000);
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}

public interface ISettingsService
{
    Task<string?> GetAsync(string key);
    Task SetAsync(string key, string value);
    Task<bool> GetBoolAsync(string key, bool fallback = false);
    Task SetBoolAsync(string key, bool value);
    Task<int> GetIntAsync(string key, int fallback);
    Task SetIntAsync(string key, int value);
    Task<T> GetEnumAsync<T>(string key, T fallback) where T : struct, Enum;
    Task SetEnumAsync<T>(string key, T value) where T : struct, Enum;
}

public sealed class SettingsService : ISettingsService
{
    private readonly SettingsRepository _repo;

    public SettingsService(MomomiDatabase database)
    {
        _repo = new SettingsRepository(database);
    }

    public Task<string?> GetAsync(string key) => _repo.GetAsync(key);

    public Task SetAsync(string key, string value) => _repo.SetAsync(key, value);

    public async Task<bool> GetBoolAsync(string key, bool fallback = false)
    {
        var raw = await _repo.GetAsync(key).ConfigureAwait(false);
        return raw is null ? fallback : raw == "true";
    }

    public Task SetBoolAsync(string key, bool value) => _repo.SetAsync(key, value ? "true" : "false");

    public async Task<int> GetIntAsync(string key, int fallback)
    {
        var raw = await _repo.GetAsync(key).ConfigureAwait(false);
        return int.TryParse(raw, out var value) ? value : fallback;
    }

    public Task SetIntAsync(string key, int value) => _repo.SetAsync(key, value.ToString());

    public async Task<T> GetEnumAsync<T>(string key, T fallback) where T : struct, Enum
    {
        var raw = await _repo.GetAsync(key).ConfigureAwait(false);
        return Enum.TryParse<T>(raw, ignoreCase: true, out var value) ? value : fallback;
    }

    public Task SetEnumAsync<T>(string key, T value) where T : struct, Enum
        => _repo.SetAsync(key, value.ToString());
}
