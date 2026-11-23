using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Cryptography;
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

    // 每次宿主启动时由主程序随机生成并经命令行传入的会话令牌。
    // 仅持有该令牌的请求会被执行,防止同机其它进程复用管道下发命令。
    private static string _token = string.Empty;

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
        catch (Exception ex)
        {
            // 日志是提权宿主唯一可观测渠道,写失败时至少要能落到 stderr。
            try { Console.Error.WriteLine($"[Momomi.Elevated] 写日志失败:{ex.Message}"); } catch { }
        }
    }

    private static async Task<int> Main(string[] args)
    {
        if (!IsAdministrator())
        {
            Console.Error.WriteLine("Momomi.Elevated 需要管理员权限。");
            return 5;
        }

        _token = ParseArg(args, "--token") ?? string.Empty;
        if (_token.Length < 32)
        {
            Console.Error.WriteLine("缺少有效的会话令牌,拒绝启动。");
            return 6;
        }

        // 只允许发起提权的那个用户连接(而非所有已认证用户)。
        var clientSid = ParseArg(args, "--client-sid") ?? GetOwnerSid();
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        // 监视主程序存活：主程序退出后自动结束本宿主，避免残留进程锁住可执行文件。
        var ownerPid = ParseIntArg(args, "--owner");
        var ownerStartTicks = ParseLongArg(args, "--owner-start");
        if (ownerPid > 0)
        {
            _ = Task.Run(() => WatchOwnerAsync(ownerPid, ownerStartTicks, cts.Token));
        }

        while (!cts.IsCancellationRequested)
        {
            try
            {
                await using var server = CreateServer(clientSid);
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
                Log($"接受连接出错:{ex}");
                // 用不关联取消的令牌等待,避免取消时异常从 catch 逃逸导致非正常退出。
                try { await Task.Delay(200, CancellationToken.None).ConfigureAwait(false); }
                catch { }
            }
        }

        lock (Gate)
        {
            StopCore();
        }
        return 0;
    }

    private static string? ParseArg(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        }
        return null;
    }

    private static int ParseIntArg(string[] args, string name)
        => int.TryParse(ParseArg(args, name), out var value) ? value : -1;

    private static long ParseLongArg(string[] args, string name)
        => long.TryParse(ParseArg(args, name), out var value) ? value : 0;

    private static async Task WatchOwnerAsync(int ownerPid, long ownerStartTicks, CancellationToken ct)
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

            if (!IsProcessAlive(ownerPid, ownerStartTicks))
            {
                Log("主程序已退出,提权宿主结束。");
                lock (Gate)
                {
                    StopCore();
                }
                Environment.Exit(0);
            }
        }
    }

    private static bool IsProcessAlive(int pid, long expectedStartTicks)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited) return false;

            // PID 会被复用:启动时间不一致说明原主程序已退出、此处是新进程。
            if (expectedStartTicks > 0)
            {
                try
                {
                    if (process.StartTime.ToUniversalTime().Ticks != expectedStartTicks) return false;
                }
                catch
                {
                    return false;
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static NamedPipeServerStream CreateServer(string clientSid)
    {
        var security = new PipeSecurity();

        // 提权进程的 Owner 会变成 BUILTIN\Administrators，因此不能只按 Owner 授权，
        // 否则普通权限的主程序连不上管道（Access denied）。
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(clientSid),
            PipeAccessRights.FullControl,
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
        // 取不到用户 SID 时退回到管理员组,而不是对所有已认证用户开放。
        return new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value;
    }

    private static async Task HandleClientAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        using var reader = new StreamReader(server, Encoding.UTF8, false, 4096, leaveOpen: true);
        using var writer = new StreamWriter(server, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };

        // 限制读取时间与行长,避免未授权连接长期占用唯一实例或放大内存占用。
        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        readCts.CancelAfter(TimeSpan.FromSeconds(15));
        string? line;
        try
        {
            line = await reader.ReadLineAsync(readCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(line) || line.Length > 65536) return;

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

        if (request is null || !TokenMatches(request.Token))
        {
            // 不回显具体原因,避免给未授权调用方任何信息。
            await WriteAsync(writer, new ElevatedResponse(false, "未授权")).ConfigureAwait(false);
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

    private static bool TokenMatches(string? token)
    {
        if (string.IsNullOrEmpty(token)) return false;
        var provided = Encoding.UTF8.GetBytes(token);
        var expected = Encoding.UTF8.GetBytes(_token);
        return provided.Length == expected.Length && CryptographicOperations.FixedTimeEquals(provided, expected);
    }

    private static ElevatedResponse StartCore(ElevatedRequest request)
    {
        if (string.IsNullOrEmpty(request.BinaryPath) || string.IsNullOrEmpty(request.ConfigPath)
            || string.IsNullOrEmpty(request.WorkingDirectory))
            return new ElevatedResponse(false, "缺少内核路径或配置路径");

        string binaryPath, configPath, workingDir;
        try
        {
            workingDir = Path.GetFullPath(request.WorkingDirectory);
            binaryPath = Path.GetFullPath(request.BinaryPath);
            configPath = Path.GetFullPath(request.ConfigPath);
        }
        catch (Exception ex)
        {
            Log($"内核路径无效:{ex.Message}");
            return new ElevatedResponse(false, "内核路径无效");
        }

        // 只允许启动工作目录内的官方内核与其运行时配置,防止宿主被当作任意程序启动器。
        if (!Directory.Exists(workingDir)
            || !string.Equals(Path.GetFileName(binaryPath), "mihomo.exe", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetFileName(configPath), "runtime.yaml", StringComparison.OrdinalIgnoreCase)
            || !IsDirectlyInside(workingDir, binaryPath)
            || !IsDirectlyInside(workingDir, configPath))
        {
            Log($"拒绝启动:二进制或配置不在允许范围({binaryPath} | {configPath})");
            return new ElevatedResponse(false, "内核路径不在允许范围");
        }

        lock (Gate)
        {
            if (_coreProcess is { HasExited: false })
                return new ElevatedResponse(true, "内核已在运行", _coreProcess.Id);

            if (!File.Exists(request.BinaryPath))
                return new ElevatedResponse(false, $"找不到内核：{request.BinaryPath}");
            if (!File.Exists(request.ConfigPath))
                return new ElevatedResponse(false, $"找不到配置：{request.ConfigPath}");

            var psi = new ProcessStartInfo
            {
                FileName = binaryPath,
                WorkingDirectory = workingDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-d");
            psi.ArgumentList.Add(workingDir);
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add(configPath);
            // secret 已写入 runtime.yaml,不再经命令行传递,避免同机进程从命令行读取。

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
                Log($"启动内核失败:{ex}");
                return new ElevatedResponse(false, "内核启动失败,详情见 elevated.log");
            }
        }
    }

    private static bool IsDirectlyInside(string directory, string filePath)
    {
        var dir = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parent = Path.GetDirectoryName(filePath)?
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(parent, dir, StringComparison.OrdinalIgnoreCase);
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
