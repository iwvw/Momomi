using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Momomi.Core.Services;

namespace Momomi.Elevated;

internal static class Program
{
    private static Process? _coreProcess;
    private static readonly object Gate = new();
    private static string? _lastError;

    // 提权宿主看不到主程序的日志，内核输出必须落盘，否则 TUN 等失败无从排查。
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Momomi", "elevated.log");

    private static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"[{DateTime.Now:O}] {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    private static async Task<int> Main(string[] args)
    {
        if (!IsAdministrator())
        {
            Console.Error.WriteLine("Momomi.Elevated 需要管理员权限。");
            return 5;
        }

        var ownerSid = GetOwnerSid();
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        // 监视主程序存活：主程序退出后自动结束本宿主，避免残留进程锁住可执行文件。
        var ownerPid = ParseOwnerPid(args);
        if (ownerPid > 0)
        {
            _ = Task.Run(() => WatchOwnerAsync(ownerPid, cts.Token));
        }

        while (!cts.IsCancellationRequested)
        {
            try
            {
                await using var server = CreateServer(ownerSid);
                await server.WaitForConnectionAsync(cts.Token).ConfigureAwait(false);
                await HandleClientAsync(server, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                await Task.Delay(200, cts.Token).ConfigureAwait(false);
            }
        }

        lock (Gate)
        {
            StopCore();
        }
        return 0;
    }

    private static int ParseOwnerPid(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--owner", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(args[i + 1], out var pid))
            {
                return pid;
            }
        }
        return -1;
    }

    private static async Task WatchOwnerAsync(int ownerPid, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (!IsProcessAlive(ownerPid))
            {
                lock (Gate)
                {
                    StopCore();
                }
                Environment.Exit(0);
            }
        }
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static NamedPipeServerStream CreateServer(string ownerSid)
    {
        var security = new PipeSecurity();

        // 提权进程的 Owner 会变成 BUILTIN\Administrators，因此不能只按 Owner 授权，
        // 否则普通权限的主程序连不上管道（Access denied）。
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(ownerSid),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            ElevatedProtocol.PipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            65536,
            65536,
            security);
    }

    private static string GetOwnerSid()
    {
        try
        {
            // 优先取当前登录用户 SID（提权后 Owner 会变成 Administrators，不能用它）。
            using var identity = WindowsIdentity.GetCurrent();
            if (identity.User is not null) return identity.User.Value;
        }
        catch
        {
        }
        return new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null).Value;
    }

    private static async Task HandleClientAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        using var reader = new StreamReader(server, Encoding.UTF8, false, 4096, leaveOpen: true);
        using var writer = new StreamWriter(server, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };

        var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(line)) return;

        ElevatedRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<ElevatedRequest>(line, ElevatedProtocol.JsonOptions);
        }
        catch
        {
            await WriteAsync(writer, new ElevatedResponse(false, "无法解析请求")).ConfigureAwait(false);
            return;
        }

        if (request is null)
        {
            await WriteAsync(writer, new ElevatedResponse(false, "空请求")).ConfigureAwait(false);
            return;
        }

        var response = request.Command switch
        {
            ElevatedProtocol.Start => StartCore(request),
            ElevatedProtocol.Stop => StopCoreCommand(),
            ElevatedProtocol.Status => StatusCommand(),
            ElevatedProtocol.Shutdown => ShutdownCommand(),
            _ => new ElevatedResponse(false, $"未知命令：{request.Command}"),
        };

        await WriteAsync(writer, response).ConfigureAwait(false);
    }

    private static async Task WriteAsync(StreamWriter writer, ElevatedResponse response)
    {
        var json = JsonSerializer.Serialize(response, ElevatedProtocol.JsonOptions);
        await writer.WriteLineAsync(json).ConfigureAwait(false);
    }

    private static ElevatedResponse StartCore(ElevatedRequest request)
    {
        if (string.IsNullOrEmpty(request.BinaryPath) || string.IsNullOrEmpty(request.ConfigPath))
            return new ElevatedResponse(false, "缺少内核路径或配置路径");

        lock (Gate)
        {
            if (_coreProcess is { HasExited: false })
                return new ElevatedResponse(true, "内核已在运行", _coreProcess.Id);

            if (!File.Exists(request.BinaryPath))
                return new ElevatedResponse(false, $"找不到内核：{request.BinaryPath}");
            if (!File.Exists(request.ConfigPath))
                return new ElevatedResponse(false, $"找不到配置：{request.ConfigPath}");

            var workingDir = request.WorkingDirectory ?? Path.GetDirectoryName(request.BinaryPath)!;

            var psi = new ProcessStartInfo
            {
                FileName = request.BinaryPath,
                WorkingDirectory = workingDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-d");
            psi.ArgumentList.Add(workingDir);
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add(request.ConfigPath);
            if (!string.IsNullOrEmpty(request.Secret))
            {
                psi.ArgumentList.Add("-secret");
                psi.ArgumentList.Add(request.Secret);
            }

            try
            {
                var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
                process.OutputDataReceived += (_, e) => Capture(e.Data);
                process.ErrorDataReceived += (_, e) => Capture(e.Data);
                process.Exited += (_, _) => _lastError = $"内核已退出（代码 {process.ExitCode}）";

                if (!process.Start())
                    return new ElevatedResponse(false, "无法启动内核进程");

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                _coreProcess = process;
                _lastError = null;
                return new ElevatedResponse(true, "内核已启动", process.Id);
            }
            catch (Exception ex)
            {
                return new ElevatedResponse(false, ex.Message);
            }
        }
    }

    private static void Capture(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        _lastError = line;
        Log(line);
    }

    private static ElevatedResponse StopCoreCommand()
    {
        lock (Gate)
        {
            StopCore();
            return new ElevatedResponse(true, "内核已停止");
        }
    }

    private static void StopCore()
    {
        var process = _coreProcess;
        _coreProcess = null;
        if (process is null) return;
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
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

    private static ElevatedResponse StatusCommand()
    {
        lock (Gate)
        {
            if (_coreProcess is { HasExited: false })
                return new ElevatedResponse(true, _lastError, _coreProcess.Id);
            return new ElevatedResponse(false, _lastError ?? "内核未运行");
        }
    }

    private static ElevatedResponse ShutdownCommand()
    {
        lock (Gate)
        {
            StopCore();
        }
        _ = Task.Run(async () =>
        {
            await Task.Delay(300).ConfigureAwait(false);
            Environment.Exit(0);
        });
        return new ElevatedResponse(true, "提权宿主正在退出");
    }

    private static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
}
