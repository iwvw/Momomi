using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Momomi.Core.Services;
using Windows.UI;

namespace Momomi.App.Controls;

/// <summary>
/// 带 YAML 语法高亮的编辑器。
///
/// 用 RichEditBox 而不是 TextBox：WinRT 的 ITextDocument 支持按范围设置前景色，
/// 这是能自绘高亮的唯一原生控件（本应用打包时移除了 WebView2，用不了 Monaco）。
///
/// 有三个必须处理好的坑：
///
/// 1. <b>控件未 Loaded 时写入会丢</b>。页面刚把编辑器切到可见时，Loaded 还没触发，
///    此时调 Document.SetText 写不进去，表现为"编辑器空白"。所以文本先存到
///    _pendingText，等 Loaded 后再真正落盘。
/// 2. <b>每次改格式都会重置选区</b>，着色前后必须显式保存/恢复，否则光标乱跳。
/// 3. <b>每次按键全量着色会卡</b>，用 DispatcherQueue 做防抖，停手 ~40ms 才刷一次。
/// </summary>
public sealed partial class CodeEditor : UserControl
{
    private DispatcherQueueTimer? _highlightTimer;
    private bool _applyingHighlight;
    private bool _loaded;

    /// <summary>控件就绪前收到的文本，Loaded 后写入。</summary>
    private string? _pendingText;

    /// <summary>文本变化（已过滤掉着色/程序写入自身触发的事件）。</summary>
    public event EventHandler? TextChanged;

    public CodeEditor()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>编辑器文本（统一 LF 换行，便于与存储格式一致）。</summary>
    public string Text
    {
        get
        {
            // 未加载时文档是空的，返回缓存内容，避免调用方读到空串。
            if (!_loaded) return _pendingText ?? "";

            var text = GetText();
            // 文档里只有占位的 \r（还没写内容）时，用缓存兜底。
            return text.Length == 0 && _pendingText is not null ? _pendingText : text;
        }
        set => SetText(value);
    }

    public bool IsReadOnly
    {
        get => Editor.IsReadOnly;
        set => Editor.IsReadOnly = value;
    }
    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        _loaded = true;

        if (_highlightTimer is null)
        {
            _highlightTimer = DispatcherQueue.CreateTimer();
            _highlightTimer.Interval = TimeSpan.FromMilliseconds(40);
            _highlightTimer.IsRepeating = false;
            _highlightTimer.Tick += (_, _) => ApplyHighlight();
        }

