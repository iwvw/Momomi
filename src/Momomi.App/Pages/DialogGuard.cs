using Microsoft.UI.Xaml.Controls;

namespace Momomi.App.Pages;

/// <summary>
/// ContentDialog 的单例守卫。
///
/// WinUI 同时只允许存在一个 ContentDialog：并发 ShowAsync 会抛
/// "Only a single ContentDialog can be open at any time"。而调用点大多是
/// 按钮事件 / async void 处理器，异常逃出去就是进程级崩溃。
/// 典型触发：快速双击「删除覆写」，或在设置页录快捷键时弹冲突确认、
/// 同时别的页面也弹了对话框。
///
/// 这里不做排队（用户视角排队反而更奇怪），只保证第二个对话框直接返回
/// 非 Primary 结果，调用方什么也不做即可。
/// </summary>
internal static class DialogGuard
{
    private static bool _open;

    /// <summary>显示对话框；已有对话框打开时立即返回 <see cref="ContentDialogResult.None"/>。</summary>
    public static async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        if (_open) return ContentDialogResult.None;

        _open = true;
        try
        {
            return await dialog.ShowAsync();
        }
        finally
        {
            _open = false;
        }
    }
}
