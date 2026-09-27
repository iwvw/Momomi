using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Momomi.Core.Services;

namespace Momomi.App.ViewModels;

public sealed partial class ProfileRowViewModel : ObservableObject
{
    public long Id { get; }
    public string Name { get; }
    public string KindText { get; }
    public string? Source { get; }
    public string UpdatedText { get; }
    public string? SubscriptionUserInfo { get; }

    [ObservableProperty]
    public partial bool IsActive { get; set; }

    [ObservableProperty]
    public partial bool IsRefreshing { get; set; }

    public string SourceText => string.IsNullOrEmpty(Source) ? "本地" : Source!;

    public string TrafficText => FormatSubscriptionInfo(SubscriptionUserInfo);

    public bool HasTraffic => !string.IsNullOrEmpty(TrafficText);

    public double UsagePercent { get; }

    public bool HasUsageBar { get; }

    public string ExpireText { get; }

    public string UsageText { get; }

    public ProfileRowViewModel(ProfileItem item)
    {
        Id = item.Id;
        Name = item.Name;
        KindText = item.KindText;
        Source = item.Source;
        UpdatedText = item.UpdatedText;
        SubscriptionUserInfo = item.SubscriptionUserInfo;
        IsActive = item.IsActive;

        var info = SubscriptionUsage.Parse(item.SubscriptionUserInfo);
        if (info is not null && info.Total > 0)
        {
            UsagePercent = Math.Clamp(info.Used * 100.0 / info.Total, 0, 100);
            HasUsageBar = true;
            UsageText = $"{Format.Bytes(info.Used)} / {Format.Bytes(info.Total)}";
        }
        else if (info is not null)
        {
            UsageText = $"↑ {Format.Bytes(info.Upload)}  ↓ {Format.Bytes(info.Download)}";
        }
        else
        {
            UsageText = "";
        }

        ExpireText = info is null || info.Expire <= 0
            ? (info is null ? "" : "长期有效")
            : $"到期 {DateTimeOffset.FromUnixTimeSeconds(info.Expire).LocalDateTime:yyyy-MM-dd}";
    }

    private static string FormatSubscriptionInfo(string? raw)
    {
        var info = SubscriptionUsage.Parse(raw);
        if (info is null) return "";
        return info.Total > 0
            ? $"{Format.Bytes(info.Used)} / {Format.Bytes(info.Total)}"
            : $"↑ {Format.Bytes(info.Upload)}  ↓ {Format.Bytes(info.Download)}";
    }
}

public sealed partial class ProfilesViewModel : ObservableObject
{
    private readonly MomomiHost _host;
    private readonly DispatcherQueue _dispatcher;

    public ObservableCollection<ProfileRowViewModel> Items { get; } = new();

    [ObservableProperty]
    public partial string StatusText { get; set; } = "";

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial bool HasItems { get; set; }

    [ObservableProperty]
    public partial string ImportUrl { get; set; } = "";

    [ObservableProperty]
    public partial string ImportName { get; set; } = "";

