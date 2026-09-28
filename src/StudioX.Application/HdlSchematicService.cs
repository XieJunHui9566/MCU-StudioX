namespace StudioX.Application;

using StudioX.Engine.Hdl;

/// <summary>提供 AG32 HDL 电路预览用例；界面和 Agent 共用同一综合、输入校验及快照契约。</summary>
public sealed class HdlSchematicService(string runtimeDirectory)
{
    private readonly HdlSchematicEngine engine = new(runtimeDirectory);
    public Task<HdlSchematicSettings> ReadSettingsAsync(string project, CancellationToken token = default) =>
        Task.Run(() => engine.ReadSettingsAsync(project, token), token);
    public Task SaveSettingsAsync(string project, HdlSchematicSettings settings, CancellationToken token = default) => engine.SaveSettingsAsync(project, settings, token);
    public Task<HdlSchematicResult> GenerateAsync(string project, HdlSchematicSettings settings, CancellationToken token = default) =>
        Task.Run(() => engine.GenerateAsync(project, settings, token), token);
    public Task<bool> IsCurrentAsync(HdlSchematicResult result, CancellationToken token = default) =>
        Task.Run(() => engine.IsCurrentAsync(result, token), token);
    public HdlDiagram CreateDiagram(HdlModule module) => HdlDiagramLayout.Create(module);
    public Task ExportSvgAsync(HdlDiagram diagram, string path, CancellationToken token = default) =>
        File.WriteAllTextAsync(path, HdlDiagramSvg.Write(diagram), token);
}
