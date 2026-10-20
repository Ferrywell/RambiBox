using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using RambiBox.Core.Updates;

namespace RambiBox.App.Views;

/// <summary>Release notes of a version, rendered from the release's Markdown (headings, bullets, bold, links).</summary>
public partial class ReleaseNotesWindow : Window
{
    private readonly ReleaseInfo _release;
    private readonly ICommand? _update;

    /// <param name="update">"Update now" (null hides the button).</param>
    public ReleaseNotesWindow(ReleaseInfo release, ICommand? update)
    {
        InitializeComponent();
        _release = release;
        _update = update;
        Title = $"What's new in RambiBox {release.Version}";
        TitleText.Text = $"RambiBox {release.Version}";
        SubtitleText.Text = (release.PublishedAt is { } p ? $"Released {p.ToLocalTime():d MMMM yyyy}" : "")
                            + (release.IsPrerelease ? " · pre-release" : "")
                            + $" · you have {AppInfo.Version}";
        UpdateButton.Visibility = update is null ? Visibility.Collapsed : Visibility.Visible;
        Viewer.Document = Render(release.Notes);
    }

    private FlowDocument Render(string markdown)
    {
        var doc = new FlowDocument
        {
            FontFamily = FontFamily,
            FontSize = 14,
            PagePadding = new Thickness(14, 10, 14, 10),
            TextAlignment = TextAlignment.Left,
        };
        doc.SetResourceReference(FlowDocument.ForegroundProperty, "TextFillColorPrimaryBrush");
        List? list = null;
        var blocks = SimpleMarkdown.Parse(markdown);
        if (blocks.Count == 0)
        {
            doc.Blocks.Add(new Paragraph(new Run("No release notes were published for this version.")));
            return doc;
        }
        foreach (var b in blocks)
        {
            if (b.Kind != MdBlockKind.Bullet) list = null;
            switch (b.Kind)
            {
                case MdBlockKind.Heading:
                    var h = new Paragraph { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 4) };
                    h.FontSize = b.Level <= 1 ? 20 : b.Level == 2 ? 17 : 15;
                    AddInlines(h.Inlines, b.Inlines);
                    doc.Blocks.Add(h);
                    break;
                case MdBlockKind.Bullet:
                    if (list is null)
                    {
                        list = new List { MarkerStyle = TextMarkerStyle.Disc, Margin = new Thickness(0, 2, 0, 6), Padding = new Thickness(22, 0, 0, 0) };
                        doc.Blocks.Add(list);
                    }
                    var p = new Paragraph { Margin = new Thickness(b.Level * 18, 1, 0, 1) };
                    AddInlines(p.Inlines, b.Inlines);
                    list.ListItems.Add(new ListItem(p));
                    break;
                case MdBlockKind.Code:
                    var code = new Paragraph(new Run(b.Inlines.FirstOrDefault()?.Text ?? ""))
                    {
                        FontFamily = (FontFamily)FindResource("MonoFont"),
                        FontSize = 12.5,
                        Padding = new Thickness(10, 6, 10, 6),
                    };
                    code.SetResourceReference(Block.BackgroundProperty, "SubtleFillColorSecondaryBrush");
                    doc.Blocks.Add(code);
                    break;
                default:
                    var para = new Paragraph { Margin = new Thickness(0, 4, 0, 6) };
                    AddInlines(para.Inlines, b.Inlines);
                    doc.Blocks.Add(para);
                    break;
            }
        }
        return doc;
    }

    private void AddInlines(InlineCollection target, IReadOnlyList<MdInline> inlines)
    {
        foreach (var i in inlines)
        {
            switch (i.Kind)
            {
                case MdInlineKind.Bold:
                    target.Add(new Bold(new Run(i.Text)));
                    break;
                case MdInlineKind.Italic:
                    target.Add(new Italic(new Run(i.Text)));
                    break;
                case MdInlineKind.Code:
                    target.Add(new Run(i.Text) { FontFamily = (FontFamily)FindResource("MonoFont"), FontSize = 12.5 });
                    break;
                case MdInlineKind.Link when i.Url is { } url:
                    var link = new Hyperlink(new Run(i.Text)) { ToolTip = url };
                    link.Click += (_, _) => AppInfo.OpenUrl(url);
                    target.Add(link);
                    break;
                default:
                    target.Add(new Run(i.Text));
                    break;
            }
        }
    }

    private void OnOpenGitHub(object sender, RoutedEventArgs e) => AppInfo.OpenUrl(_release.HtmlUrl);

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnUpdate(object sender, RoutedEventArgs e)
    {
        Close();
        if (_update?.CanExecute(null) == true) _update.Execute(null);
    }
}
