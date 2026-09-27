using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Momomi.Core.Models;
using Momomi.Core.Services;

namespace Momomi.App.ViewModels;

public sealed partial class RuleRowViewModel
{
    public int Index { get; }
    public string Type { get; }
    public string Payload { get; }
    public string Proxy { get; }
    public string Size { get; }

    public RuleRowViewModel(RuleItem item)
    {
        Index = item.Index;
        Type = item.Type;
        Payload = item.Payload;
        Proxy = item.Proxy;
        Size = item.Size ?? "—";
    }
}

public sealed partial class RulesViewModel : ObservableObject
{
    private readonly ICoreManager _core;
    private readonly DispatcherQueue _dispatcher;
    private IReadOnlyList<RuleItem> _all = Array.Empty<RuleItem>();
    private readonly EventHandler<CoreStateChanged> _onStateChanged;

    public ObservableCollection<RuleRowViewModel> Items { get; } = new();

    [ObservableProperty]
    public partial string StatusText { get; set; } = "未加载";

    [ObservableProperty]
    public partial string Filter { get; set; } = "";

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    public RulesViewModel(ICoreManager core, DispatcherQueue dispatcher)
    {
        _core = core;
        _dispatcher = dispatcher;
        _onStateChanged = (_, e) =>
        {
            if (e.State != CoreState.Running) _all = Array.Empty<RuleItem>();
            else _ = AutoReloadAsync();
        };
    }

    private bool _attached;

    public void Attach()
    {
        if (_attached) return;
        _attached = true;
        _core.StateChanged += _onStateChanged;
    }

    public void Detach()
    {
        if (!_attached) return;
        _attached = false;
        _core.StateChanged -= _onStateChanged;
    }

    private async Task AutoReloadAsync()
    {
        await Task.Delay(500);
        await LoadAsync(force: true);
    }

    public async Task LoadAsync(bool force = false)
    {
        if (_core.Api is null || _core.State != CoreState.Running)
        {
            StatusText = "内核未运行";
            return;
        }
        if (_all.Count > 0 && !force) return;

        IsBusy = true;
        try
        {
            _all = await _core.Api.GetRulesAsync().ConfigureAwait(false);
            _dispatcher.TryEnqueue(() =>
            {
                Apply();
                StatusText = $"共 {_all.Count} 条规则";
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

    private void Apply()
    {
        var keyword = Filter?.Trim() ?? "";
        var rows = _all
            .Where(r => keyword.Length == 0
                || r.Payload.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || r.Type.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || r.Proxy.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            .Select(r => new RuleRowViewModel(r))
            .ToList();

        Items.Clear();
        foreach (var row in rows) Items.Add(row);
    }

    [RelayCommand]
    private async Task RefreshAsync() => await LoadAsync(force: true);

    partial void OnFilterChanged(string value) => Apply();
}
