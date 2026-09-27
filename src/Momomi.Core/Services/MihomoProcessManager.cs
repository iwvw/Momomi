using System.Diagnostics;
using System.Text.Json;

namespace Momomi.Core.Services;

public sealed record CoreLaunchOptions(
    string BinaryPath,
    string WorkingDirectory,
    string ConfigPath,
    string? Secret);

public interface IMihomoProcessManager : IDisposable
{
    bool IsRunning { get; }
    int? ProcessId { get; }
    event EventHandler<CoreProcessExitedEventArgs>? Exited;

    Task<bool> ValidateConfigAsync(CoreLaunchOptions options, CancellationToken ct = default);
    Task<bool> StartAsync(CoreLaunchOptions options, CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
    Task<IReadOnlyList<int>> KillOrphansAsync(string binaryPath, CancellationToken ct = default);
}

public sealed record CoreProcessExitedEventArgs(int ExitCode, string? LastError);

public sealed class MihomoProcessManager : IMihomoProcessManager
{
    private readonly object _gate = new();
    private Process? _process;
    private readonly List<string> _recentError = new();

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _process is { HasExited: false };
            }
        }
    }

    public int? ProcessId
    {
        get
        {
            lock (_gate)
            {
                return _process is { HasExited: false } ? _process.Id : null;
            }
        }
    }

    public event EventHandler<CoreProcessExitedEventArgs>? Exited;

    public async Task<bool> ValidateConfigAsync(CoreLaunchOptions options, CancellationToken ct = default)
    {
        if (!File.Exists(options.BinaryPath) || !File.Exists(options.ConfigPath)) return false;

        var psi = new ProcessStartInfo
        {
            FileName = options.BinaryPath,
            WorkingDirectory = options.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-t");
        psi.ArgumentList.Add("-d");
        psi.ArgumentList.Add(options.WorkingDirectory);
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add(options.ConfigPath);

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null) return false;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            try
            {
                await proc.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(true); } catch { }
                return false;
            }
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> StartAsync(CoreLaunchOptions options, CancellationToken ct = default)
    {
        await StopAsync(ct).ConfigureAwait(false);

        if (!File.Exists(options.BinaryPath))
            throw new FileNotFoundException("找不到 mihomo 内核可执行文件", options.BinaryPath);

        var psi = new ProcessStartInfo
        {
            FileName = options.BinaryPath,
            WorkingDirectory = options.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-d");
        psi.ArgumentList.Add(options.WorkingDirectory);
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add(options.ConfigPath);
        if (!string.IsNullOrEmpty(options.Secret))
        {
            psi.ArgumentList.Add("-secret");
            psi.ArgumentList.Add(options.Secret);
        }

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => CaptureLine(e.Data);
        process.ErrorDataReceived += (_, e) => CaptureLine(e.Data);
        process.Exited += OnProcessExited;

        lock (_gate)
        {
            _process = process;
            _recentError.Clear();
        }

        if (!process.Start()) return false;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process.HasExited == false;
    }

    private void CaptureLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        lock (_gate)
        {
            _recentError.Add(line);
            if (_recentError.Count > 50) _recentError.RemoveAt(0);
        }
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        int exitCode;
        string? lastError;
        lock (_gate)
        {
            exitCode = _process?.ExitCode ?? -1;
            lastError = _recentError.Count > 0 ? string.Join(Environment.NewLine, _recentError.TakeLast(10)) : null;
        }
        Exited?.Invoke(this, new CoreProcessExitedEventArgs(exitCode, lastError));
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        Process? process;
        lock (_gate)
        {
            process = _process;
            _process = null;
        }
        if (process is null) return;

        try
        {
            process.Exited -= OnProcessExited;
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                try
                {
                    await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }
        }
        catch
        {
        }
        finally
        {
            process.Dispose();
        }
    }

    public async Task<IReadOnlyList<int>> KillOrphansAsync(string binaryPath, CancellationToken ct = default)
    {
        var killed = new List<int>();
        if (!File.Exists(binaryPath)) return killed;

        var targetPath = Path.GetFullPath(binaryPath);
        var expectedDirectory = Path.GetDirectoryName(targetPath)!;
        var processName = Path.GetFileNameWithoutExtension(targetPath);

        try
        {
            var all = Process.GetProcessesByName(processName);
            var currentPid = Environment.ProcessId;

            foreach (var victim in all)
            {
                try
                {
                    if (victim.Id == currentPid) continue;

                    string? victimPath = null;
                    try { victimPath = victim.MainModule?.FileName; } catch { }
                    if (string.IsNullOrEmpty(victimPath)) continue;

                    // 仅清理本应用工作目录下的内核，避免误杀其他客户端（如 clash-party）的进程。
                    var victimDirectory = Path.GetDirectoryName(Path.GetFullPath(victimPath));
                    if (!string.Equals(victimDirectory, expectedDirectory, StringComparison.OrdinalIgnoreCase))
                        continue;

                    victim.Kill(entireProcessTree: true);
                    await victim.WaitForExitAsync(ct).ConfigureAwait(false);
                    killed.Add(victim.Id);
                }
                catch
                {
                }
                finally
                {
                    victim.Dispose();
                }
            }
        }
        catch
        {
        }
        return killed;
    }

    public void Dispose()
    {
        try
        {
            StopAsync().GetAwaiter().GetResult();
        }
        catch
        {
        }
    }
}
