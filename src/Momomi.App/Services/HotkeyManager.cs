using Momomi.Core.Services;
using Momomi.App.ViewModels;

namespace Momomi.App.Services;

/// <summary>快捷键绑定尝试的结果状态。</summary>
public enum HotkeyApplyStatus
{
    /// <summary>绑定成功。</summary>
    Ok,
    /// <summary>已清除绑定。</summary>
    Cleared,
    /// <summary>格式无效（缺修饰键/主键不可识别）。</summary>
    Invalid,
    /// <summary>与应用内其它动作冲突（Conflict 为冲突动作）。</summary>
    InternalConflict,
    /// <summary>被其它程序占用，注册失败。</summary>
    Occupied,
}

/// <summary>快捷键绑定尝试的结果。</summary>
public sealed record HotkeyApplyResult(HotkeyApplyStatus Status, HotkeyAction? Conflict);

/// <summary>
/// 全局快捷键管理：把各动作绑定到主窗口操作，并将配置持久化到设置表。
/// 存储键：hotkey.<动作名>，值为 "Ctrl+Alt+P" 形式，空表示未绑定。
/// </summary>
public sealed partial class HotkeyManager : IDisposable
{
    private readonly HotkeyService _service = new();
    private readonly MomomiHost _host;
    private readonly MainWindow _window;

    public HotkeyManager(MomomiHost host, MainWindow window)
    {
        _host = host;
        _window = window;
    }

    public static readonly HotkeyAction[] AllActions =
    {
        HotkeyAction.ShowWindow,
        HotkeyAction.ToggleSystemProxy,
        HotkeyAction.ToggleTun,
        HotkeyAction.ToggleMiniPanel,
        HotkeyAction.ModeRule,
        HotkeyAction.ModeGlobal,
        HotkeyAction.ModeDirect,
    };

    public static string SettingKey(HotkeyAction action) => $"hotkey.{action}";

    /// <summary>动作的中文显示名（用于冲突提示）。</summary>
    public static string DisplayName(HotkeyAction action) => action switch
    {
        HotkeyAction.ShowWindow => "显示主界面",
        HotkeyAction.ToggleSystemProxy => "切换系统代理",
        HotkeyAction.ToggleTun => "切换 TUN 模式",
        HotkeyAction.ToggleMiniPanel => "切换迷你面板",
        HotkeyAction.ModeRule => "规则模式",
        HotkeyAction.ModeGlobal => "全局模式",
        HotkeyAction.ModeDirect => "直连模式",
        _ => action.ToString(),
    };

    /// <summary>从设置加载并注册全部快捷键。</summary>
    public async Task LoadAsync()
    {
        foreach (var action in AllActions)
        {
            var text = await _host.Settings.GetAsync(SettingKey(action)).ConfigureAwait(false);
            ApplyBinding(action, text);
        }
    }

    /// <summary>判断某动作当前绑定的显示文本（无绑定返回空）。</summary>
    public async Task<string> GetBindingAsync(HotkeyAction action)
        => await _host.Settings.GetAsync(SettingKey(action)).ConfigureAwait(false) ?? "";

    /// <summary>查找占用同一组合键的其它动作；无冲突返回 null。normalized 为格式化后的绑定文本。</summary>
    public async Task<HotkeyAction?> FindConflictActionAsync(HotkeyAction action, string normalized)
    {
        foreach (var other in AllActions.Where(a => a != action))
        {
            var text = await GetBindingAsync(other).ConfigureAwait(false);
            if (string.Equals(text, normalized, StringComparison.OrdinalIgnoreCase))
                return other;
        }
        return null;
    }

    /// <summary>更新某动作的绑定并持久化；text 为空则解除绑定。返回是否成功。</summary>
    public async Task<bool> SetAsync(HotkeyAction action, string? text)
    {
        text = text?.Trim() ?? "";
        if (text.Length == 0)
        {
            _service.Unregister(action);
            await _host.Settings.SetAsync(SettingKey(action), "").ConfigureAwait(false);
            return true;
        }

        if (!HotkeyParser.TryParse(text, out var mods, out var vk)) return false;

        var normalized = HotkeyParser.Format(mods, vk);
        var ok = _service.Register(action, mods, vk, CreateCallback(action));
        if (!ok) return false;

        await _host.Settings.SetAsync(SettingKey(action), normalized).ConfigureAwait(false);
        return true;
    }

    /// <summary>解除某动作的绑定并持久化。</summary>
    public async Task UnbindAsync(HotkeyAction action)
    {
        _service.Unregister(action);
        await _host.Settings.SetAsync(SettingKey(action), "").ConfigureAwait(false);
    }

    private void ApplyBinding(HotkeyAction action, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (!HotkeyParser.TryParse(text, out var mods, out var vk)) return;
        _service.Register(action, mods, vk, CreateCallback(action));
    }

    private Action CreateCallback(HotkeyAction action) => action switch
    {
        HotkeyAction.ShowWindow => () => _window.DispatcherQueue.TryEnqueue(_window.ShowAndActivate),
        HotkeyAction.ToggleSystemProxy => () => _window.DispatcherQueue.TryEnqueue(_window.HotkeyToggleSystemProxy),
        HotkeyAction.ToggleTun => () => _window.DispatcherQueue.TryEnqueue(_window.HotkeyToggleTun),
        HotkeyAction.ToggleMiniPanel => () => _window.DispatcherQueue.TryEnqueue(_window.ToggleMiniWindow),
        HotkeyAction.ModeRule => () => _window.DispatcherQueue.TryEnqueue(() => _ = _window.HotkeySelectModeAsync(0)),
        HotkeyAction.ModeGlobal => () => _window.DispatcherQueue.TryEnqueue(() => _ = _window.HotkeySelectModeAsync(1)),
        HotkeyAction.ModeDirect => () => _window.DispatcherQueue.TryEnqueue(() => _ = _window.HotkeySelectModeAsync(2)),
        _ => () => { },
    };

    public void Dispose() => _service.Dispose();
}
