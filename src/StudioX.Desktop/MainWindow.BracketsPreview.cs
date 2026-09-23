namespace StudioX.Desktop;

using System.Text;
using System.Windows.Media;
using System.Windows.Threading;
using StudioX.Application;
using StudioX.Application.CodeIntelligence;

public partial class MainWindow
{
    /// <summary>只在内存里编辑示例，检查配对、异步过期结果和实际排版颜色。</summary>
    public async Task RenderBracketsPreviewAsync(string directory, string project)
    {
        Check(BracketPairs.Find("{f(a[0]);}", "C").SequenceEqual(new ColoredBracket[] { new(0, 0), new(2, 1), new(4, 2), new(6, 2), new(7, 1), new(9, 0) }), "混合嵌套的配对深度");
        const string literals = "{ /* ([ */ const char *s = \"[\\\"}]\"; char c = ')'; // }\n }";
        Check(BracketPairs.Find(literals, "C").Length == 2, "C 注释、转义字符串、字符常量");
        const string raw = "{ auto s = R\"tag(\" } ([ //)tag\"; }";
        Check(BracketPairs.Find(raw, "C++").Length == 2, "C++ 原始字符串");
        Check(BracketPairs.Find("{ // continued \\\r\n }\n}", "C").Length == 2, "续行注释");
        Check(BracketPairs.Find("{ int n = 1'024; f(n); }", "C++").Length == 4, "数字分隔符");
        Check(BracketPairs.Find("set(A [=[ ([)} ]=]) # (\n#[==[ (}] ]==]\nset(B \"[)]\")", "CMake").Length == 4, "CMake 长括号参数及注释");
        Check(BracketPairs.Find("{\"[\": [1, 2]}", "JSON").Length == 4, "JSON 字符串排除");
        Check(BracketPairs.Find("assign value = 'hA5 + bus[0]; // ]\n", "Verilog").Length == 2, "Verilog 单引号常量与注释");
        Check(BracketPairs.Find("(]", "C").Length == 0 && BracketPairs.Find("{x();", "C").Length == 2, "未配对括号保持原色");
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            try { BracketPairs.Find("{}", "C", canceled.Token); throw new InvalidOperationException("取消未生效"); }
            catch (OperationCanceledException) { }
        }

        await OpenProjectAsync(project, CancellationToken.None);
        const string sample = """
            #include "system_config.h"

            /* 注释里的 { [ ( 不参与配对着色。 */
            static const char *label = "brackets: { [ ( ) ] }";
            static int samples[4] = { 120, 256, 512, 1024 };

            int main(void)
            {
                System_Init();

                for (;;) {
                    for (int i = 0; i < 4; ++i) {
                        if ((samples[i] > 128) && (samples[i] < 900)) {
                            samples[i] = (samples[(i + 1) % 4] + 16);
                        }
                    }
                    System_Delay(100);
                }
            }
            """;
        ShowSource(new SourceDocument("src/bracket_review.c", sample, Encoding.UTF8, "", false));
        var session = activeEditor!;
        await bracketColors!.RefreshAsync();
        Check(bracketColors.Brackets.Length > 30, "实际编辑器配色接入");
        foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light })
        {
            ApplyTheme(theme); SourceEditor.Select(0, 0); SourceEditor.ScrollToHome(); UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            var view = SourceEditor.TextArea.TextView;
            var actualColors = bracketColors.Brackets.Select(bracket =>
            {
                var line = view.VisualLines.Single(line => line.FirstDocumentLine.Offset <= bracket.Offset && line.LastDocumentLine.EndOffset > bracket.Offset);
                var element = line.Elements.Single(element => line.FirstDocumentLine.Offset + element.RelativeTextOffset <= bracket.Offset && line.FirstDocumentLine.Offset + element.RelativeTextOffset + element.DocumentLength > bracket.Offset);
                return (bracket.Depth, Color: ((SolidColorBrush)element.TextRunProperties.ForegroundBrush).Color);
            }).ToArray();
            Check(actualColors.GroupBy(item => item.Depth).All(group => group.Select(item => item.Color).Distinct().Count() == 1), "相同配对深度实际字形颜色一致");
            Check(actualColors.Select(item => item.Color).Distinct().Count() >= 4, "多层嵌套实际字形颜色区分");
            Render(this, Path.Combine(directory, "brackets-" + theme.Id + ".png"));
        }
        session.Buffer.Text = "{ value[0]; }";
        await bracketColors.RefreshAsync();
        session.Buffer.Remove(0, 1); await bracketColors.RefreshAsync();
        Check(bracketColors.Brackets.Length == 2, "删除括号后不残留旧配对");
        SourceEditor.Undo(); await bracketColors.RefreshAsync();
        Check(bracketColors.Brackets.Length == 4, "撤销后恢复配对");
        session.Buffer.Text = string.Concat(Enumerable.Repeat("{ f(a[0]); }\n", 60000));
        var stale = bracketColors.RefreshAsync();
        ShowSource(new SourceDocument("src/bracket_review.json", "{\"x\":[0]}", Encoding.UTF8, "", false));
        await bracketColors.RefreshAsync(); await stale;
        Check(bracketColors.Brackets.Select(item => item.Offset).SequenceEqual(new[] { 0, 5, 7, 8 }), "快速切换文件拒绝过期结果");
        session.Buffer.Text = sample;
        ShowDocument(session.Tab); ApplyTheme(ThemeService.Dark); await bracketColors.RefreshAsync();
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), "PASS: paired colors and nested depths, comments/strings/raw strings/numeric separators/CMake/JSON/Verilog, unmatched brackets, cancellation, real dark/light glyph colors, editing/undo and stale results after switching a large document. No project writes or hardware access.\n");

        static void Check(bool pass, string name) { if (!pass) throw new InvalidOperationException(name); }
    }
}
