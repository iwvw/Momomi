using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Momomi.Core.Services;

namespace Momomi.App.ViewModels;

/// <summary>差异比较的可选模式。</summary>
public enum DiffViewMode
{
    /// <summary>合并结果全文，改动行高亮。</summary>
    Full,

    /// <summary>只显示改动行及其上下文。</summary>
    ChangesOnly,
}

/// <summary>
/// 预览里的一行差异，带好了显示用的编号、符号与配色。
/// 配色在构造时算好，避免在 DataTemplate 里堆转换器。
/// </summary>
public sealed class DiffLineViewModel
{
    public string Glyph { get; }
    public string Text { get; }
    public string LineNumber { get; }
    public Windows.UI.Color RowBackground { get; }
    public Brush RowBrush { get; }
    public Brush GlyphBrush { get; }
    public bool HasBackground { get; }

    public DiffLineViewModel(DiffLine line)
    {
        Text = line.Text;
        Glyph = line.Kind switch
        {
            DiffLineKind.Added => "+",
            DiffLineKind.Removed => "−",
            _ => " ",
        };

        // 用合并结果的行号；纯删除行没有结果行号，退回原文行号。
        var number = line.Kind == DiffLineKind.Removed ? line.SourceLine : line.ResultLine;
        LineNumber = number > 0 ? number.ToString() : "";

        HasBackground = line.Kind != DiffLineKind.Same;
        RowBackground = line.Kind switch
        {
            DiffLineKind.Added => Windows.UI.Color.FromArgb(0x24, 0x3F, 0xB9, 0x50),
            DiffLineKind.Removed => Windows.UI.Color.FromArgb(0x24, 0xE5, 0x48, 0x4D),
            _ => Colors.Transparent,
        };
        RowBrush = new SolidColorBrush(RowBackground);

        var glyphColor = line.Kind switch
        {
            DiffLineKind.Added => Windows.UI.Color.FromArgb(0xFF, 0x3F, 0xB9, 0x50),
            DiffLineKind.Removed => Windows.UI.Color.FromArgb(0xFF, 0xE5, 0x48, 0x4D),
            _ => Windows.UI.Color.FromArgb(0x00, 0x00, 0x00, 0x00),
        };
        GlyphBrush = new SolidColorBrush(glyphColor);

        // 正文颜色交给 XAML 的主题资源，深浅色自动适配；这里只管符号颜色。
    }
}
