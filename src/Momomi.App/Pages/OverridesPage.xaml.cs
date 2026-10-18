using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Momomi.App.ViewModels;

namespace Momomi.App.Pages;

public sealed partial class OverridesPage : Page
{
    public OverridesViewModel ViewModel { get; }

    public OverridesPage()
    {
        ViewModel = new OverridesViewModel(
            global::Momomi.App.AppHost.Host,
            DispatcherQueue);
        InitializeComponent();
        BuildTemplateMenu();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.LoadAsync();
    }

    /// <summary>模板菜单由内置模板动态生成，新增模板不用改 XAML。</summary>
    private void BuildTemplateMenu()
    {
        foreach (var template in ViewModel.Templates)
        {
            var item = new MenuFlyoutItem
            {
                Text = template.Name,
                Icon = new FontIcon { Glyph = "\uE70F" },
                Tag = template.Key,
            };
            ToolTipService.SetToolTip(item, template.Description);
            item.Click += Template_Click;
            TemplateFlyout.Items.Add(item);
        }
    }

    private void New_Click(object sender, RoutedEventArgs e)
        => TemplateFlyout.ShowAt((FrameworkElement)sender);

    private void Template_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem item || item.Tag is not string key) return;
        _ = ViewModel.CreateCommand.ExecuteAsync(key);
    }

    private void ImportUrl_Click(object sender, RoutedEventArgs e) => ImportFromBox();

    private void ImportUrlBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        e.Handled = true;
        ImportFromBox();
    }

    private void ImportFromBox()
    {
        var url = ImportUrlBox.Text?.Trim();
        if (string.IsNullOrEmpty(url)) return;
        ImportUrlBox.Text = "";
        _ = ViewModel.ImportUrlCommand.ExecuteAsync(url);
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not OverrideRowViewModel row) return;
        _ = OpenEditorAsync(row);
    }

    /// <summary>
    /// 打开编辑器：先让 ViewModel 读内容，再把文本交给带高亮的编辑器。
    /// CodeEditor 不是绑定控件，文本由这里显式灌入。
    ///
    /// 注意 IsEditorOpen 变化后，编辑器才从 Collapsed 切到可见并开始布局；
    /// 这里用 DispatcherQueue 延后一帧写入，确保控件已完成加载。
    /// CodeEditor 内部也会缓存未就绪时收到的文本，双保险。
    /// </summary>
    private async Task OpenEditorAsync(OverrideRowViewModel row)
    {
        await ViewModel.OpenEditorCommand.ExecuteAsync(row);
        if (!ViewModel.IsEditorOpen) return;

        var text = ViewModel.EditorText;
        DispatcherQueue.TryEnqueue(() =>
        {
            RawEditor.Text = text;
            RawEditor.Focus(FocusState.Programmatic);
        });
    }

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not OverrideRowViewModel row) return;
        _ = ViewModel.RenameCommand.ExecuteAsync(row);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not OverrideRowViewModel row) return;
        _ = ViewModel.DeleteCommand.ExecuteAsync(row);
    }

    private void Global_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch toggle) return;
        if (toggle.DataContext is not OverrideRowViewModel row) return;
        if (toggle.IsOn == row.IsGlobal) return;
        _ = ViewModel.ToggleGlobalAsync(row, toggle.IsOn);
    }

    private void Card_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not OverrideRowViewModel row) return;
        _ = OpenEditorAsync(row);
    }

    private async void EditorTabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        ViewModel.PreviewTabSelected = EditorTabs.SelectedItem != EditorTabs.Items[0];
        if (!ViewModel.PreviewTabSelected) return;

        // 预览用编辑器当前内容（含未保存改动），方便写完先看效果。
        ViewModel.EditorText = RawEditor.Text;
        await ViewModel.PreviewAsync();
    }

    private void SaveEditor_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.EditorText = RawEditor.Text;
        _ = ViewModel.SaveEditorCommand.ExecuteAsync(null);
    }

    private bool _editorMaximized;

    private void ToggleEditorMaximize_Click(object sender, RoutedEventArgs e)
    {
        _editorMaximized = !_editorMaximized;
        if (EditorPanel is not null)
            EditorPanel.Margin = _editorMaximized ? new Thickness(0) : new Thickness(40);
        EditorMaximizeIcon.Glyph = _editorMaximized ? "\uE73F" : "\uE740";
    }
}
