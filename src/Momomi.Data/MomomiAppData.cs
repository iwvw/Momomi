namespace Momomi.Data;

/// <summary>
/// 解析用户数据（数据库、订阅、内核、地理数据）的根目录。
/// 优先放在程序目录下的 data\，便于便携使用与整体迁移；
/// 若程序目录不可写（如装到只读位置），自动回退到 %LOCALAPPDATA%\Momomi。
/// 首次使用程序目录且其中尚无数据时，从旧的 %LOCALAPPDATA%\Momomi 迁移一次。
/// </summary>
public static class MomomiAppData
{
    private const string OverrideVariable = "MOMOMI_DATA_DIR";
    private const string DataFolderName = "data";
    private const string AppFolderName = "Momomi";

    public static string ResolveDirectory()
    {
        var overridden = Environment.GetEnvironmentVariable(OverrideVariable);
        if (!string.IsNullOrWhiteSpace(overridden))
            return Path.GetFullPath(overridden);

        var legacy = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolderName);

        var portable = Path.Combine(AppContext.BaseDirectory, DataFolderName);
        if (TryPrepareWritable(portable, out var error) is { } dir)
        {
            MigrateLegacyOnce(dir, legacy);
            return dir;
        }

        _ = error;
        Directory.CreateDirectory(legacy);
        return legacy;
    }

    /// <summary>尝试创建并写入探测文件；成功返回目录，失败返回 null。</summary>
    private static string? TryPrepareWritable(string directory, out string? error)
    {
        error = null;
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, ".write-test");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return directory;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>旧数据迁移：目标目录尚无数据库且旧目录存在时，复制旧数据过来（只迁一次）。</summary>
    private static void MigrateLegacyOnce(string target, string legacy)
    {
        try
        {
            if (File.Exists(Path.Combine(target, "momomi.db"))) return;
            if (!Directory.Exists(legacy)) return;
            if (!File.Exists(Path.Combine(legacy, "momomi.db"))) return;
            if (string.Equals(Path.GetFullPath(target), Path.GetFullPath(legacy), StringComparison.OrdinalIgnoreCase))
                return;

            CopyDirectory(legacy, target);
        }
        catch
        {
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
        {
            var dest = Path.Combine(destination, Path.GetFileName(file));
            try
            {
                if (!File.Exists(dest)) File.Copy(file, dest, overwrite: false);
            }
            catch
            {
            }
        }
        foreach (var dir in Directory.GetDirectories(source))
        {
            CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
        }
    }
}
