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
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Momomi";

    public bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) is string value && !string.IsNullOrEmpty(value);
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
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key is null) return false;
            if (enabled)
            {
                var exe = Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrEmpty(exe))
                    exe = Path.Combine(AppContext.BaseDirectory, "Momomi.exe");
                key.SetValue(ValueName, $"\"{exe}\" --minimized");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            return true;
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
