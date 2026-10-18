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

    /// <summary>编辑订阅信息：名称、订阅地址，以及挂载哪些覆写。</summary>
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

        // 覆写挂载：全局启用的覆写对所有订阅生效，这里只列未全局的那些供选择。
        var mounted = (await host.Overrides.GetProfileOverridesAsync(row.Id)).ToHashSet();
        var candidates = (await host.Overrides.ListAsync())
            .Where(o => !o.IsGlobal)
            .ToList();

        var checkBoxes = new List<(long Id, CheckBox Box)>();
        if (candidates.Count > 0)
        {
            var list = new StackPanel { Spacing = 4 };
            foreach (var o in candidates)
            {
                var box = new CheckBox
                {
                    Content = o.Name,
                    IsChecked = mounted.Contains(o.Id),
                };
                checkBoxes.Add((o.Id, box));
                list.Children.Add(box);
            }

            var scroll = new ScrollViewer
            {
                MaxHeight = 160,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = list,
            };
            panel.Children.Add(new TextBlock
            {
                Text = "覆写",
                Margin = new Thickness(0, 4, 0, 0),
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            });
            panel.Children.Add(scroll);
            panel.Children.Add(new TextBlock
            {
                Text = "按勾选顺序叠加在订阅之上；全局启用的覆写在覆写页管理。",
                TextWrapping = TextWrapping.Wrap,
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            });
        }

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

        // 先记下原有的挂载顺序，保存时保持相对顺序不变。
        var selectedIds = checkBoxes
            .Where(c => c.Box.IsChecked == true)
            .Select(c => c.Id)
            .OrderBy(id => mounted.Contains(id) ? mounted.ToList().IndexOf(id) : int.MaxValue)
            .ToList();

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
                // 新条目继承旧的覆写挂载，否则换地址后覆写会静默丢失。
                await host.Overrides.SetProfileOverridesAsync(imported.Id, selectedIds);
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
            await host.Overrides.SetProfileOverridesAsync(row.Id, selectedIds);
            if (row.IsActive)
                await host.ApplyActiveProfileAsync();
            ViewModel.StatusText = "订阅信息已保存";
        }

        await ViewModel.LoadAsync();
    }
}