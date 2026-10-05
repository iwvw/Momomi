using System.Diagnostics;
using System.Security;
using System.Security.Principal;
using Microsoft.Win32;
using Momomi.Data;

namespace Momomi.Core.Services;

public interface IStartupService
{
    bool IsEnabled();
    bool SetEnabled(bool enabled);

    /// <summary>
    /// 若已存在自启任务但配置不良（电源/空闲/超时会终止进程），则重建为正确配置。
    /// 返回是否执行了修复。
    /// </summary>
    bool RepairIfNeeded();
}

public sealed class StartupService : IStartupService
{
    private const string TaskName = "Momomi";

    // 主程序以管理员身份运行，注册表 Run 键无法带提权启动，因此自启改用计划任务
    // （RunLevel=Highest，登录时以最高权限静默运行，不弹 UAC）。
    //
    // 必须用 XML 创建任务：schtasks 命令行的默认参数会带上
    // DisallowStartIfOnBatteries / StopIfGoingOnBatteries / StopOnIdleEnd=true
    // 与默认 ExecutionTimeLimit(PT72H)，导致笔记本拔电源、系统空闲或连续运行 72 小时后
    // 任务计划程序直接终止进程，表现为「程序自己退出」。这些只能通过 XML 关闭。

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
            if (!enabled)
                return RunSchtasks($"/Delete /TN \"{TaskName}\" /F");

            var exe = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exe))
                exe = Path.Combine(AppContext.BaseDirectory, "Momomi.exe");

            var xml = BuildTaskXml(exe);
            var tmp = Path.Combine(Path.GetTempPath(), $"momomi-task-{Guid.NewGuid():N}.xml");
            try
            {
                // schtasks 读取 XML 要求 UTF-16；写入带 BOM 的 Unicode。
                File.WriteAllText(tmp, xml, new System.Text.UnicodeEncoding(false, true));
                // 先删旧任务，再以 XML 创建，避免残留旧配置。
                RunSchtasks($"/Delete /TN \"{TaskName}\" /F");
                return RunSchtasks($"/Create /TN \"{TaskName}\" /XML \"{tmp}\" /F");
            }
            finally
            {
                try { File.Delete(tmp); } catch { }
            }
        }
        catch
        {
            return false;
        }
    }

    public bool RepairIfNeeded()
    {
        try
        {
            if (!IsEnabled()) return false;
            var xml = QueryXml();
            if (string.IsNullOrEmpty(xml)) return false;
            if (!NeedsRepair(xml)) return false;
            return SetEnabled(true);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>判断已有任务是否含会终止进程的不良设置。</summary>
    private static bool NeedsRepair(string xml)
    {
        // 电池相关默认值会终止任务。
        if (xml.Contains("<StopIfGoingOnBatteries>true", StringComparison.OrdinalIgnoreCase)) return true;
        if (xml.Contains("<DisallowStartIfOnBatteries>true", StringComparison.OrdinalIgnoreCase)) return true;
        if (xml.Contains("<StopOnIdleEnd>true", StringComparison.OrdinalIgnoreCase)) return true;
        // 未显式关闭超时（应为 PT0S）。
        if (!xml.Contains("<ExecutionTimeLimit>PT0S</ExecutionTimeLimit>", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    private static string? QueryXml()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/Query /TN \"{TaskName}\" /XML",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return null;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5000);
            return proc.ExitCode == 0 ? output : null;
        }
        catch
        {
            return null;
        }
    }

    private static string BuildTaskXml(string exe)
    {
        string user;
        try
        {
            user = WindowsIdentity.GetCurrent().User?.Value
                ?? WindowsIdentity.GetCurrent().Name;
        }
        catch
        {
            user = Environment.UserName;
        }

        var author = SecurityElement.Escape(user) ?? user;
        var command = SecurityElement.Escape(exe) ?? exe;

        return $"""
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Author>{author}</Author>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>{author}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>{command}</Command>
      <Arguments>--minimized</Arguments>
    </Exec>
  </Actions>
</Task>
""";
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
