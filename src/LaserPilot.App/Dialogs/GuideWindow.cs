using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace LaserPilot.App;

/// <summary>Shows one of the bundled guides (docs/*.md) with simple formatting.</summary>
public sealed partial class GuideWindow : Window
{
    public static void Open(Window owner, string docName, string title)
    {
        string text;
        try
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream($"LaserPilot.docs.{docName}")
                          ?? throw new FileNotFoundException(docName);
            using var reader = new StreamReader(s);
            text = reader.ReadToEnd();
        }
        catch
        {
            MessageBox.Show(owner, "That guide isn't included in this build.", title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        new GuideWindow(text, title) { Owner = owner }.Show();
    }

    private GuideWindow(string markdown, string title)
    {
        Style = (Style)Application.Current.Resources["DarkWindow"];
        Title = title;
        Width = 820;
        Height = 760;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = new FlowDocumentScrollViewer
        {
            Document = Render(markdown),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = (Brush)Application.Current.Resources["BgBrush"],
        };
    }

    [GeneratedRegex(@"(\*\*[^*]+\*\*|`[^`]+`|\[[^\]]+\]\([^)]+\)|<https?://[^>]+>)")]
    private static partial Regex InlinePattern();

    private static FlowDocument Render(string md)
    {
        var text = (Brush)Application.Current.Resources["TextBrush"];
        var accent = (Brush)Application.Current.Resources["AccentBrush"];
        var doc = new FlowDocument
        {
            Background = (Brush)Application.Current.Resources["BgBrush"],
            Foreground = text,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 14,
            PagePadding = new Thickness(28, 18, 28, 28),
            LineHeight = 22,
            TextAlignment = TextAlignment.Left,
        };
        List? list = null;
        var tableRows = new List<string>();

        void FlushTable()
        {
            if (tableRows.Count == 0) return;
            var rows = tableRows.Where(r => !Regex.IsMatch(r, @"^\|?[\s:\-|]+\|?$")).ToList();
            var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 6, 0, 12) };
            // The last column usually holds the explanation: give it the most room.
            int columns = rows.Count == 0 ? 0 : rows[0].Trim().Trim('|').Split('|').Length;
            for (int c = 0; c < columns; c++)
                table.Columns.Add(new TableColumn { Width = new GridLength(c == columns - 1 && columns > 2 ? 3 : 1, GridUnitType.Star) });
            var group = new TableRowGroup();
            table.RowGroups.Add(group);
            for (int r = 0; r < rows.Count; r++)
            {
                var cells = rows[r].Trim().Trim('|').Split('|');
                var row = new TableRow();
                foreach (var cell in cells)
                {
                    var p = Inline(cell.Trim(), text);
                    if (r == 0) p.FontWeight = FontWeights.SemiBold;
                    row.Cells.Add(new TableCell(p)
                    {
                        Padding = new Thickness(8, 5, 8, 5),
                        BorderBrush = (Brush)Application.Current.Resources["PanelBorderBrush"],
                        BorderThickness = new Thickness(0, 0, 0, 1),
                        Background = r == 0 ? (Brush)Application.Current.Resources["PanelBrush"] : null,
                    });
                }
                group.Rows.Add(row);
            }
            doc.Blocks.Add(table);
            tableRows.Clear();
        }

        foreach (var raw in md.Replace("\r", "").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.StartsWith('|')) { list = null; tableRows.Add(line); continue; }
            FlushTable();
            if (line.StartsWith("```", StringComparison.Ordinal)) continue;
            if (line.Length == 0) { list = null; continue; }

            if (line.StartsWith('#'))
            {
                list = null;
                int level = line.TakeWhile(c => c == '#').Count();
                var p = Inline(line[level..].Trim(), level == 1 ? accent : text);
                p.FontSize = level switch { 1 => 24, 2 => 19, _ => 16 };
                p.FontWeight = FontWeights.Bold;
                p.Margin = new Thickness(0, level == 1 ? 0 : 14, 0, 6);
                doc.Blocks.Add(p);
                continue;
            }

            var bullet = Regex.Match(line, @"^(\s*)([-*]|\d+\.)\s+(.*)$");
            if (bullet.Success)
            {
                bool numbered = char.IsDigit(bullet.Groups[2].Value[0]);
                if (list is null || (list.MarkerStyle == TextMarkerStyle.Decimal) != numbered || bullet.Groups[1].Length > 0)
                {
                    list = new List { MarkerStyle = numbered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc, Margin = new Thickness(bullet.Groups[1].Length > 0 ? 24 : 0, 2, 0, 6) };
                    doc.Blocks.Add(list);
                }
                list.ListItems.Add(new ListItem(Inline(bullet.Groups[3].Value, text)));
                continue;
            }

            list = null;
            var para = Inline(line.TrimStart('>', ' '), text);
            para.Margin = new Thickness(0, 2, 0, 8);
            doc.Blocks.Add(para);
        }
        FlushTable();
        return doc;
    }

    private static Paragraph Inline(string s, Brush brush)
    {
        var p = new Paragraph { Foreground = brush };
        int last = 0;
        foreach (Match m in InlinePattern().Matches(s))
        {
            if (m.Index > last) p.Inlines.Add(new Run(s[last..m.Index]));
            var token = m.Value;
            if (token.StartsWith("**", StringComparison.Ordinal))
                p.Inlines.Add(new Bold(new Run(token[2..^2])));
            else if (token.StartsWith('`'))
                p.Inlines.Add(new Run(token[1..^1])
                {
                    FontFamily = new FontFamily("Consolas"),
                    Background = (Brush)Application.Current.Resources["PanelBrush"],
                    Foreground = (Brush)Application.Current.Resources["WarnBrush"],
                });
            else
            {
                // [text](url) or <url>
                string label, url;
                if (token.StartsWith('<')) { url = token[1..^1]; label = url; }
                else
                {
                    var close = token.IndexOf("](", StringComparison.Ordinal);
                    label = token[1..close];
                    url = token[(close + 2)..^1];
                }
                if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    var link = new Hyperlink(new Run(label)) { NavigateUri = new Uri(url) };
                    link.RequestNavigate += (_, e) => MainWindow.OpenUrl(e.Uri.ToString());
                    p.Inlines.Add(link);
                }
                else p.Inlines.Add(new Run(label));
            }
            last = m.Index + m.Length;
        }
        if (last < s.Length) p.Inlines.Add(new Run(s[last..]));
        return p;
    }
}
