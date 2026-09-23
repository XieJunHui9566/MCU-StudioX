namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Threading;

public partial class MainWindow
{
    /// <summary>只编译隔离副本，验证桌面保存、构建、原始诊断和随包工具；不访问硬件。</summary>
    public async Task RenderBuildPreviewAsync(string directory, string project)
    {
        var fixture = Path.Combine(directory, "fixture");
        if (Directory.Exists(fixture)) throw new InvalidOperationException("构建预览需要新的输出目录。");
        foreach (var path in Directory.EnumerateFiles(project, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(project, path).Replace('\\', '/');
            if (relative.StartsWith(".build/", StringComparison.Ordinal) || relative.StartsWith(".git/", StringComparison.Ordinal)) continue;
            var target = Path.Combine(fixture, relative); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(path, target);
        }
        await OpenProjectAsync(fixture, CancellationToken.None);
        var main = activeEditor ?? throw new InvalidOperationException("主文件未打开。");
        var original = main.Buffer.Text;
        main.Buffer.Insert(0, "// 自动保存与内置工具链构建检查\n");
        Build_Click(this, new RoutedEventArgs()); await pendingOperation;
        if (Status.Text != "编译成功，退出代码：0" || !BuildLog.Text.TrimEnd().EndsWith(Status.Text, StringComparison.Ordinal) || main.IsDirty || !File.Exists(Path.Combine(fixture, ".build/firmware.elf")))
            throw new InvalidOperationException("桌面构建失败：" + BuildLog.Text);
        await Layout(); Render(this, Path.Combine(directory, "build-success.png"));
        main.Buffer.Insert(0, "#error STUDIOX_EXPECTED_DIAGNOSTIC\n");
        Build_Click(this, new RoutedEventArgs()); await pendingOperation;
        if (!Status.Text.StartsWith("编译失败，退出代码：", StringComparison.Ordinal) || !BuildLog.Text.TrimEnd().EndsWith(Status.Text, StringComparison.Ordinal) || !BuildLog.Text.Contains("STUDIOX_EXPECTED_DIAGNOSTIC", StringComparison.Ordinal))
            throw new InvalidOperationException("编译错误没有传回界面。");
        await File.WriteAllTextAsync(Path.Combine(directory, "expected-failure.log"), BuildLog.Text);
        main.Buffer.Text = original;
        await SaveEditorAsync(fixture, main, CancellationToken.None);
        VerifyTools_Click(this, new RoutedEventArgs()); await pendingOperation;
        if (ToolInventory.Text.Contains("检查失败", StringComparison.Ordinal) || ToolInventory.Text.Split('✓').Length != 4)
            throw new InvalidOperationException("发行工具集检查失败：" + ToolInventory.Text);
        await File.WriteAllTextAsync(Path.Combine(directory, "inventory.txt"), ToolInventory.Text);
        await Layout(); Render(this, Path.Combine(directory, "tool-inventory.png"));
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), "PASS: desktop saves dirty source before build; AG32 ELF/BIN/HEX/MAP; compiler errors retained; all three bundled toolsets pass hashes and executable startup. Isolated fixture only; no hardware access.\n");
        async Task Layout() { UpdateLayout(); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render); }
    }
}