    /// <summary>订阅地址变化时清空名称，让导入时用订阅响应头里的名称作为默认。</summary>
    partial void OnImportUrlChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            ImportName = "";
        }
    }

    [ObservableProperty]
    public partial bool AutoUpdate { get; set; } = true;

    public ProfilesViewModel(MomomiHost host, DispatcherQueue dispatcher)
    {
        _host = host;
        _dispatcher = dispatcher;
    }

    public async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            AutoUpdate = await _host.Settings.GetBoolAsync("profile.autoUpdate", true);
            var list = await _host.Profiles.ListAsync();
            _dispatcher.TryEnqueue(() =>
            {
                Items.Clear();
                foreach (var item in list) Items.Add(new ProfileRowViewModel(item));
                HasItems = Items.Count > 0;
                StatusText = Items.Count == 0 ? "尚无配置文件" : $"共 {Items.Count} 个配置";
            });
        }
        catch (Exception ex)
        {
            StatusText = $"加载失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ImportUrlAsync()
    {
        var url = ImportUrl?.Trim();
        if (string.IsNullOrEmpty(url))
        {
            StatusText = "请输入订阅链接";
            return;
        }

        IsBusy = true;
        StatusText = "正在下载订阅…";
        try
        {
            var item = await _host.Profiles.ImportFromUrlAsync(url, string.IsNullOrWhiteSpace(ImportName) ? null : ImportName);
            StatusText = $"已导入 {item.Name}";
            ImportUrl = "";
            ImportName = "";
            await LoadAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"导入失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ImportFileAsync()
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(global::Momomi.App.App.Main);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            picker.FileTypeFilter.Add(".yaml");
            picker.FileTypeFilter.Add(".yml");
            picker.FileTypeFilter.Add(".txt");

            var file = await picker.PickSingleFileAsync();
            if (file is null) return;

            IsBusy = true;
            StatusText = "正在导入文件…";
            var item = await _host.Profiles.ImportFromFileAsync(file.Path);
            StatusText = $"已导入 {item.Name}";
            await LoadAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"导入失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ActivateAsync(ProfileRowViewModel? row)
    {
        if (row is null) return;
        IsBusy = true;
        try
        {
            await _host.Profiles.SetActiveAsync(row.Id);
            StatusText = $"正在应用 {row.Name}…";
            var applied = await _host.ApplyActiveProfileAsync();
            StatusText = applied ? $"已切换到 {row.Name}" : $"已切换，但配置应用失败";
            await LoadAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"切换失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RefreshAsync(ProfileRowViewModel? row)
    {
        if (row is null) return;
        row.IsRefreshing = true;
        IsBusy = true;
        StatusText = $"正在刷新 {row.Name}…";
        try
        {
            var ok = await _host.Profiles.RefreshAsync(row.Id);
            if (ok)
            {
                if (row.IsActive) await _host.ApplyActiveProfileAsync();
                StatusText = $"已更新 {row.Name}";
            }
            else
            {
                StatusText = $"刷新失败：该配置无订阅来源";
            }
            // LoadAsync 会重建列表，刷新状态自然重置。
            await LoadAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"刷新失败：{ex.Message}";
        }
        finally
        {
            row.IsRefreshing = false;
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task DeleteAsync(ProfileRowViewModel? row)
    {
        if (row is null) return;
        try
        {
            await _host.Profiles.DeleteAsync(row.Id);
            StatusText = $"已删除 {row.Name}";
            await LoadAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"删除失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private async Task RefreshAllAsync()
    {
        IsBusy = true;
        var count = 0;
        try
        {
            foreach (var item in Items.Where(i => i.Source is not null && i.KindText == "订阅链接").ToList())
            {
                if (await _host.Profiles.RefreshAsync(item.Id)) count++;
            }
            await _host.ApplyActiveProfileAsync();
            StatusText = $"已刷新 {count} 个订阅";
            await LoadAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"刷新失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ToggleAutoUpdateAsync()
    {
        await _host.Settings.SetBoolAsync("profile.autoUpdate", AutoUpdate);
        StatusText = AutoUpdate ? "已开启订阅自动更新（每小时）" : "已关闭订阅自动更新";
    }

    [RelayCommand]
    private async Task RenameAsync(ProfileRowViewModel? row)
    {
        if (row is null) return;
        var dialog = new Microsoft.UI.Xaml.Controls.ContentDialog
        {
            Title = "重命名配置",
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = Microsoft.UI.Xaml.Controls.ContentDialogButton.Primary,
            XamlRoot = global::Momomi.App.App.Main?.Content?.XamlRoot,
        };
        var box = new Microsoft.UI.Xaml.Controls.TextBox { Text = row.Name };
        dialog.Content = box;
        var result = await dialog.ShowAsync();
        if (result != Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary) return;

        var name = box.Text?.Trim();
        if (string.IsNullOrEmpty(name)) return;

        await _host.Profiles.RenameAsync(row.Id, name);
        StatusText = $"已重命名为 {name}";
        await LoadAsync();
    }
}
