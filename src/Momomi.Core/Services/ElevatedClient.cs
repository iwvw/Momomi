using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Momomi.Core.Services;

public interface IElevatedClient
{
    bool IsElevatedHostRunning { get; }
    Task<ElevatedResponse> StartCoreAsync(string binaryPath, string workingDirectory, string configPath, string? secret, CancellationToken ct = default);
    Task<ElevatedResponse> StopCoreAsync(CancellationToken ct = default);
    Task<ElevatedResponse> GetStatusAsync(CancellationToken ct = default);
    Task<ElevatedResponse> ShutdownAsync(CancellationToken ct = default);
    bool LaunchElevatedHost();
    void KillOrphanHosts();
}

public sealed class ElevatedClient : IElevatedClient
{
    private readonly string _hostPath;
    private readonly object _gate = new();
    private Process? _hostProcess;

    public ElevatedClient(string hostPath)
    {
        _hostPath = hostPath;
    }

    public bool IsElevatedHostRunning
    {
        get
        {
            lock (_gate)
            {
                if (_hostProcess is { HasExited: false }) return true;
            }

            // 本进程没启动过宿主时，通过命名管道探测是否已有宿主在运行。
            try
            {
                using var pipe = new NamedPipeClientStream(
                    ".", ElevatedProtocol.PipeName, PipeDirection.InOut);
                pipe.Connect(300);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public void KillOrphanHosts()
    {
        lock (_gate)
        {
            if (_hostProcess is not null)
            {
                try
                {
                    if (!_hostProcess.HasExited)
                    {
                        _hostProcess.Kill();
                        _hostProcess.WaitForExit(3000);
                    }
                }
                catch
                {
                }
                finally
                {
                    _hostProcess.Dispose();
                    _hostProcess = null;
                }
            }
        }

        // 再按可执行文件完整路径匹配，结束本进程未持有句柄的遗留宿主。
        try
        {
            var target = Path.GetFullPath(_hostPath);
            foreach (var process in Process.GetProcessesByName("Momomi.Elevated"))
            {
                try
                {
                    var path = process.MainModule?.FileName;
                    if (path is not null &&
                        string.Equals(Path.GetFullPath(path), target, StringComparison.OrdinalIgnoreCase))
                    {
                        process.Kill();
                        process.WaitForExit(3000);
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
        }
        catch
        {
        }
    }

    public bool LaunchElevatedHost()
    {
        lock (_gate)
        {
            if (_hostProcess is { HasExited: false }) return true;
            if (!File.Exists(_hostPath)) return false;

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = _hostPath,
                    // 显式传递主程序 PID：提权进程的父进程会被 UAC 改写，不能靠父进程关系判断存活。
                    Arguments = $"--owner {Environment.ProcessId}",
                    UseShellExecute = true,
                    Verb = "runas",
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                };
                _hostProcess = Process.Start(psi);
                return _hostProcess is not null;
            }
            catch
            {
                return false;
            }
        }
    }

    public async Task<ElevatedResponse> StartCoreAsync(string binaryPath, string workingDirectory, string configPath, string? secret, CancellationToken ct = default)
        => await SendAsync(new ElevatedRequest(ElevatedProtocol.Start, workingDirectory, configPath, binaryPath, secret), ct).ConfigureAwait(false);

    public async Task<ElevatedResponse> StopCoreAsync(CancellationToken ct = default)
        => await SendAsync(new ElevatedRequest(ElevatedProtocol.Stop), ct).ConfigureAwait(false);

    public async Task<ElevatedResponse> GetStatusAsync(CancellationToken ct = default)
        => await SendAsync(new ElevatedRequest(ElevatedProtocol.Status), ct).ConfigureAwait(false);

    public async Task<ElevatedResponse> ShutdownAsync(CancellationToken ct = default)
        => await SendAsync(new ElevatedRequest(ElevatedProtocol.Shutdown), ct).ConfigureAwait(false);

    private async Task<ElevatedResponse> SendAsync(ElevatedRequest request, CancellationToken ct)
    {
        try
        {
            await using var pipe = new NamedPipeClientStream(
                ".", ElevatedProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);

            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);

            var json = JsonSerializer.Serialize(request, ElevatedProtocol.JsonOptions);
            await writer.WriteLineAsync(json).ConfigureAwait(false);

            var line = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(line))
                return new ElevatedResponse(false, "提权宿主无响应");

            return JsonSerializer.Deserialize<ElevatedResponse>(line, ElevatedProtocol.JsonOptions)
                   ?? new ElevatedResponse(false, "无法解析提权宿主响应");
        }
        catch (OperationCanceledException)
        {
            return new ElevatedResponse(false, "连接提权宿主超时");
        }
        catch (Exception ex)
        {
            return new ElevatedResponse(false, ex.Message);
        }
    }
}
