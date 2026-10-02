namespace StudioX.Desktop;

using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

/// <summary>只渲染随产品分发的文本、标题、列表、表格与代码，不执行脚本或加载外部内容。</summary>
internal static class HelpDocumentRenderer
{
    internal static FlowDocument Render(string markdown)
    {
        var document = new FlowDocument { FontFamily = new("Microsoft YaHei UI"), FontSize = 14, LineHeight = 25, TextAlignment = TextAlignment.Left, PagePadding = new(24, 0, 24, 24) };
        document.SetResourceReference(TextElement.ForegroundProperty, "Text");
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("# ", StringComparison.Ordinal)) continue;
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                var code = new List<string>();
                while (++index < lines.Length && !lines[index].StartsWith("```", StringComparison.Ordinal)) code.Add(lines[index]);
                var block = new Paragraph(new Run(string.Join('\n', code))) { FontFamily = new("Consolas"), FontSize = 13, LineHeight = 21, Padding = new(12), Margin = new(0, 8, 0, 14) };
                block.SetResourceReference(TextElement.BackgroundProperty, "ToolSurface");
                document.Blocks.Add(block);
            }
            else if (line.StartsWith('|'))
            {
                var rows = new List<string[]>();
                do
                {
                    var cells = lines[index].Trim().Trim('|').Split('|').Select(cell => cell.Trim()).ToArray();
                    if (!cells.All(cell => Regex.IsMatch(cell, "^:?-+:?$"))) rows.Add(cells);
                    index++;
                } while (index < lines.Length && lines[index].StartsWith('|'));
                index--;
                var table = new Table { CellSpacing = 0, Margin = new(0, 6, 0, 16) };
                var group = new TableRowGroup();
                table.RowGroups.Add(group);
                foreach (var cells in rows)
                {
                    var row = new TableRow();
                    foreach (var cell in cells)
                    {
                        var value = new TableCell(Paragraph(cell)) { Padding = new(8), BorderThickness = new(0, 0, 0, 1) };
                        value.SetResourceReference(TableCell.BorderBrushProperty, "Border");
                        if (group.Rows.Count == 0) { value.FontWeight = FontWeights.SemiBold; value.SetResourceReference(TextElement.BackgroundProperty, "ToolSurface"); }
                        row.Cells.Add(value);
                    }
                    group.Rows.Add(row);
                }
                document.Blocks.Add(table);
            }
            else
            {
                var heading = line.StartsWith("## ", StringComparison.Ordinal) ? 2 : line.StartsWith("### ", StringComparison.Ordinal) ? 3 : 0;
                var block = Paragraph(heading > 0 ? line[(heading + 1)..] : line.StartsWith("- ", StringComparison.Ordinal) ? "•  " + line[2..] : line);
                block.Margin = heading > 0 ? new(0, 18, 0, 8) : new(0, 0, 0, 9);
                if (heading > 0) { block.FontSize = heading == 2 ? 19 : 16; block.FontWeight = FontWeights.SemiBold; }
                document.Blocks.Add(block);
            }
        }
        return document;
    }

    private static Paragraph Paragraph(string text)
    {
        var paragraph = new Paragraph();
        foreach (var part in Regex.Split(text, "(`[^`]+`|\\*\\*[^*]+\\*\\*)"))
        {
            if (part.StartsWith('`') && part.EndsWith('`')) paragraph.Inlines.Add(new Run(part[1..^1]) { FontFamily = new FontFamily("Consolas") });
            else if (part.StartsWith("**", StringComparison.Ordinal) && part.EndsWith("**", StringComparison.Ordinal)) paragraph.Inlines.Add(new Bold(new Run(part[2..^2])));
            else paragraph.Inlines.Add(new Run(part));
        }
        return paragraph;
    }
}
