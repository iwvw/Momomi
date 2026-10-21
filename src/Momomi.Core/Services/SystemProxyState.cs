namespace Momomi.Core.Services;

/// <summary>
/// 系统代理的「用户意图」持久化与跨启动恢复。
///
/// 背景：系统代理是写在注册表里的全局状态，而内核（127.0.0.1:混合端口）只在
/// Momomi 运行期间存在。两者必须一起开、一起关，否则会断网。
///
/// 之前的行为有几个问题：
/// <list type="bullet">
/// <item>退出时无条件关掉代理，重启后不会恢复——「上次开着代理」这个预期不成立。</item>
/// <item>崩溃时注册表里proxyEnable 会残留为 1 且指向已死的端口，等于断网。</item>
/// <item>只有内核重启（同一进程内）会恢复代理，跨进程不管。</item>
/// </list>
///
/// 现在的约定：
/// <list type="bullet">
/// <item>用户每次开/关系统代理，都把意图写入 <c>ui.systemProxyDesired</c>。</item>
/// <item>退出时一定关掉「自己的」代理（避免指向已关闭端口），意图保留。</item>
/// <item>启动时：先清理崩溃遗留的残留代理，内核就绪后按意图恢复。</item>
/// </list>
/// </summary>
public static class SystemProxyState
{
    /// <summary>用户意图：是否希望 Momomi 接管系统代理。</summary>
    public const string DesiredKey = "ui.systemProxyDesired";

    /// <summary>记录用户意图。</summary>
    public static async Task SetDesiredAsync(ISettingsService settings, bool desired)
    {
        try
        {
            await settings.SetBoolAsync(DesiredKey, desired).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    /// <summary>读取用户意图；未设置过返回 null（表示沿用旧行为）。</summary>
    public static async Task<bool?> GetDesiredAsync(ISettingsService settings)
    {
        try
        {
            var raw = await settings.GetAsync(DesiredKey).ConfigureAwait(false);
            if (raw is null) return null;
            return raw == "true";
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 启动时清理崩溃遗留：注册表里代理是开的、指向本应用端口，但内核还没起来。
    /// 这种状态会让整机断网，必须先关掉；稍后由 <see cref="RestoreAsync"/> 按意图重开。
    /// </summary>
    public static bool ClearStaleProxy(ISystemProxyService proxy, int corePort)
    {
        try
        {
            return proxy.ClearOwnProxy(corePort);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 首次升级到本版本时没有意图记录。<b>必须在 <see cref="ClearStaleProxy"/> 之前调用</b>：
    /// 若此刻注册表里开着指向本应用的代理，就说明用户上一次是开着的，
    /// 据此补记意图，否则清掉残留后代理不会自动恢复（用户会以为"升级把代理弄没了"）。
    /// </summary>
    public static async Task<bool> BootstrapDesiredIfMissingAsync(
        ISettingsService settings, ISystemProxyService proxy, int corePort)
    {
        try
        {
            if (await GetDesiredAsync(settings).ConfigureAwait(false) is not null) return false;

            // 只看「有没有指向自己的代理」：别人的代理与我们的意图无关，不推断。
            var desired = proxy.IsOwnProxy(corePort);
            await settings.SetBoolAsync(DesiredKey, desired).ConfigureAwait(false);
            return desired;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 按用户意图恢复系统代理。仅在调用方确认内核已就绪后调用。
    /// 返回是否实际做了变更。
    /// </summary>
    /// <param name="applyPac">PAC 模式下由调用方启动本地服务器并写入 AutoConfigURL；返回是否成功。</param>
    public static async Task<bool> RestoreAsync(
        ISettingsService settings,
        ISystemProxyService proxy,
        int corePort,
        Func<bool>? applyPac = null)
    {
        var desired = await GetDesiredAsync(settings).ConfigureAwait(false);

        // 没有记录过意图：不动用户当前的设置（可能是别的软件开的代理）。
        if (desired is null) return false;

        try
        {
            if (!desired.Value)
            {
                // 用户希望关闭：只在确实是自己开的代理时才关，别误关别人的。
                return proxy.ClearOwnProxy(corePort);
            }

            // 用户希望开启：已经是自己的代理就不重复写注册表（避免无谓的刷新与图标闪烁）。
            if (proxy.IsOwnProxy(corePort)) return false;

            var mode = await settings.GetAsync("ui.systemProxyMode").ConfigureAwait(false) ?? "manual";
            if (mode == "pac" && applyPac is not null)
                return applyPac();

            return proxy.Enable($"127.0.0.1:{corePort}", Bypass);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>与界面里一致的绕过列表。</summary>
    public const string Bypass = "localhost;127.*;10.*;172.16.*;192.168.*";
}
