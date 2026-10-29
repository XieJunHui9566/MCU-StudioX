namespace StudioX.Desktop;

using System.Windows.Threading;
using StudioX.Application;
using StudioX.Foundation;

public partial class MainWindow
{
    public async Task RenderMx251MemoryPreviewAsync(string output, string project)
    {
        var checks = new List<string>();
        void Check(bool ok, string name)
        {
            if (!ok)
            {
                throw new InvalidOperationException(name);
            }
            checks.Add(name);
        }
        await OpenProjectAsync(project, default);
        var rootNode = ProjectTree.Items[0];
        var sourceNode = await FindProjectNodeAsync("src");
        var mainNode = await FindProjectNodeAsync("src/main.c");
        mainNode!.IsSelected = true;
        var firstBuild = !Directory.Exists(Path.Combine(project, ".build"));
        if (firstBuild)
        {
            Check(await FindProjectNodeAsync(".build") is null, "fresh project initially has no build directory in its tree");
        }
        var before = (await services.BuildHistory.ListAsync(project)).Count;
        var result = await BuildWithSummaryAsync(project, default);
        Check(result.Success && BuildMemory.RegionCount == 3, "actual desktop build displays three MX251 memory regions");
        Check((await services.BuildHistory.ListAsync(project)).Count == before + 1, "desktop build automatically saves history");
        Check(!BuildLog.Text.Contains("构建历史保存失败"), "no history-memory failure in actual desktop log");
        Check(await FindProjectNodeAsync(".build/firmware.hex") is not null,
            "actual build exposes firmware in project tree without reopening or F5");
        Check(ReferenceEquals(rootNode, ProjectTree.Items[0]) && ReferenceEquals(sourceNode, await FindProjectNodeAsync("src")) &&
            ReferenceEquals(mainNode, await FindProjectNodeAsync("src/main.c")) && sourceNode!.IsExpanded && mainNode.IsSelected,
            "build reconciliation preserves existing nodes, expansion and selection");
        await File.WriteAllTextAsync(Path.Combine(output, "desktop-build.log"), BuildLog.Text);
        foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light })
        {
            ApplyTheme(theme);
            UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            Render(BuildMemory, Path.Combine(output, "mx251-memory-" + theme.Id + ".png"));
        }
        Render(this, Path.Combine(output, "build-tree-first-build.png"));
        await ShowBuildHistoryAsync();
        UpdateLayout();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        Render(buildHistoryView!, Path.Combine(output, "mx251-history.png"));
        await CloseProjectAsync(default);
        Check(BuildMemory.RegionCount == 0, "closing clears memory panel");
        await OpenProjectAsync(project, default);
        Check(BuildMemory.RegionCount == 3, "reopening restores verified memory without recompiling");
        await File.WriteAllTextAsync(Path.Combine(project, ".build/cancelled-preview.log"), "isolated pre-cancelled build fixture\n");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            await BuildWithSummaryAsync(project, cancelled.Token);
            throw new InvalidOperationException("Pre-cancelled build was not cancelled.");
        }
        catch (OperationCanceledException) when (cancelled.IsCancellationRequested) { }
        Check(await FindProjectNodeAsync(".build/cancelled-preview.log") is not null,
            "cancelled build still reconciles generated directories without using the cancelled build token");
        await JsonStore.WriteAsync(Path.Combine(output, "result.json"), new
        {
            passed = true,
            checks,
            hardware = false
        });
    }
}
