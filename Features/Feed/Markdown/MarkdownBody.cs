using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace NtfyDesktop.Features.Feed.Markdown;

/// <summary>
/// Attached properties that fill a <see cref="Panel"/> (the feed row's body host) with a
/// message body: a single wrapping selectable <see cref="TextBox"/> for plain text, or the rendered
/// FlowDocument inside a copy-selectable <see cref="RichTextBox"/> from <see cref="MarkdownRenderer"/> when <see cref="IsMarkdownProperty"/>
/// is set. Re-renders whenever either property changes — including when a virtualized row
/// recycles onto a new message (its DataContext changes and both bindings re-evaluate).
/// </summary>
public static class MarkdownBody
{
    private const string PrimaryTextBrush = "TextFillColorPrimaryBrush";

    private static readonly ControlTemplate RichTextBoxTemplate = CreateContentHostTemplate<RichTextBox>();
    private static readonly ControlTemplate TextBoxTemplate = CreateContentHostTemplate<TextBox>();

    private static ControlTemplate CreateContentHostTemplate<T>() where T : FrameworkElement
    {
        var template = new ControlTemplate(typeof(T));
        var factory = new FrameworkElementFactory(typeof(ScrollViewer));
        factory.Name = "PART_ContentHost";
        template.VisualTree = factory;
        return template;
    }

    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(MarkdownBody),
        new PropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty IsMarkdownProperty = DependencyProperty.RegisterAttached(
        "IsMarkdown", typeof(bool), typeof(MarkdownBody),
        new PropertyMetadata(false, OnChanged));

    public static void SetText(DependencyObject d, string? value) => d.SetValue(TextProperty, value);
    public static string? GetText(DependencyObject d) => (string?)d.GetValue(TextProperty);

    public static void SetIsMarkdown(DependencyObject d, bool value) => d.SetValue(IsMarkdownProperty, value);
    public static bool GetIsMarkdown(DependencyObject d) => (bool)d.GetValue(IsMarkdownProperty);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is Panel panel) Render(panel);
    }

    private static void Render(Panel panel)
    {
        panel.Children.Clear();

        var text = GetText(panel);
        if (string.IsNullOrEmpty(text)) return;

        if (GetIsMarkdown(panel))
        {
            // RichTextBox with FlowDocument to allow full text selection and partial selection.
            var rtb = new RichTextBox
            {
                Document = MarkdownRenderer.RenderFlowDocument(text),
                IsReadOnly = true,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Padding = new Thickness(0),
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                IsDocumentEnabled = true, // enables Hyperlink clicks
                Template = RichTextBoxTemplate,
                FocusVisualStyle = null,
            };

            // When user hits Copy and nothing is selected, copy the canonical plain text.
            rtb.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, (s, e) =>
            {
                var rich = (RichTextBox)s;
                var sel = rich.Selection;
                if (sel == null || sel.IsEmpty)
                {
                    Clipboard.SetText(MarkdownRenderer.ToPlainText(text));
                }
                else
                {
                    // Copy the selected plain text (no markdown syntax).
                    Clipboard.SetText(sel.Text);
                }
                e.Handled = true;
            }));

            panel.Children.Add(rtb);
        }
        else
        {
            // Plain text — render in a read-only TextBox so it's selectable and behaves like a TextBlock visually.
            var tb = new TextBox
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                IsReadOnly = true,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                AcceptsReturn = true,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(0),
                Template = TextBoxTemplate,
                FocusVisualStyle = null,
            };
            tb.SetResourceReference(TextBox.ForegroundProperty, PrimaryTextBrush);

            // If user copies without selecting, copy full cleaned text (plain text branch is already plain).
            tb.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, (s, e) =>
            {
                var box = (TextBox)s;
                if (string.IsNullOrEmpty(box.SelectedText))
                    Clipboard.SetText(box.Text);
                else
                    Clipboard.SetText(box.SelectedText);
                e.Handled = true;
            }));
            panel.Children.Add(tb);
        }
    }
}
