using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace PersonalCards;

internal sealed class MarkdownReplyView : RichTextBox
{
    private string _text = "";
    private readonly MdXaml.Markdown _markdown = new();

    public MarkdownReplyView()
    {
        IsReadOnly = true;
        IsDocumentEnabled = true;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        BorderThickness = new Thickness(0);
        Background = Brushes.Transparent;
        Padding = new Thickness(0);
        SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
    }

    public string Text
    {
        get => _text;
        set
        {
            value ??= "";
            if (_text == value) return;
            _text = value;
            Document = _markdown.Transform(value);
            Document.PagePadding = new Thickness(0);
            Document.ColumnWidth = double.PositiveInfinity;
            Document.FontFamily = FontFamily;
            Document.FontSize = FontSize;
            Document.SetResourceReference(FlowDocument.ForegroundProperty, "TextPrimaryBrush");
        }
    }
}