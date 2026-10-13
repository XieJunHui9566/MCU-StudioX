namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using StudioX.Application;
using StudioX.Application.Output;
using StudioX.Engine;

public partial class MainWindow
{
    /// <summary>只回放 ISP 输出并检查实际日志视图；不调用下载服务或打开串口。</summary>
    public async Task RenderStcDownloadOutputPreviewAsync(string directory)
    {
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
        Check(BuildOutputParser.Tone("[01:17:53] STC ISP 下载成功，工具已完成写入并退出") == OutputTone.Success &&
            BuildOutputParser.Tone("STC 下载成功 · IAP15F2K61S2 · COM15") == OutputTone.Success,
            "both STC success messages receive the success color");
        Check(BuildOutputParser.Tone("STC ISP 未完成；请查看原始日志") == OutputTone.Error &&
            BuildOutputParser.Tone("STC ISP 等待或写入超时；请查看日志") == OutputTone.Error &&
            BuildOutputParser.Tone("STC 下载已取消；板内程序可能不完整") == OutputTone.Warning,
            "failure, timeout and cancellation retain distinct colors");
        foreach (var invalid in new[] { "STUDIOX_ISP_PROGRESS 1 0", "STUDIOX_ISP_PROGRESS 2 1", "STUDIOX_ISP_PROGRESS -1 512",
            "STUDIOX_ISP_PROGRESS 999999999999999999999999 512", "STUDIOX_ISP_PROGRESS 64 512 trailing" })
        {
            Check(StcIspOutputParser.Activity(invalid) is null, "invalid progress stays raw: " + invalid);
        }
        ShowBottom(0);
        BottomRow.Height = new GridLength(360);
        BuildLog.Clear();
        BeginBuildOutput("等待 MCU 上电；请按开发板电源/下载键", "下载中");
        Check(!BuildLog.Text.Contains('%'), "waiting for power uses activity without an estimated percentage");
        var buffer = new OutputLineBuffer();
        void Feed(string chunk)
        {
            foreach (var line in buffer.Append(chunk))
            {
                PresentStcToolLine(line);
            }
        }
        Feed("Waiting for MCU, please cycle power: done\r");
        Feed("\nProtocol detected: stc15\r\nSTUDIOX_TARGET_VERIFIED IAP15F2K61S2\r\nErasing flash: done\r\nSTUDIOX_ISP_PRO");
        Feed("GRESS 64 512\r");
        Feed("\n");
        var initialCount = BuildLog.Document.LineCount;
        Feed("STUDIOX_ISP_PROGRESS 128 512\r\nSTUDIOX_ISP_PROGRESS 192 512\n");
        Check(BuildLog.Document.LineCount == initialCount && BuildLog.Text.Contains("38% (192/512)"),
            "fragmented progress and CRLF refresh one character bar using acknowledged bytes");
        Feed("warning: retained download notice\nSTUDIOX_ISP_PROGRESS 256 512\nSTUDIOX_ISP_PROGRESS 320 512\nSTUDIOX_ISP_PROGRESS 384 512\nSTUDIOX_ISP_PROGRESS 448 512\nSTUDIOX_ISP_PROGRESS 512 512\n");
        Check(BuildLog.Text.Split('\n').Count(line => line.Contains("写入代码 Flash")) == 1 &&
            !BuildLog.Text.Contains("STUDIOX_ISP_PROGRESS") && BuildLog.Text.Contains("warning: retained download notice"),
            "eight block acknowledgements coalesce without removing warnings or displaying telemetry");
        Check(buildOutputActive && buildOutputState == "下载中" && BuildLog.Text.Contains("100% (512/512)"),
            "100 percent transferred cannot declare operation success");
        await Layout();
        Capture("download-progress.png");
        Feed("Finishing write: done\nSetting options: done\nSTUDIOX_PROGRAM_COMPLETE\nDisconnecting: done");
        if (buffer.Flush() is { } tail)
        {
            PresentStcToolLine(tail);
        }
        Check(BuildLog.Text.Contains("Disconnecting: done") && buildOutputMeasurement is null,
            "unterminated output survives and finishing resets byte measurement");
        var prepared = new StcIspPreparation(directory, "IAP15F2K61S2", 62464, "COM15", "firmware.hex", "snapshot.hex",
            "fixture", 271, 270, "unused", "unused", Path.Combine(directory, "fixture-stcgal.log"), new());
        var report = new StcIspReport(true, "fixture", prepared.LogPath, 0, false, true);
        var windowCount = System.Windows.Application.Current.Windows.Count;
        PresentStcDownloadResult(prepared, report);
        Check(System.Windows.Application.Current.Windows.Count == windowCount && !buildOutputActive && !buildOutputTimer.IsEnabled &&
            Status.Text.Contains("STC 下载成功") && BuildLog.Text.TrimEnd().Contains("[成功] STC ISP 下载成功") &&
            BuildLog.Text.Contains("未执行 Flash 读回校验") && BuildLog.Text.Contains("下载日志："),
            "completed download displays one final report, elapsed time and log path without a success window");
        var original = BuildLog.Text;
        foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light })
        {
            ApplyTheme(theme);
            await Layout();
            var successColor = ((SolidColorBrush)BuildLog.BrushFor(OutputTone.Success)).Color;
            Check(BuildLog.TextArea.TextView.VisualLines.SelectMany(line => line.Elements)
                .Any(element => element.TextRunProperties.ForegroundBrush is SolidColorBrush brush && brush.Color == successColor),
                "actual success glyphs use green in " + theme.Id);
            Check(BuildLog.Text == original, "theme switch preserves copyable text in " + theme.Id);
            Capture("download-success-" + theme.Id + ".png");
        }
        BuildLog.SelectAll();
        Check(BuildLog.IsReadOnly && BuildLog.SelectedText == original, "colored download output remains selectable and read-only");
        BuildLog.Clear();
        BeginBuildOutput("写入代码 Flash", "下载中");
        PresentStcToolLine("STUDIOX_ISP_PROGRESS 512 512");
        PresentStcToolLine("Serial port error: incorrect magic in finish packet");
        PresentStcDownloadResult(prepared, report with
        {
            Success = false,
            ExitCode = 1
        });
        Check(buildOutputState == "失败" && !buildOutputTimer.IsEnabled && !BuildLog.Text.Contains("[成功]") &&
            BuildLog.Text.Contains("incorrect magic in finish packet") && BuildLog.Text.Contains("[失败]"),
            "failure after complete byte transfer preserves diagnostics and never reports success");
        BeginBuildOutput("等待 MCU 上电", "下载中");
        FinishBuildOutput("STC 下载已取消", null);
        var cancelled = BuildLog.Text;
        ReportBuildActivity("写入代码 Flash", new(512, 512));
        Check(buildOutputState == "取消" && !buildOutputTimer.IsEnabled && BuildLog.Text == cancelled,
            "late progress cannot restart the cancelled activity indicator");
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), $"PASS {checks.Count} STC output checks; no serial or hardware access.\n" + string.Join('\n', checks));

        async Task Layout()
        {
            UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            BuildLog.TextArea.TextView.EnsureVisualLines();
        }
        void Capture(string name)
        {
            var width = (int)Math.Ceiling(BottomPanel.ActualWidth);
            var height = (int)Math.Ceiling(BottomPanel.ActualHeight);
            var visual = new DrawingVisual();
            using (var drawing = visual.RenderOpen())
            {
                drawing.DrawRectangle((Brush)System.Windows.Application.Current.Resources["Background"], null, new Rect(0, 0, width, height));
                drawing.DrawRectangle(new VisualBrush(BottomPanel), null, new Rect(0, 0, width, height));
            }
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(directory, name));
            encoder.Save(file);
        }
    }
}
