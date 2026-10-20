namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using StudioX.Application.Arduino;
using StudioX.Engine;
using StudioX.Engine.Arduino;

public partial class MainWindow
{
    private bool IsArduinoProject => currentProjectManifest?.Kind == ProjectKind.Arduino;

    private void RefreshArduinoTemplateSelection()
    {
        if (ArduinoTemplatePanel is null || ArduinoBoardPicker is null)
        {
            return;
        }
        var selected = ArduinoTemplateCatalog.IsArduino(TemplatePicker.SelectedItem as StudioX.Packages.ProjectTemplate);
        ArduinoTemplatePanel.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        ArduinoBoardPicker.ItemsSource = selected && DevicePicker.SelectedItem is StudioX.Packages.DeviceDefinition device
            ? ArduinoBoard.ForDevice(device) : null;
        ArduinoBoardPicker.SelectedIndex = -1;
        ArduinoTemplateDescription.Text = ArduinoTemplateCatalog.Description(null);
        if (selected)
        {
            CreateProjectButton.IsEnabled = false;
        }
    }

    private void ArduinoBoardPicker_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (ArduinoTemplateDescription is null || CreateProjectButton is null)
        {
            return;
        }
        var board = ArduinoBoardPicker.SelectedItem as ArduinoBoard;
        ArduinoTemplateDescription.Text = ArduinoTemplateCatalog.Description(board);
        if (ArduinoTemplateCatalog.IsArduino(TemplatePicker.SelectedItem as StudioX.Packages.ProjectTemplate))
        {
            CreateProjectButton.IsEnabled = board is not null;
        }
        UpdateSelectedComponents();
    }

    private async void ImportArduino_Click(object sender, RoutedEventArgs args)
    {
        var source = new OpenFolderDialog { Title = "选择包含同名 .ino 的 sketch 目录；原工程保持不变" };
        if (source.ShowDialog(this) == true)
        {
            await CreateArduinoAsync(source.FolderName);
        }
    }

    private async Task CreateArduinoAsync(string? source)
    {
        var dialog = new ArduinoProjectWindow(services.Arduino.Boards, source is null ? null : Path.GetFileName(source)) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }
        var parent = new OpenFolderDialog { Title = "选择 Arduino 工程的保存位置" };
        if (parent.ShowDialog(this) != true)
        {
            return;
        }
        await RunAsync(async token =>
        {
            var root = Path.Combine(parent.FolderName, dialog.ProjectName);
            await services.Arduino.CreateAsync(dialog.ProjectName, dialog.BoardId, root, source, token);
            await OpenProjectAsync(root, token);
        });
    }

    private async void ArduinoManager_Click(object sender, RoutedEventArgs args) => await RunAsync(async token =>
    {
        var root = RequireProject();
        if (!IsArduinoProject)
        {
            Status.Text = "请先新建或打开 Arduino 工程。";
            return;
        }
        await ShowArduinoManagerAsync(root, token);
    });

    private async Task ShowArduinoManagerAsync(string root, CancellationToken token)
    {
        await SaveAllSourcesAsync(root, token);
        var project = await ProjectService.ReadAsync(root, token);
        new ArduinoManagerWindow(services.Arduino, root, project) { Owner = this }.ShowDialog();
        SetProjectDetailsMode(await ProjectService.ReadAsync(root, token));
        ClearBuildDiagnostics();
        try
        {
            await services.Intelligence.StartAsync(root, token);
            QueueLiveDiagnostics();
            Status.Text = "Arduino 工程依赖已刷新。";
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            await RecordAnalysisFailureAsync("Arduino 库更新后的代码分析", error);
        }
    }

    private async Task DownloadArduinoAsync(string root, CancellationToken token)
    {
        await SaveAllSourcesAsync(root, token);
        ShowBottom(0);
        BuildLog.Clear();
        var report = await BuildWithSummaryAsync(root, token);
        if (!report.Success)
        {
            return;
        }
        var preview = await services.Arduino.PreviewUploadAsync(root, token);
        var dialog = new ArduinoUploadWindow(preview) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            Status.Text = "上传已取消；固件编译结果已保留。";
            return;
        }
        var result = await services.Arduino.UploadApprovedAsync(root, dialog.Port, preview.ApprovalId, new Progress<string>(Log), token);
        Log(result.StandardOutput);
        Log(result.StandardError);
        Status.Text = result.ExitCode == 0 && !result.TimedOut ? "Arduino 上传完成并校验。" : $"Arduino 上传失败，退出码 {result.ExitCode}；原始诊断见输出。";
    }
}
