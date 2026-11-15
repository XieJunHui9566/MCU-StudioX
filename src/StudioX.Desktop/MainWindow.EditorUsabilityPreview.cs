namespace StudioX.Desktop;

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using StudioX.Application;
using StudioX.Application.CodeIntelligence;

public partial class MainWindow
{
    public async Task RenderEditorUsabilityPreviewAsync(string directory)
    {
        var checks = new List<string>();
        void Check(bool passed, string description)
        {
            if (!passed)
            {
                throw new InvalidOperationException(description);
            }
            checks.Add(description);
        }
        const string text = """
            #include <stdint.h>
            #define SAMPLE_COUNT 3
            #define clamp(value) ((value) > 100 ? 100 : (value))

            struct Sensor
            {
                uint16_t value;
                uint8_t status;
            };

            static const uint8_t samples[SAMPLE_COUNT] = {
                10, 20, 30
            };

            int main(void)
            {
                struct Sensor sensor;
                sensor.value = samples[0];
                // Braces in comments { } do not create fold markers.
                const char *message = "Hello { World }";
                while (1)
                {
                    sensor.value = clamp(sensor.value + 1);
                }
            }
            """;
        Check(CodeFolding.Find(text, "C").Length == 4, "函数、结构体、数组和循环均有独立折叠区域");
        Check(CodeFolding.Find("// {\n}\nconst char *s = \"{\";\n", "C").Length == 0, "注释、字符串和未配对括号不产生错误区域");
        Check(CodeFolding.Find("auto value = R\"tag({\n})tag\";\n", "C++").Length == 0, "C++ 原始字符串中的括号不被折叠");
        var large = string.Concat(Enumerable.Repeat("void function(void)\n{\n    while (1) { break; }\n}\n", 10000));
        var watch = Stopwatch.StartNew();
        Check(CodeFolding.Find(large, "C").Length == 10000, "大文件线性扫描完整保留一万个定义");
        var scanMilliseconds = watch.Elapsed.TotalMilliseconds;
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            try
            {
                CodeFolding.Find(large, "C", cancelled.Token);
                throw new InvalidOperationException("未取消扫描");
            }
            catch (OperationCanceledException)
            {
                Check(true, "后台扫描支持取消");
            }
        }
        ShowSource(new SourceDocument("src/editor_colors.c", text, Encoding.UTF8, "preview", false));
        var first = activeEditor!;
        await sourceFolding!.RefreshAsync();
        Check(sourceFolding.Manager?.AllFoldings.Count() == 4, "真实源码编辑器已安装折叠边栏");
        var custom = ThemeService.Dark with
        {
            Id = "custom-accent",
            Colors = new(ThemeService.Dark.Colors)
            {
                ["Accent"] = "#F4C95D",
                ["OnAccent"] = "#201C13"
            }
        };
        foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light, custom })
        {
            ApplyTheme(theme);
            await sourceFolding.SetFoldedAsync(false, true);
            SourceEditor.ScrollToHome();
            UpdateLayout();
            await Dispatcher.InvokeAsync(() =>
            {
            }, DispatcherPriority.Render);
            Render(this, Path.Combine(directory, theme.Id + "-expanded.png"));
            await sourceFolding.SetFoldedAsync(true, true);
            Check(sourceFolding.Manager!.AllFoldings.All(folding => folding.IsFolded), theme.Id + " 全部折叠生效");
            UpdateLayout();
            await Dispatcher.InvokeAsync(() =>
            {
            }, DispatcherPriority.Render);
            Render(this, Path.Combine(directory, theme.Id + "-collapsed.png"));
            Check(SourceEditor.Text == text && !SourceEditor.CanUndo, "折叠不改写源码或插入撤销记录");
        }
        ShowSource(new SourceDocument("src/other_editor.c", "int other(void)\n{\n    return 0;\n}\n", Encoding.UTF8, "preview", false));
        await sourceFolding.RefreshAsync();
        var other = activeEditor!;
        ShowDocument(first.Tab);
        await sourceFolding.RefreshAsync();
        Check(sourceFolding.Manager!.AllFoldings.All(folding => folding.IsFolded), "切换标签保留各文件的折叠状态");
        first.Buffer.Insert(0, "// inserted before the collapsed definitions\n");
        await sourceFolding.RefreshAsync();
        ShowDocument(other.Tab);
        ShowDocument(first.Tab);
        await sourceFolding.RefreshAsync();
        Check(sourceFolding.Manager!.AllFoldings.All(folding => folding.IsFolded), "插入行后锚点保持折叠状态，不错折其他区域");
        await sourceFolding.SetFoldedAsync(false, true);
        Check(sourceFolding.Manager.AllFoldings.All(folding => !folding.IsFolded), "全部展开恢复正文");
        Check(CodeFolding.Find("unknown {\ntext\n}", "Text").Length == 0, "未知后缀安全回退");
        first.Buffer.Text = text;
        SourceEditor.FontSize *= 1.3;
        ApplyTheme(ThemeService.Dark);
        UpdateLayout();
        await Dispatcher.InvokeAsync(() =>
        {
        }, DispatcherPriority.Render);
        Render(this, Path.Combine(directory, "large-font.png"));
        await File.WriteAllTextAsync(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new
        {
            passed = true,
            checks,
            scanMilliseconds,
            hardware = false
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
