namespace StudioX.Desktop;

using System.Windows;
using Microsoft.Win32;
using StudioX.Application.Simulation;

public partial class MainWindow
{
    private void InitializeSimulation()
    {
        services.Simulation.LogReceived += SimulationLogReceived;
        services.Simulation.PlotReceived += SimulationPlotReceived;
        services.Simulation.Diagnostic += SimulationDiagnostic;
    }

    private void SimulationLogReceived(SimulationLogFrame frame) => Dispatcher.BeginInvoke(() =>
    {
        if (closed || frame.Generation != services.Simulation.Generation)
        {
            return;
        }
        DeviceLog.AppendText($"#{frame.Sequence:D6}   " + frame.Text);
        if (DeviceLog.Text.Length > 12000)
        {
            DeviceLog.Text = DeviceLog.Text[^8000..];
        }
        DeviceLog.ScrollToEnd();
    });

    private void SimulationPlotReceived(SimulationPlotFrame frame) => Dispatcher.BeginInvoke(() =>
    {
        if (closed || frame.Generation != services.Simulation.Generation)
        {
            return;
        }
        Plot.Add(frame.Value, frame.State);
        FrameStatus.Text = $"模拟 · 帧 {frame.Sequence} · 丢帧 {frame.DroppedFrames}";
    });

    private void SimulationDiagnostic(string diagnostic) => Dispatcher.BeginInvoke(() =>
    {
        if (!closed)
        {
            Log(diagnostic);
        }
    });

    private async void Simulation_Click(object sender, RoutedEventArgs e) => await RunAsync(async _ =>
    {
        if (services.Simulation.IsConnected)
        {
            await StopSimulationAsync();
        }
        else
        {
            await StartSimulationAsync();
        }
    });

    private async Task StartSimulationAsync()
    {
        await services.Simulation.StartAsync();
        ShowBottom(1);
        Plot.Clear();
        DeviceLog.Clear();
        SimulationButton.Content = "断开模拟设备";
        RecordButton.IsEnabled = true;
        Status.Text = "模拟设备运行中 · 未连接真实串口";
    }

    private async Task StopSimulationAsync()
    {
        await services.Simulation.StopAsync();
        SimulationButton.Content = "连接模拟设备";
        RecordButton.IsEnabled = false;
        RecordButton.Content = "开始记录";
        FrameStatus.Text = "已断开";
    }

    private async void Record_Click(object sender, RoutedEventArgs e) => await RunAsync(async _ =>
    {
        if (services.Simulation.IsRecording)
        {
            await services.Simulation.StopCaptureAsync();
            RecordButton.Content = "开始记录";
            return;
        }
        if (!services.Simulation.IsConnected)
        {
            return;
        }
        var dialog = new SaveFileDialog
        {
            Filter = "StudioX 记录|*.sxcapture",
            FileName = "capture-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".sxcapture"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }
        await services.Simulation.StartCaptureAsync(dialog.FileName);
        RecordButton.Content = "停止记录";
        Status.Text = "记录中：" + dialog.FileName;
    });

    /// <summary>验证同一模拟连接的日志和绘图观察者；普通启动不自动建立连接。</summary>
    public async Task ExerciseSimulationAsync()
    {
        ShowDocument(LabTab);
        await StartSimulationAsync();
        await Task.Delay(1800);
        if (services.Simulation.LogFrames < 2 || services.Simulation.PlotFrames < 2)
        {
            throw new InvalidOperationException("模拟订阅未收到数据。");
        }
        await StopSimulationAsync();
    }
}