        // 把控件就绪前暂存的文本写进去。
        if (_pendingText is not null)
        {
            var text = _pendingText;
            _pendingText = null;
            WriteToDocument(text);
        }
        else
        {
            ScheduleHighlight();
        }
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        _highlightTimer?.Stop();
    }

    private string GetText()
    {
        Editor.TextDocument.GetText(TextGetOptions.None, out var text);
        // RichEditBox 会在末尾追加一个 \r，去掉它。
        return text.TrimEnd('\r').Replace("\r\n", "\n").Replace('\r', '\n');
    }

    private void SetText(string? value)
    {
        var text = (value ?? "").Replace("\r\n", "\n").Replace('\r', '\n');

        if (!_loaded)
        {
            // 还没就绪：先记下来，Loaded 时再写。
            _pendingText = text;
            return;
        }

        // 已经就绪：直接写入，并清掉缓存避免读到旧内容。
        _pendingText = null;
        WriteToDocument(text);
    }

    private void WriteToDocument(string text)
    {
        // 不再用 IsLoaded / ActualWidth 做前置判断——控件刚从 Collapsed 切到可见时
        // ActualWidth 还是 0，靠尺寸判断只会把自己转进死循环（之前就是这样卡死的）。
        // SetText 本身在未加载时也能安全调用，写完校验一下即可。
        _applyingHighlight = true;
        try
        {
            // SetText 只认 \r 作为换行。
            Editor.TextDocument.SetText(TextSetOptions.None, text.Replace('\n', '\r'));
        }
        catch (Exception ex)
        {
            global::Momomi.App.App.WriteLog($"CodeEditor：SetText 失败：{ex.Message}");
        }
        finally
        {
            _applyingHighlight = false;
        }

        ScheduleHighlight();
    }

    private void Editor_TextChanged(object sender, RoutedEventArgs e)
    {
        if (_applyingHighlight || !_loaded) return;
        TextChanged?.Invoke(this, EventArgs.Empty);
        ScheduleHighlight();
    }

    private void ScheduleHighlight()
    {
        if (_highlightTimer is null)
        {
            // 计时器还没建（未 Loaded）：直接下次 Loaded 再处理。
            return;
        }
        _highlightTimer.Stop();
        _highlightTimer.Start();
    }

    /// <summary>Tab 插入两个空格，避免焦点跳到下一个控件。</summary>
    private void Editor_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Tab) return;
        e.Handled = true;

        var selection = Editor.TextDocument.Selection;
        selection.TypeText("  ");
    }

    /// <summary>
    /// 只接受纯文本粘贴。RichEditBox 默认会把外部格式（字号、颜色、字体）
    /// 一起粘进来，那会和高亮互相打架，也会污染保存下来的 YAML。
    /// </summary>
    private async void Editor_Paste(object sender, TextControlPasteEventArgs e)
    {
        try
        {
            var clipboard = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
            if (!clipboard.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text)) return;

            e.Handled = true;
            var text = await clipboard.GetTextAsync();
            if (string.IsNullOrEmpty(text)) return;

            Editor.TextDocument.Selection.TypeText(text.Replace("\r\n", "\r").Replace('\n', '\r'));
        }
        catch
        {
            // 读剪贴板失败时交回默认行为。
        }
    }

    private void ApplyHighlight()
    {
        if (_applyingHighlight || !_loaded) return;

        var text = GetText();
        if (text.Length == 0) return;

        _applyingHighlight = true;
        var batching = false;
        var undoGroup = false;
        try
        {
            // 关键：绝对不要用 GetRange(0, MaxUnitCount) 去设置整个文档的字符格式。
            // 那样会把全文涂成一个连续格式块，导致：
            //   - 选区反向（从后往前选）失效
            //   - 撤销栈被搅乱，Ctrl+Z 行为异常
            //   - 整篇文本被强制成同一颜色，看起来"没有高亮"
            // 正确做法：只给有颜色的 token 上色，其余位置保持控件的默认格式不动。
            Editor.TextDocument.BatchDisplayUpdates();
            batching = true;

            // 把这一批格式变更归入同一个撤销组，避免每次着色都在撤销栈里留下
            // 几十条"改格式"记录，把用户真正的编辑操作挤出去。
            Editor.TextDocument.BeginUndoGroup();
            undoGroup = true;

            // 保存光标与选区，着色前后原样恢复。
            var selection = Editor.TextDocument.Selection;
            var start = selection.StartPosition;
            var end = selection.EndPosition;

            foreach (var token in YamlHighlighter.Tokenize(text))
            {
                var color = ColorFor(token.Kind);
                if (color is null) continue;

                var range = Editor.TextDocument.GetRange(token.Start, token.End);

                // 只改前景色，不动字号/字重等其它属性，避免污染默认格式。
                range.CharacterFormat.ForegroundColor = color.Value;
            }

            selection.SetRange(start, end);
        }
        catch (Exception ex)
        {
            // 着色失败不能让编辑器不可用：保持纯文本状态，同时把原因写进日志便于排查。
            global::Momomi.App.App.WriteLog($"语法着色失败（已降级为纯文本）：{ex}");
        }
        finally
        {
            if (undoGroup)
            {
                try { Editor.TextDocument.EndUndoGroup(); } catch { }
            }
            if (batching)
            {
                try { Editor.TextDocument.ApplyDisplayUpdates(); } catch { }
            }
            _applyingHighlight = false;
        }
    }

    private static Color? ColorFor(YamlTokenKind kind) => kind switch
    {
        // 键名：蓝色系
        YamlTokenKind.Key => Color.FromArgb(0xFF, 0x1F, 0x6F, 0xEB),
        // 字符串：棕红色（区分于键）
        YamlTokenKind.String => Color.FromArgb(0xFF, 0xC1, 0x50, 0x2D),
        // 数字：青绿
        YamlTokenKind.Number => Color.FromArgb(0xFF, 0x0E, 0x8A, 0x8A),
        // 关键字：紫色
        YamlTokenKind.Keyword => Color.FromArgb(0xFF, 0x8B, 0x3F, 0xC1),
        // 注释：灰
        YamlTokenKind.Comment => Color.FromArgb(0xFF, 0x8A, 0x8A, 0x8A),
        // 列表符号、锚点：默认色
        _ => null,
    };
}
