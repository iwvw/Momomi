using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Momomi.Core.Services;

namespace Momomi.App.ViewModels;

/// <summary>覆写列表项（列表卡片）。</summary>
public sealed partial class OverrideRowViewModel : ObservableObject
{
    public long Id { get; }
    public string Name { get; }
    public string UpdatedText { get; }

    [ObservableProperty]
    public partial bool IsGlobal { get; set; }

    public string SourceText => Kind == "url" ? "远程导入" : "本地文件";

    public string Kind { get; }

    public OverrideRowViewModel(OverrideItem item)
    {
        Id = item.Id;
        Name = item.Name;
        Kind = item.Kind;
        IsGlobal = item.IsGlobal;
        UpdatedText = item.UpdatedText;
    }
}

public sealed partial class OverridesViewModel : ObservableObject
{
    private readonly MomomiHost _host;
    private readonly DispatcherQueue _dispatcher;

    public ObservableCollection<OverrideRowViewModel> Items { get; } = new();

    [ObservableProperty]
    public partial string StatusText { get; set; } = "";

    [ObservableProperty]
    public partial bool HasItems { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    // ---- 编辑器 ----

    [ObservableProperty]
    public partial bool IsEditorOpen { get; set; }

    [ObservableProperty]
    public partial string EditorTitle { get; set; } = "";

    [ObservableProperty]
    public partial string EditorText { get; set; } = "";

    [ObservableProperty]
    public partial string EditorError { get; set; } = "";

    [ObservableProperty]
    public partial bool HasEditorError { get; set; }

    /// <summary>预览标签是否选中。</summary>
    [ObservableProperty]
    public partial bool PreviewTabSelected { get; set; }

    /// <summary>合并预览文本，切到预览标签时生成。</summary>
    [ObservableProperty]
    public partial string PreviewText { get; set; } = "";

    /// <summary>逐行差异，供预览列表高亮。</summary>
    public ObservableCollection<DiffLineViewModel> DiffLines { get; } = new();

    /// <summary>差异摘要，例如「+3 −1」。</summary>
    [ObservableProperty]
    public partial string DiffSummary { get; set; } = "";

    [ObservableProperty]
    public partial bool HasDiff { get; set; }

    /// <summary>当前生效的其他覆写名称，用于提示合并基线。</summary>
    [ObservableProperty]
    public partial string OtherOverridesHint { get; set; } = "";

    /// <summary>true = 只看改动及上下文；false = 全文。</summary>
    [ObservableProperty]
    public partial bool ChangesOnly { get; set; }

    partial void OnChangesOnlyChanged(bool value) => RebuildDiffView();

    private IReadOnlyList<DiffLine> _diff = Array.Empty<DiffLine>();

    [ObservableProperty]
    public partial bool IsPreviewBusy { get; set; }

    public bool IsRawTab => !PreviewTabSelected;

    public bool IsPreviewTab => PreviewTabSelected;

    partial void OnPreviewTabSelectedChanged(bool value)
    {
        OnPropertyChanged(nameof(IsRawTab));
        OnPropertyChanged(nameof(IsPreviewTab));
    }

    private OverrideRowViewModel? _editing;

    /// <summary>新建覆写时可选的模板。</summary>
    public IReadOnlyList<OverrideTemplate> Templates => _host.Overrides.Templates;

    public OverridesViewModel(MomomiHost host, DispatcherQueue dispatcher)
    {
        _host = host;
        _dispatcher = dispatcher;
    }

    public async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            var list = await _host.Overrides.ListAsync();
            _dispatcher.TryEnqueue(() =>
            {
                Items.Clear();
                foreach (var item in list) Items.Add(new OverrideRowViewModel(item));
                HasItems = Items.Count > 0;
                StatusText = Items.Count == 0
                    ? "还没有覆写。新建一个，把规则写在订阅之上。"
                    : $"共 {Items.Count} 个覆写，全局启用 {Items.Count(i => i.IsGlobal)} 个";
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
    private async Task CreateAsync(string? templateKey)
    {
        var template = Templates.FirstOrDefault(t => t.Key == templateKey);
        var name = template?.Name ?? "新覆写";
        var content = template?.Content;
        try
        {
            var item = await _host.Overrides.CreateAsync(name, content);
            StatusText = $"已新建 {item.Name}";
            await LoadAsync();
            await OpenEditorAsync(new OverrideRowViewModel(item));
        }
        catch (Exception ex)
        {
            StatusText = $"新建失败：{ex.Message}";
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

            var file = await picker.PickSingleFileAsync();
            if (file is null) return;

            var item = await _host.Overrides.ImportFromFileAsync(file.Path);
            StatusText = $"已导入 {item.Name}";
            await LoadAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"导入失败：{ex.Message}";
        }
    }

    /// <summary>从 URL 导入（社区覆写仓库直接可用）。</summary>
    [RelayCommand]
    private async Task ImportUrlAsync(string? url)
    {
        var trimmed = url?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed)) return;

        IsBusy = true;
        StatusText = "正在下载覆写…";
        try
        {
            var item = await _host.Overrides.ImportFromUrlAsync(trimmed);
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
    private async Task OpenEditorAsync(OverrideRowViewModel? row)
    {
        if (row is null) return;
        var content = await _host.Overrides.ReadContentAsync(row.Id);
        if (content is null)
        {
            StatusText = "无法读取覆写内容";
            return;
        }

        _editing = row;
        EditorTitle = $"编辑覆写：{row.Name}";
        EditorText = ToDisplayText(content);
        PreviewText = "";
        PreviewTabSelected = false;
        SetEditorError(null);
        IsEditorOpen = true;
    }

    [RelayCommand]
    private void CloseEditor()
    {
        IsEditorOpen = false;
        _editing = null;
        EditorText = "";
        PreviewText = "";
        SetEditorError(null);
    }

    [RelayCommand]
    private async Task SaveEditorAsync()
    {
        if (_editing is null) return;

        var content = ToStorageText(EditorText);
        var error = YamlOverrideMerger.Validate(content);
        if (error is not null)
        {
            SetEditorError($"语法错误：{error}");
            return;
        }

        var ok = await _host.Overrides.SaveContentAsync(_editing.Id, content);
        if (!ok)
        {
            SetEditorError("保存失败，请检查数据目录是否可写");
            return;
        }

        SetEditorError(null);
        StatusText = "已保存";

        // 若当前订阅正在使用这条覆写，立刻重新生成 runtime.yaml 并热重载内核。
        try
        {
            if (await IsInUseAsync(_editing.Id))
            {
                var reloaded = await _host.ApplyActiveProfileAsync();
                StatusText = reloaded ? "已保存并热重载内核" : "已保存，但内核重载失败（可重启内核）";
            }
        }
        catch (Exception ex)
        {
            StatusText = $"已保存，但应用失败：{ex.Message}";
        }

        await LoadAsync();
    }

    /// <summary>
    /// 预览：把当前编辑器内容合并进运行时配置，同时算出"只由这条覆写带来的改动"。
    ///
    /// 比较基线特意取"去掉这条覆写后的配置"，而不是原始订阅：
    /// 否则其他已生效的覆写会被误算成当前这条的改动。
    /// </summary>
    public async Task PreviewAsync()
    {
        IsPreviewBusy = true;
        try
        {
            var options = await _host.BuildRuntimeOptionsAsync();
            var active = await _host.Profiles.GetActiveAsync();

            // GenerateRuntimeYamlAsync 已经把配置规范化（emoji 不转义），
            // 拿它当基线即可与合并结果直接对比，不会出现纯格式的假改动。
            var baseYaml = await _host.GenerateRuntimeYamlAsync(options);

            // 除当前这条之外的其它生效覆写（全局 + 挂载）。
            var others = (await _host.Overrides.ResolveActiveDocumentsAsync(active?.Id))
                .Where(d => d.Name != _editing?.Name)
                .ToList();

            var withOthers = others.Count == 0
                ? baseYaml
                : YamlOverrideMerger.Apply(baseYaml, others);

            OtherOverridesHint = others.Count == 0
                ? ""
                : $"已叠加其他 {others.Count} 条生效覆写：{string.Join("、", others.Select(o => o.Name))}";

            var content = ToStorageText(EditorText);
            if (string.IsNullOrWhiteSpace(content))
            {
                _diff = Array.Empty<DiffLine>();
                RebuildDiffView();
                DiffSummary = "";
                HasDiff = false;
                return;
            }

            string merged;
            try
            {
                merged = YamlOverrideMerger.Apply(withOthers, new[]
                {
                    new OverrideDocument(_editing?.Name ?? "当前覆写", content),
                });
            }
            catch (Exception ex)
            {
                _diff = Array.Empty<DiffLine>();
                DiffLines.Clear();
                DiffSummary = "";
                HasDiff = false;
                SetEditorError($"无法生成预览：{ex.Message}");
                return;
            }

            _diff = LineDiff.Compute(withOthers, merged);
            var (added, removed) = LineDiff.Summarize(_diff);
            DiffSummary = added == 0 && removed == 0
                ? "没有产生任何改动（检查规则是否写在了生效的位置）"
                : $"+{added}  −{removed}";
            HasDiff = true;
            RebuildDiffView();
        }
        finally
        {
            IsPreviewBusy = false;
        }
    }

    /// <summary>按当前视图模式重建差异列表。</summary>
    private void RebuildDiffView()
    {
        DiffLines.Clear();
        if (_diff.Count == 0) return;

        var visible = ChangesOnly
            ? LineDiff.WithContext(_diff, context: 3)
            : _diff;

        foreach (var line in visible)
            DiffLines.Add(new DiffLineViewModel(line));
    }

    private async Task<bool> IsInUseAsync(long overrideId)
    {
        var all = await _host.Overrides.ListAsync();
        if (all.Any(o => o.Id == overrideId && o.IsGlobal)) return true;

        var active = await _host.Profiles.GetActiveAsync();
        if (active is null) return false;
        return (await _host.Overrides.GetProfileOverridesAsync(active.Id)).Contains(overrideId);
    }

    [RelayCommand]
    private async Task RenameAsync(OverrideRowViewModel? row)
    {
        if (row is null) return;
        var dialog = new Microsoft.UI.Xaml.Controls.ContentDialog
        {
            Title = "重命名覆写",
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = Microsoft.UI.Xaml.Controls.ContentDialogButton.Primary,
            XamlRoot = global::Momomi.App.App.Main?.Content?.XamlRoot,
        };
        var box = new Microsoft.UI.Xaml.Controls.TextBox { Text = row.Name };
        dialog.Content = box;
        if (await dialog.ShowAsync() != Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary) return;

        var name = box.Text?.Trim();
        if (string.IsNullOrEmpty(name)) return;

        await _host.Overrides.RenameAsync(row.Id, name);
        StatusText = $"已重命名为 {name}";
        await LoadAsync();
    }

    [RelayCommand]
    private async Task DeleteAsync(OverrideRowViewModel? row)
    {
        if (row is null) return;

        var dialog = new Microsoft.UI.Xaml.Controls.ContentDialog
        {
            Title = "删除覆写",
            Content = $"确定删除「{row.Name}」吗？已挂载它的订阅会失去这项覆写。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = Microsoft.UI.Xaml.Controls.ContentDialogButton.Close,
            XamlRoot = global::Momomi.App.App.Main?.Content?.XamlRoot,
        };
        if (await dialog.ShowAsync() != Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary) return;

        await _host.Overrides.DeleteAsync(row.Id);
        StatusText = $"已删除 {row.Name}";
        await LoadAsync();
    }

    /// <summary>切换全局启用：全局覆写对所有订阅生效。</summary>
    public async Task ToggleGlobalAsync(OverrideRowViewModel row, bool isGlobal)
    {
        await _host.Overrides.SetGlobalAsync(row.Id, isGlobal);
        row.IsGlobal = isGlobal;
        StatusText = isGlobal ? $"「{row.Name}」已全局启用" : $"「{row.Name}」已取消全局启用";

        try
        {
            await _host.ApplyActiveProfileAsync();
        }
        catch
        {
        }
        await LoadAsync();
    }

    private void SetEditorError(string? message)
    {
        EditorError = message ?? "";
        HasEditorError = message is not null;
    }

    private static string ToDisplayText(string text)
        => text.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");

    private static string ToStorageText(string text)
        => text.Replace("\r\n", "\n").Replace("\r", "\n");
}
