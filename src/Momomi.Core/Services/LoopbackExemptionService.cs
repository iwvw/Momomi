using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Momomi.Core.Services;

public sealed record LoopbackApplyResult(
    bool Success,
    int Added,
    int Total,
    int Already,
    string? Error = null);

public interface ILoopbackExemptionService
{
    /// <summary>当前进程是否具备管理员权限（操作回环豁免需要）。</summary>
    bool IsElevated { get; }

    /// <summary>当前已豁免的 AppContainer / 包数量。</summary>
    int GetExemptedCount();

    /// <summary>为所有已安装的 UWP 应用解除本地回环限制，返回新增与总计数量。</summary>
    LoopbackApplyResult ExemptAll();

    /// <summary>清除全部回环豁免，返回清除前的数量。</summary>
    int Clear();
}

/// <summary>
/// 为 UWP（AppContainer）应用解除本地回环限制，使其能通过本机代理端口联网。
/// 等价于社区的「UWP 解除限制」批处理：枚举所有已安装包的 PackageFamilyName，
/// 逐个执行 CheckNetIsolation.exe LoopbackExempt -a -n=&lt;包名&gt;。
/// </summary>
public sealed class LoopbackExemptionService : ILoopbackExemptionService
{
    private static readonly Regex NameLine = new(
        @"^\s*(?:名称|Name)\s*[:：]\s*(?<name>.+?)\s*$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    public bool IsElevated => MomomiHost.IsRunningAsAdmin();

    public int GetExemptedCount() => ListExempted().Count;

    public LoopbackApplyResult ExemptAll()
    {
        if (!IsElevated)
            return new LoopbackApplyResult(false, 0, 0, 0, "需要管理员权限");

        var families = ListInstalledPackageFamilies();
        if (families.Count == 0)
            return new LoopbackApplyResult(false, 0, 0, 0, "未找到已安装的 UWP 应用");

        var existing = new HashSet<string>(ListExempted(), StringComparer.OrdinalIgnoreCase);
        var added = 0;
        foreach (var family in families)
        {
            if (string.IsNullOrWhiteSpace(family)) continue;
            if (existing.Contains(family)) continue;
            if (Add(family)) added++;
        }

        var already = families.Count - added;
        return new LoopbackApplyResult(true, added, families.Count, already);
    }

    public int Clear()
    {
        if (!IsElevated) return -1;
        var before = ListExempted().Count;
        Run("CheckNetIsolation.exe", "LoopbackExempt -c");
        return before;
    }

    public IReadOnlyList<string> ListExempted()
    {
        var output = Run("CheckNetIsolation.exe", "LoopbackExempt -s");
        if (string.IsNullOrEmpty(output)) return Array.Empty<string>();

        var list = new List<string>();
        foreach (Match match in NameLine.Matches(output))
        {
            var name = match.Groups["name"].Value.Trim();
            if (name.Length > 0 && !name.Equals("AppContainer NOT FOUND", StringComparison.OrdinalIgnoreCase))
                list.Add(name);
        }
        return list;
    }

    public IReadOnlyList<string> ListInstalledPackageFamilies()
    {
        var output = Run(
            "powershell.exe",
            "-NoProfile -NonInteractive -Command \"(Get-AppxPackage -AllUsers).PackageFamilyName\"");
        if (string.IsNullOrEmpty(output)) return Array.Empty<string>();

        return output
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0 && s.Contains('_'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool Add(string family)
    {
        if (family.IndexOfAny(new[] { '"', '\r', '\n' }) >= 0) return false;
        return Run("CheckNetIsolation.exe", $"LoopbackExempt -a -n={family}") is not null;
    }

    private static string? Run(string fileName, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return null;
            var output = proc.StandardOutput.ReadToEnd();
            proc.StandardError.ReadToEnd();
            if (!proc.WaitForExit(60000))
            {
                try { proc.Kill(); } catch { }
                return null;
            }
            return proc.ExitCode == 0 ? output : null;
        }
        catch
        {
            return null;
        }
    }
}
