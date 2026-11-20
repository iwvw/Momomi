using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Momomi.Core.Models;
using Momomi.Core.Services;

namespace Momomi.App.ViewModels;

public sealed partial class LogsViewModel : ObservableObject
{
    private const int MaxLines = 1000;

    private readonly ICoreManager _core;
    private readonly DispatcherQueue _dispatcher;
    private readonly EventHandler<LogEntry> _onLogReceived;
    private readonly List<LogEntry> _pending = new();
    private readonly object _pendingGate = new();
    private bool _flushScheduled;

    public ObservableCollection<LogEntry> Items { get; } = new();

    [ObservableProperty]
    public partial string Level { get; set; } = "info";

    [ObservableProperty]
    public partial bool IsPaused { get; set; }

    public LogsViewModel(ICoreManager core, DispatcherQueue dispatcher)
    {
        _core = core;
        _dispatcher = dispatcher;
        _onLogReceived = (_, entry) => OnLog(entry);
    }

    private bool _attached;

    public void Attach()
    {
        if (_attached) return;
        _attached = true;
        _core.LogReceived += _onLogReceived;
    }

    public void Detach()
    {
        if (!_attached) return;
        _attached = false;
        _core.LogReceived -= _onLogReceived;
    }

    private void OnLog(LogEntry entry)
    {
        if (IsPaused) return;
        if (!ShouldShow(entry.Type)) return;

        var schedule = false;
        lock (_pendingGate)
        {
            _pending.Add(entry);
            if (!_flushScheduled)
            {
                _flushScheduled = true;
                schedule = true;
            }
        }
        if (schedule) _ = FlushLaterAsync();
    }

    private async Task FlushLaterAsync()
    {
        await Task.Delay(150).ConfigureAwait(false);
        Flush();
    }

    private void Flush()
    {
        List<LogEntry> batch;
        lock (_pendingGate)
        {
            _flushScheduled = false;
            if (_pending.Count == 0) return;
            batch = new List<LogEntry>(_pending);
            _pending.Clear();
        }

        _dispatcher.TryEnqueue(() =>
        {
            foreach (var entry in batch) Items.Add(entry);
            var overflow = Items.Count - MaxLines;
            for (var i = 0; i < overflow; i++) Items.RemoveAt(0);
        });
    }

    private bool ShouldShow(string type)
    {
        static int Rank(string t) => t.ToLowerInvariant() switch
        {
            "debug" => 0,
            "info" => 1,
            "warning" => 2,
            "error" => 3,
            _ => 1,
        };
        return Rank(type) >= Rank(Level);
    }

    [RelayCommand]
    private void Clear()
    {
        lock (_pendingGate) _pending.Clear();
        Items.Clear();
    }

    [RelayCommand]
    private void CopyAll()
    {
        try
        {
            var text = string.Join(Environment.NewLine, Items.Select(i => $"[{i.Type}] {i.Payload}"));
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        }
        catch
        {
        }
    }
}
