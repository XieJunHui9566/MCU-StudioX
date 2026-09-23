namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Threading;

public partial class MainWindow
{
    /// <summary>隔离工程副本中验证构建刷新、失败清空与重开缓存；不连接芯片。</summary>
    public async Task RenderBuildMemoryPreviewAsync(string directory, string project)
    {
        var fixture = Path.Combine(directory, "fixture");
        if (Directory.Exists(fixture)) throw new InvalidOperationException("预览需要新的输出目录。");
        foreach (var path in Directory.EnumerateFiles(project, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(project, path).Replace('\\', '/');
            if (relative.StartsWith(".build/", StringComparison.Ordinal) || relative.StartsWith(".git/", StringComparison.Ordinal)) continue;
            var destination = Path.Combine(fixture, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(path, destination);
        }
        await OpenProjectAsync(fixture, CancellationToken.None);
        if (BuildMemory.RegionCount != 0) throw new InvalidOperationException("新工程显示了其他工程的数据。");
        await BuildAndCheck();
        await CloseProjectAsync(CancellationToken.None);
        if (BuildMemory.RegionCount != 0) throw new InvalidOperationException("关闭工程后遗留占用。");
        await OpenProjectAsync(fixture, CancellationToken.None);
        if (BuildMemory.RegionCount != 3) throw new InvalidOperationException("重开没有恢复已有构建占用。");
        var source = activeEditor!; var original = source.Buffer.Text;
        source.Buffer.Insert(0, "#error STUDIOX_EXPECTED_MEMORY_FAILURE\n");
        Build_Click(this, new RoutedEventArgs()); await pendingOperation;
        if (BuildMemory.RegionCount != 0 || !Status.Text.StartsWith("编译失败", StringComparison.Ordinal))
            throw new InvalidOperationException("失败后仍把旧占用显示为本次结果。");
        source.Buffer.Text = original;
        await BuildAndCheck();
        UpdateLayout(); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        Render(this, Path.Combine(directory, "build-memory.png"));
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), "PASS: real F407 build shows FLASH/RAM/CCM; close clears, reopen restores; compiler failure clears, next successful build restores. Only isolated fixture changed; no hardware access.\n");
        async Task BuildAndCheck()
        {
            Build_Click(this, new RoutedEventArgs()); await pendingOperation;
            if (Status.Text != "编译成功，退出代码：0" || BuildMemory.RegionCount != 3)
                throw new InvalidOperationException("构建分析器未更新：" + BuildLog.Text);
        }
    }
}
