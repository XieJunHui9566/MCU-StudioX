namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using StudioX.Application;
using StudioX.Application.Output;
using StudioX.Engine;
using StudioX.Engine.Hdl;

public partial class MainWindow
{
    /// <summary>隔离工程真实编译及只读日志呈现检查；不连接或写入设备，不改变安装版或用户工程。</summary>
    public async Task RenderBuildOutputPreviewAsync(string directory, string sourceProject)
    {
        var sourceManifest = await ProjectService.ReadAsync(sourceProject);
        var checks = new List<string>();
        void Check(bool condition, string name)
        {
            if (!condition)
            {
                File.WriteAllText(Path.Combine(directory, "failed-display.log"), BuildLog.Text);
                throw new InvalidOperationException(name);
            }
            checks.Add(name);
        }
        Check(BuildOutputParser.Tone(@"E:\source\warning.c") == OutputTone.Normal &&
            BuildOutputParser.Tone("0 errors, 0 warnings") == OutputTone.Normal, "paths and zero diagnostic counts stay neutral");
        Check(BuildOutputParser.Tone("main.c:10: error 20: Undefined identifier") == OutputTone.Error &&
            BuildOutputParser.Tone("CMake Error at CMakeLists.txt:10") == OutputTone.Error &&
            BuildOutputParser.Tone("main.c:5: warning: unused variable") == OutputTone.Warning, "SDCC, GCC and CMake diagnostics retain severity");
        Check(BuildOutputParser.Tone("E (123) component: failed") == OutputTone.Error &&
            BuildOutputParser.Tone("W (123) component: warning") == OutputTone.Warning &&
            BuildOutputParser.Tone("file.v:7: warning: invalid port") == OutputTone.Warning &&
            BuildOutputParser.Tone("FATAL: testbench.v:12: assertion") == OutputTone.Error &&
            BuildOutputParser.Tone("project: error MSB1001: unknown switch") == OutputTone.Error &&
            BuildOutputParser.Tone("ERROR: Yosys failed") == OutputTone.Error, "IDF, Icarus, Yosys and MSBuild retain diagnostic severity");
        Check(BuildOutputParser.Measure("[23:54:32] text") is null && BuildOutputParser.Measure("[9/8] text") is null &&
            BuildOutputParser.Measure("[1/0] text") is null && BuildOutputParser.Measure("[130%] text") is null, "timestamps and invalid tool measurements do not fabricate progress");
        Check(BuildOutputParser.Measure("[25%] Building C object")?.Detail == "25%", "percentage-only tools do not invent task counts");
        LvglPreview.RenderSnapshot(new("fixture", "Building", "PC 构建", false, false,
            "校验开发环境组件 pc.mingw / 1.0.0… 25%（1/4）\nwarning: PC verification notice\n校验开发环境组件 pc.mingw / 1.0.0… 100%（4/4）\n[PC 配置]\n-- Configuring\n", null, null));
        Check(LvglPreview.DiagnosticLog.IsReadOnly && LvglPreview.DiagnosticLog.Text.Split('\n').Count(l => l.Contains("校验开发环境组件")) == 1 &&
            LvglPreview.DiagnosticLog.Text.Contains("warning: PC verification notice") &&
            LvglPreview.DiagnosticLog.Text.Split('\n').Last().StartsWith("[进度] PC 配置 [", StringComparison.Ordinal) &&
            !LvglPreview.DiagnosticLog.Text.Split('\n').Last().Contains('%'), "PC verification coalesces in place, preserves warnings and resets counts before configuration");
        LvglPreview.RenderSnapshot(new("fixture", "Building", "PC 构建", false, false,
            "[PC 配置]\n[PC 编译]\n[3/8] Building C object\n", null, null));
        Check(LvglPreview.DiagnosticLog.Text.Contains("[进度] PC 编译 [=========>--------------] 38% (3/8)"),
            "PC compilation presents its own reported task counts in the current stage");
        LvglPreview.RenderSnapshot(new("fixture", "BuildFailed", "PC 构建失败", false, false, "error: expected failure\n", null, null));
        Check(!LvglPreview.DiagnosticLog.Text.Contains("[进度]") && LvglPreview.DiagnosticLog.Text.Contains("[失败] PC 构建失败"),
            "PC failure stops active progress without inventing completion");
        var buffer = new OutputLineBuffer();
        var lines = new[] { "one\r", "\ntw", "o\n[1/", "4] compile\r", "\nwarning: notice\nlast tail" }.SelectMany(buffer.Append).ToArray();
        Check(lines.SequenceEqual(new[] { "one", "two", "[1/4] compile", "warning: notice" }) && buffer.Flush() == "last tail",
            "fragmented progress, split CRLF, complete diagnostics and unterminated tails lose no characters");
        var longLine = new string('x', 70000);
        Check(string.Concat(buffer.Append(longLine)) + buffer.Flush() == longLine, "bounded display chunks preserve an oversized raw line");
        ShowBottom(0);
        BottomRow.Height = new GridLength(300);
        BuildLog.Clear();
        BeginBuildOutput();
        Check(BuildLog.Text.Contains("[进度] 工程检查 [<=>") && !BuildLog.Text.Contains('%'), "stages without measurements use an inline activity bar without fabricated percentages");
        var preparationLines = BuildLog.Document.LineCount;
        foreach (var phase in new[] { "工程清单", "工程构建入口", "编译参数", "工程工具内容锁定" })
        {
            ReportBuildActivity(phase);
        }
        Check(BuildLog.Document.LineCount == preparationLines, "unmeasured preparation steps reuse one activity line without flooding output");
        ReportBuildActivity($"完整校验开发环境组件 {sourceManifest.ToolsetId} / {sourceManifest.ToolsetVersion}（10,512 个文件）…");
        var beforeUpdates = BuildLog.Document.LineCount;
        foreach (var completed in new[] { 322, 1009, 1723, 2445, 3167, 3930, 4695, 5495, 6264, 7036, 7853, 8603, 9314, 9843 })
        {
            ReportBuildActivity($"校验开发环境组件 {sourceManifest.ToolsetId} / {sourceManifest.ToolsetVersion}… {completed * 100 / 10512}%（{completed:N0}/10,512）");
        }
        Check(BuildLog.Document.LineCount == beforeUpdates && BuildLog.Text.Split('\n').Count(l => l.Contains("校验开发环境组件")) == 1 &&
            BuildLog.Text.Contains("9,843/10,512"), "fourteen verification reports replace exactly one terminal progress line");
        Log("-- Configuring done (0.0s)");
        Log("src/main.c:10: warning: sample warning diagnostic");
        Log("src/main.c:11: error 20: sample error diagnostic");
        Log("普通工具原始输出，路径、行号和诊断正文保持可复制。");
        Log("编译成功，退出代码：0");
        ReportBuildActivity($"校验开发环境组件 {sourceManifest.ToolsetId} / {sourceManifest.ToolsetVersion}… 91%（9,659/10,512）");
        Check(buildOutputMeasurement is { Percent: > 91 and < 92 } && BuildLog.Text.Contains("92% (9,659/10,512)") &&
            BuildLog.Text.Contains("src/main.c:10: warning: sample warning diagnostic") && BuildLog.Text.Contains("src/main.c:11: error 20: sample error diagnostic"),
            "component verification displays its reported file counts rather than an estimated whole-build percentage");
        var original = BuildLog.Text;
        foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light })
        {
            ApplyTheme(theme);
            await Layout();
            Check(((SolidColorBrush)LvglPreview.DiagnosticLog.Background).Color == ((SolidColorBrush)System.Windows.Application.Current.Resources["EditorSurface"]).Color,
                "PC output background follows the actual theme and keeps diagnostic text readable: " + theme.Id);
            var renderedColors = BuildLog.TextArea.TextView.VisualLines.SelectMany(l => l.Elements)
                .Select(e => e.TextRunProperties.ForegroundBrush).OfType<SolidColorBrush>().Select(b => b.Color).ToHashSet();
            foreach (var tone in new[] { OutputTone.Information, OutputTone.Success, OutputTone.Warning, OutputTone.Error })
            {
                Check(renderedColors.Contains(((SolidColorBrush)BuildLog.BrushFor(tone)).Color), "actual " + theme.Id + " glyph color for " + tone);
            }
            Check(BuildLog.Text == original, "theme changes preserve exact displayed text: " + theme.Id);
            CaptureOutput(Path.Combine(directory, "output-" + theme.Id + ".png"));
        }
        BuildLog.SelectAll();
        Check(BuildLog.SelectedText == original && BuildLog.IsReadOnly && ApplicationCommands.Copy.CanExecute(null, BuildLog.TextArea),
            "colored logs remain read-only and selectable for native plain-text copying");
        var font = BuildLog.FontSize;
        HandleTextMouseWheel(BuildLog, 120, true);
        Check(BuildLog.FontSize == font + 1, "Ctrl-wheel still changes only the output font");
        BuildLog.FontSize = font;
        ReportBuildActivity("编译固件");
        ReportBuildToolLine("[3/8] Building C object");
        Check(buildOutputMeasurement?.Percent == 37.5 && buildOutputPhase == "编译固件" && BuildLog.Text.Contains("38% (3/8)"), "Ninja counts show progress within the current compiler stage");
        ReportBuildToolLine("[8/8] Linking firmware");
        Check(buildOutputActive && buildOutputState == "构建中", "last Ninja step never declares build success before the real report");
        FinishBuildOutput("编译已取消", null);
        var cancelledText = BuildLog.Text;
        ReportBuildToolLine("[8/8] delayed output");
        Check(!buildOutputActive && !buildOutputTimer.IsEnabled && buildOutputState == "取消" && BuildLog.Text == cancelledText &&
            BuildLog.Text.TrimEnd().Split('\n').Last().Contains("[取消]"),
            "cancellation stops the indicator and late callbacks cannot revive it");
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            try
            {
                await RunBuildOutputOperationAsync("CMake 配置", (_, _) => Task.FromCanceled<BuildReport>(cancellation.Token),
                    report => report.Success, report => report.Summary, cancellation.Token);
                throw new InvalidOperationException("Cancelled operation unexpectedly completed.");
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                Check(!buildOutputActive && !buildOutputTimer.IsEnabled && buildOutputState == "取消",
                    "a cancelled independent operation drains output and stops its timer before propagating cancellation");
            }
        }
        BuildLog.Clear();
        BeginBuildOutput();
        Log(new string('x', 510000));
        ReportBuildActivity("校验开发环境组件 fixture.tool / 1.0.0… 50%（1/2）");
        Log("warning: retained after display truncation");
        ReportBuildActivity("校验开发环境组件 fixture.tool / 1.0.0… 100%（2/2）");
        Check(BuildLog.Text.Contains("较早日志已截断") && BuildLog.Text.Contains("warning: retained after display truncation") &&
            BuildLog.Text.Split('\n').Count(l => l.Contains("校验开发环境组件")) == 1, "bounded log truncation preserves the active anchor and all subsequent diagnostics");
        BuildLog.Clear();
        ReportBuildActivity("校验开发环境组件 fixture.tool / 1.0.0… 50%（1/2）");
        Check(BuildLog.Document.LineCount == 2 && BuildLog.Text.Contains("50% (1/2)"), "clearing a live output resets its progress anchor safely");
        FinishBuildOutput("fixture cleanup", null);
        ApplyTheme(ThemeService.Dark);
        var fixture = Path.Combine(directory, "fixture");
        foreach (var path in Directory.EnumerateFiles(sourceProject, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceProject, path).Replace('\\', '/');
            if (relative.StartsWith(".build/", StringComparison.Ordinal) || relative.StartsWith(".git/", StringComparison.Ordinal))
            {
                continue;
            }
            var destination = StudioX.Foundation.PathBoundary.Resolve(fixture, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(path, destination);
        }
        await OpenProjectAsync(fixture, CancellationToken.None);
        var manifest = await ProjectService.ReadAsync(fixture);
        var configured = await ConfigureWithOutputAsync(fixture, CancellationToken.None);
        Check(configured.Success && buildOutputState == "成功" && buildOutputPhase.StartsWith("CMake 配置成功", StringComparison.Ordinal),
            "standalone configuration uses real results and the shared output lifecycle: " + manifest.CompilerId);
        var main = activeEditor ?? throw new InvalidOperationException("Fixture main source missing.");
        main.Buffer.Insert(0, (manifest.ToolsetId == "stc.sdcc" ? "" : "#pragma GCC diagnostic warning \"-Wcpp\"\n") + "#warning STUDIOX_OUTPUT_WARNING\n");
        Build_Click(this, new RoutedEventArgs());
        await pendingOperation;
        Check(guideBuild is { Success: true } && !buildOutputActive && !buildOutputTimer.IsEnabled && buildOutputState == "成功" &&
            BuildLog.Text.Contains("STUDIOX_OUTPUT_WARNING") && BuildLog.Text.TrimEnd().EndsWith(guideBuild.Summary, StringComparison.Ordinal),
            "real build with a warning finishes from the actual report and preserves the raw warning before its final summary: " + manifest.CompilerId);
        await Layout();
        CaptureOutput(Path.Combine(directory, "real-build-success.png"));
        main.Buffer.Insert(0, "#error STUDIOX_OUTPUT_EXPECTED_FAILURE\n");
        Build_Click(this, new RoutedEventArgs());
        await pendingOperation;
        Check(guideBuild is { Success: false } && buildOutputState == "失败" && !buildOutputTimer.IsEnabled &&
            BuildLog.Text.Contains("STUDIOX_OUTPUT_EXPECTED_FAILURE"), "real compiler failure stays visibly failed and keeps its original diagnostic");
        await Layout();
        CaptureOutput(Path.Combine(directory, "real-build-failed.png"));
        await File.WriteAllTextAsync(Path.Combine(directory, "raw-display.log"), BuildLog.Text);
        await File.AppendAllTextAsync(Path.Combine(fixture, "CMakeLists.txt"), "\nstudiox_expected_configuration_failure()\n");
        var failedConfiguration = await ConfigureWithOutputAsync(fixture, CancellationToken.None);
        Check(!failedConfiguration.Success && buildOutputState == "失败" && BuildLog.Text.Contains("CMake Error"),
            "standalone configuration failure retains its original CMake diagnostic and cannot show success");
        if (manifest.ToolsetId == "arm.gnu")
        {
            var pack = await services.Packs.ImportAsync(Path.Combine(Directory.GetParent(services.RuntimeDirectory)!.FullName,
                "device-packs/AGM/studiox.preview.ag32vf303-0.1.5.mcupack"));
            var logic = Path.Combine(directory, "logic-fixture");
            await services.Projects.CreateAsync(pack, "AG32VF303CCT6", "freertos-mcu", "output_logic", logic, enableAg32Logic: true);
            await File.WriteAllTextAsync(Path.Combine(logic, "logic/user_logic.v"), "module output_logic(input wire data, output wire value); assign value=data; endmodule\n");
            Directory.CreateDirectory(Path.Combine(logic, "sim"));
            await File.WriteAllTextAsync(Path.Combine(logic, "sim/tb_output.v"), "`timescale 1ns/1ps\nmodule tb_output; reg flag=0; initial begin #1; flag=1; $display(\"STUDIOX_HDL_OUTPUT\"); #1; $finish; end endmodule\n");
            var settings = new HdlSimulationSettings(1, ["logic/user_logic.v"], ["logic"], [], "sim/tb_output.v", "tb_output", 10);
            var result = await RunBuildOutputOperationAsync("RTL 仿真", (progress, output) => services.HdlWorkflow.SimulateAsync(logic, settings,
                CancellationToken.None, progress, output), _ => true, _ => "RTL 仿真完成，波形已显示。", CancellationToken.None);
            Check(result.Waveform.Signals.Length > 0 && BuildLog.Text.Contains("STUDIOX_HDL_OUTPUT") && buildOutputState == "成功",
                "real Icarus process streams its output and completes the shared lifecycle after waveform validation");
            var schematicSettings = new HdlSchematicSettings(1, ["logic/user_logic.v"], ["logic"], [], "output_logic");
            var schematic = await RunBuildOutputOperationAsync("Verilog 综合", (progress, output) => services.HdlSchematic.GenerateAsync(logic,
                schematicSettings, CancellationToken.None, progress, output), _ => true, _ => "Verilog 综合成功，电路图已生成。", CancellationToken.None);
            Check(schematic.Modules.Length > 0 && BuildLog.Text.Contains("Executing") && buildOutputState == "成功",
                "real Yosys process streams its output and completes the shared lifecycle after netlist validation");
            await File.WriteAllTextAsync(Path.Combine(logic, "sim/tb_output.v"), "`timescale 1ns/1ps\nmodule tb_output; initial begin #1; $fatal(1, \"STUDIOX_HDL_EXPECTED_FAILURE\"); end endmodule\n");
            try
            {
                await RunBuildOutputOperationAsync("RTL 仿真", (progress, output) => services.HdlWorkflow.SimulateAsync(logic, settings,
                    CancellationToken.None, progress, output), _ => true, _ => "RTL 仿真完成", CancellationToken.None);
                throw new InvalidOperationException("Failing testbench unexpectedly succeeded.");
            }
            catch (StudioX.Foundation.StudioXException error) when (error.Code == "HDL_SIM_FAILED")
            {
                Check(buildOutputState == "失败" && !buildOutputTimer.IsEnabled && BuildLog.Text.Contains("STUDIOX_HDL_EXPECTED_FAILURE"),
                    "real Icarus assertion failure retains streamed diagnostics and stops the shared progress lifecycle");
            }
        }
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), $"PASS {checks.Count} output UI checks; {manifest.CompilerId} only in fixtures, no serial or hardware access.\n" + string.Join('\n', checks));
        async Task Layout()
        {
            UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            BuildLog.TextArea.TextView.EnsureVisualLines();
        }
        void CaptureOutput(string path)
        {
            var width = (int)Math.Ceiling(BottomPanel.ActualWidth);
            var height = (int)Math.Ceiling(BottomPanel.ActualHeight);
            var visual = new DrawingVisual();
            using (var drawing = visual.RenderOpen())
            {
                // 嵌套面板的布局偏移不属于截图坐标；用 VisualBrush 在原点重新绘制。
                drawing.DrawRectangle((Brush)System.Windows.Application.Current.Resources["Background"], null, new Rect(0, 0, width, height));
                drawing.DrawRectangle(new VisualBrush(BottomPanel), null, new Rect(0, 0, width, height));
            }
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var pixels = new byte[width * height * 4];
            bitmap.CopyPixels(pixels, width * 4, 0);
            var colors = new HashSet<int>();
            for (var i = 0; i < pixels.Length && colors.Count < 9; i += 4)
            {
                colors.Add(pixels[i] | pixels[i + 1] << 8 | pixels[i + 2] << 16);
            }
            Check(colors.Count >= 9, "captured output contains actual rendered content: " + Path.GetFileName(path));
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var destination = File.Create(path);
            encoder.Save(destination);
        }
    }
}
