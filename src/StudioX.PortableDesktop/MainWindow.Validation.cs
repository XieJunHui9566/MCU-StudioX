namespace StudioX.PortableDesktop;

using System.Text.Json;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

internal sealed partial class MainWindow
{
    private async Task ValidateWorkflowAsync(string directory, CancellationToken token)
    {
        // 显式验证参数只用于隔离工程；操作与按钮共享方法，不连接或下载芯片。
        var root = Path.GetFullPath(directory);
        Directory.CreateDirectory(root);
        if (document is null || document.Source.IsReadOnly)
        {
            throw new InvalidOperationException("验证工程没有可编辑的入口文件。");
        }
        var edited = editor.Text + "\n// Linux 编辑与保存流程验证\n";
        loadingDocument = false;
        editor.IsReadOnly = false;
        editor.Text = edited;
        if (!dirty)
        {
            throw new InvalidOperationException("编辑器没有记录修改。");
        }
        var mainBuffer = activeBuffer!;
        editor.CaretOffset = Math.Min(12, editor.Document.TextLength);
        await LoadDocumentAsync("CMakeLists.txt", token);
        var cmakeBuffer = activeBuffer!;
        if (buffers.Count != 2 || mainBuffer.Document.Text != edited || !mainBuffer.IsDirty)
        {
            throw new InvalidOperationException("切换标签丢失了源码修改。");
        }
        var cmakeOriginal = editor.Text;
        editor.Document.Insert(editor.Document.TextLength, "\n# Linux 多文件编辑验证\n");
        SelectBuffer(mainBuffer);
        if (activeBuffer != mainBuffer || editor.CaretOffset != Math.Min(12, mainBuffer.Document.TextLength) || editor.Text != edited)
        {
            throw new InvalidOperationException("标签切换没有保留文档与光标。");
        }
        SelectBuffer(cmakeBuffer);
        editor.Undo();
        if (editor.Text != cmakeOriginal || !mainBuffer.IsDirty)
        {
            throw new InvalidOperationException("标签撤销记录串入其他文件。");
        }
        editor.Redo();
        Avalonia.Threading.DispatcherTimer.RunOnce(() => OwnedWindows.FirstOrDefault()?.Close("cancel"), TimeSpan.FromMilliseconds(300));
        if (await ConfirmBuffersAsync(buffers, token) || buffers.Count != 2 || !mainBuffer.IsDirty || !cmakeBuffer.IsDirty)
        {
            throw new InvalidOperationException("取消批量关闭没有保留全部内存文档。");
        }
        await SaveAllAsync(token);
        SelectBuffer(mainBuffer);
        var saved = await workspace.ReadAsync(mainBuffer.Snapshot.Source.RelativePath, token);
        if (dirty || saved.Source.Text != edited)
        {
            throw new InvalidOperationException("编辑器保存与磁盘回读不一致。");
        }
        await BuildProjectAsync(token);
        if (lastBuild is not { Success: true })
        {
            throw new InvalidOperationException("桌面编译失败，请查看完整构建日志。");
        }
        await workspace.SelectThemeAsync(StudioX.Application.ThemeService.Light, token);
        ApplyTheme(await workspace.LoadThemeAsync(token));
        await Task.Delay(600, token);
        Capture("desktop-light.png");
        await workspace.SelectThemeAsync(StudioX.Application.ThemeService.Dark, token);
        ApplyTheme(await workspace.LoadThemeAsync(token));
        await RefreshWelcomeAsync(token);
        ShowWelcome();
        await Task.Delay(600, token);
        Capture("welcome.png");
        SelectBuffer(mainBuffer);
        ShowOutput(false);
        if (editorRegion.RowDefinitions[3].Height.Value != 0)
        {
            throw new InvalidOperationException("输出面板隐藏失败。");
        }
        ShowOutput(true);
        WindowState = Avalonia.Controls.WindowState.Maximized;
        await Task.Delay(400, token);
        if (Bounds.Width < 1100 || !documentLabel.IsEffectivelyVisible)
        {
            throw new InvalidOperationException("最大化后的工作区布局失效。");
        }
        Capture("desktop-maximized.png");
        WindowState = Avalonia.Controls.WindowState.Normal;
        File.WriteAllText(Path.Combine(root, "workflow.json"), JsonSerializer.Serialize(new
        {
            success = true,
            hardware = false,
            project = workspace.Project,
            savedDocument = saved.Source.RelativePath,
            diskHash = saved.Source.DiskHash,
            multiDocument = new
            {
                count = buffers.Count,
                isolatedUndo = true,
                retainedCaret = true,
                cancelClosePreservesBuffers = true,
                saveAll = true
            },
            themes = new[] { "studiox.dark", "studiox.light" },
            layout = new
            {
                windowsReference = true,
                sharedIcons = true,
                sharedThemeColors = true,
                resizablePanels = true,
                maximize = true
            },
            build = lastBuild
        }, new JsonSerializerOptions { WriteIndented = true }));
        DispatcherTimer.RunOnce(() =>
        {
            try
            {
                Capture("desktop.png");
                Console.WriteLine("LINUX_DESKTOP_WORKFLOW_PASS");
            }
            catch (Exception error)
            {
                WriteValidationFailure(error);
                Environment.ExitCode = 1;
            }
            Close();
        }, TimeSpan.FromSeconds(2));

        void Capture(string name)
        {
            using var bitmap = new RenderTargetBitmap(new PixelSize((int)Bounds.Width, (int)Bounds.Height), new Vector(96, 96));
            bitmap.Render((Avalonia.Visual)Content!);
            bitmap.Save(Path.Combine(root, name), new PngBitmapEncoderOptions());
        }
    }

    private void WriteValidationFailure(Exception error)
    {
        if (options.ValidationDirectory is not { } directory)
        {
            return;
        }
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "failure.log"), error.ToString());
        Console.Error.WriteLine(error);
    }
}
