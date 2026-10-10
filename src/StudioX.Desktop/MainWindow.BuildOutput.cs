namespace StudioX.Desktop;

using System.Diagnostics;
using System.Windows.Threading;
using StudioX.Application.Output;

public partial class MainWindow
{
    private readonly Stopwatch buildOutputClock = new();
    private readonly DispatcherTimer buildOutputTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool buildOutputActive;
    private string buildOutputPhase = "";
    private string buildOutputState = "就绪";
    private OutputMeasurement? buildOutputMeasurement;
    private DateTime buildOutputTimestamp;
    private int buildOutputFrame;
    private void InitializeBuildOutput()
    {
        buildOutputTimer.Tick += (_, _) => { if (buildOutputActive) { buildOutputFrame++; RenderBuildProgress(); } };
        Closed += (_, _) => buildOutputTimer.Stop();
    }
    private void BeginBuildOutput()
    {
        BuildLog.CommitProgress();
        buildOutputActive = true;
        buildOutputPhase = "";
        buildOutputMeasurement = null;
        buildOutputState = "构建中";
        buildOutputFrame = 0;
        buildOutputClock.Restart();
        buildOutputTimer.Start();
        ReportBuildActivity("工程检查");
    }
    private void ReportBuildActivity(string text)
    {
        if (!buildOutputActive)
        {
            return;
        }
        var phase = TerminalProgressFormatter.Phase(text);
        if (phase != buildOutputPhase)
        {
            // 无计数的准备步骤复用活动行，避免健康检查的每个小步骤再刷一行。
            if (buildOutputMeasurement is not null)
            {
                RenderBuildProgress("阶段");
                BuildLog.CommitProgress();
            }
            buildOutputTimestamp = DateTime.Now;
            buildOutputPhase = phase;
        }
        buildOutputMeasurement = BuildOutputParser.Measure(text);
        RenderBuildProgress();
    }
    private void ReportBuildToolLine(string text)
    {
        if (!buildOutputActive)
        {
            return;
        }
        if (BuildOutputParser.Measure(text) is { } measurement)
        {
            buildOutputMeasurement = measurement;
            RenderBuildProgress();
        }
    }
    private void RenderBuildProgress(string state = "进度")
    {
        BuildLog.UpdateProgress($"[{buildOutputTimestamp:HH:mm:ss}] " + TerminalProgressFormatter.Render(
            buildOutputPhase, buildOutputMeasurement, buildOutputClock.Elapsed, buildOutputFrame, state));
    }
    private void FinishBuildOutput(string message, bool? success)
    {
        buildOutputActive = false;
        buildOutputTimer.Stop();
        buildOutputClock.Stop();
        buildOutputPhase = message;
        buildOutputState = success switch
        {
            true => "成功",
            false => "失败",
            null => "取消"
        };
        // 最终状态取实际报告；不把任务计数包装成整个构建的 100%。
        BuildLog.UpdateProgress($"[{DateTime.Now:HH:mm:ss}] [{buildOutputState}] {message} · 耗时 {buildOutputClock.Elapsed:hh\\:mm\\:ss}");
        BuildLog.CommitProgress();
        BuildLog.ScrollToEnd();
    }
}
