namespace StudioX.Application;

using StudioX.Engine;

/// <summary>桌面与扩展共用的图形引脚规划入口，不持有界面或硬件会话。</summary>
public sealed class Ag32PinPlanningService(ToolsetCatalog tools)
{
    private readonly Ag32PinPlanService engine = new(tools);

    public Task<Ag32PinPlanSnapshot> ReadAsync(string projectDirectory, CancellationToken cancellationToken = default)
        => engine.ReadAsync(projectDirectory, cancellationToken);

    public Task<Ag32PinPlanResult> ApplyAsync(string projectDirectory, string expectedSourceSha256,
        IReadOnlyList<Ag32PinAssignment> assignments, Ag32PinClockSettings clocks, CancellationToken cancellationToken = default)
        => engine.ApplyAsync(projectDirectory, expectedSourceSha256, assignments, clocks, cancellationToken);

    public Task<Ag32PinPlanResult> ApplyAsync(string projectDirectory, Ag32PinPlanSnapshot expectedSnapshot,
        IReadOnlyList<Ag32PinAssignment> assignments, Ag32PinClockSettings clocks, CancellationToken cancellationToken = default)
        => engine.ApplyAsync(projectDirectory, expectedSnapshot, assignments, clocks, cancellationToken);
}
