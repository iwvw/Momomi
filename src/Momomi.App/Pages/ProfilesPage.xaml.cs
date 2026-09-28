using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Momomi.App.ViewModels;

namespace Momomi.App.Pages;

public sealed partial class ProfilesPage : Page
{
    public ProfilesViewModel ViewModel { get; }

    public ProfilesPage()
    {
        ViewModel = new ProfilesViewModel(
            global::Momomi.App.AppHost.Host,
            DispatcherQueue);
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.LoadAsync();
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not ProfileRowViewModel row) return;
        EditorTabs.SelectedItem = EditorTabs.Items.Count > 0 ? EditorTabs.Items[0] : null;
        _ = ViewModel.OpenEditorCommand.ExecuteAsync(row);
    }

    /// <summary>点击卡片激活对应订阅；已激活或点到按钮时不重复触发。</summary>
    private void Card_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not ProfileRowViewModel row) return;
        if (row.IsActive) return;
        // 点在卡片内的按钮/输入控件上时不激活。
        if (IsWithinInteractive(e.OriginalSource as DependencyObject)) return;
        _ = ViewModel.ActivateCommand.ExecuteAsync(row);
    }

    private static bool IsWithinInteractive(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is Microsoft.UI.Xaml.Controls.Primitives.ButtonBase
                or Microsoft.UI.Xaml.Controls.TextBox
                or Microsoft.UI.Xaml.Controls.ComboBox
                or Microsoft.UI.Xaml.Controls.ToggleSwitch)
                return true;
            element = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(element);
        }
        return false;
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not ProfileRowViewModel row) return;
        _ = ViewModel.RefreshCommand.ExecuteAsync(row);
    }

    private void ToggleTime_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not ProfileRowViewModel row) return;
        row.ToggleTime();
    }

    private void Activate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not ProfileRowViewModel row) return;
        _ = ViewModel.ActivateCommand.ExecuteAsync(row);
    }

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not ProfileRowViewModel row) return;
        _ = ViewModel.RenameCommand.ExecuteAsync(row);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not ProfileRowViewModel row) return;
        _ = ViewModel.DeleteCommand.ExecuteAsync(row);
    }

    private void EditInfo_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not ProfileRowViewModel row) return;
        _ = ShowInfoEditorAsync(row);
    }

    private void EditorTabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        ViewModel.EditorTabIndex = sender.Items.IndexOf(sender.SelectedItem);
    }

    private void SaveEditor_Click(object sender, RoutedEventArgs e)
    {
        // x:Bind 的 TextBox.Text 默认失焦才回写，点按钮时不保证已同步，显式再取一次。
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

    /// <summary>编辑订阅信息：名称、订阅地址等。</summary>
    private async Task ShowInfoEditorAsync(ProfileRowViewModel row)
    {
        var host = global::Momomi.App.AppHost.Host;
        var item = (await host.Profiles.ListAsync()).FirstOrDefault(i => i.Id == row.Id);
        if (item is null) return;

        var nameBox = new TextBox { Header = "名称", Text = row.Name };
        var urlBox = new TextBox { Header = "订阅地址", Text = item.Source ?? "" };
        var panel = new StackPanel { Spacing = 12, Width = 420 };
        panel.Children.Add(nameBox);
        panel.Children.Add(urlBox);

        var dialog = new ContentDialog
        {
            Title = "编辑订阅信息",
            Content = panel,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var name = nameBox.Text?.Trim();
        var url = urlBox.Text?.Trim();

        if (!string.IsNullOrEmpty(name) && name != row.Name)
            await host.Profiles.RenameAsync(row.Id, name);

        // 订阅地址变更：重新按新地址导入并覆盖原条目。
        if (!string.IsNullOrEmpty(url) && !string.Equals(url, item.Source, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var imported = await host.Profiles.ImportFromUrlAsync(url, name);
                if (row.IsActive)
                {
                    await host.Profiles.SetActiveAsync(imported.Id);
                    await host.ApplyActiveProfileAsync();
                }
                await host.Profiles.DeleteAsync(row.Id);
                ViewModel.StatusText = "订阅地址已更新";
            }
            catch (Exception ex)
            {
                ViewModel.StatusText = $"更新订阅地址失败：{ex.Message}";
                return;
            }
        }
        else
        {
            ViewModel.StatusText = "订阅信息已保存";
        }

        await ViewModel.LoadAsync();
    }
}