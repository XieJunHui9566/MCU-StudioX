namespace StudioX.Application;

using StudioX.Engine;

/// <summary>桌面与扩展共用的图形引脚规划入口，不持有界面或硬件会话。</summary>
public sealed class Ag32PinPlanningService(ToolsetCatalog tools)
{
    private readonly Ag32PinPlanService engine = new(tools);
    private readonly Ag32TimingService timing = new(tools);

    public Task<Ag32TimingStatus> ReadTimingAsync(string projectDirectory, CancellationToken token = default)
        => timing.ReadAsync(projectDirectory, token);
    public static string? ValidateClocks(string deviceId, Ag32PinClockSettings clocks) => Ag32ClockPolicy.Validate(deviceId, clocks);
    public static string ClockGuidance(string deviceId, Ag32PinClockSettings clocks, Ag32AnalogSettings analog)
        => Ag32ClockPolicy.Guidance(deviceId, clocks, analog);
    public static Ag32ClockRecommendation[] ClockRecommendations(string deviceId, Ag32PinClockSettings clocks, Ag32AnalogSettings analog)
        => Ag32ClockPolicy.Recommendations(deviceId, clocks, analog);

    public static Ag32AnalogPin[] GetAnalogPins(string deviceId) => Ag32PeripheralSupport.Pins(deviceId);
    public static Ag32AnalogPin[] GetReservedAnalogPins(string deviceId, Ag32AnalogSettings settings)
        => Ag32PeripheralSupport.ReservedPins(deviceId, settings);
    public static void ValidateAnalogSettings(string deviceId, Ag32AnalogSettings settings)
        => Ag32PeripheralSupport.ValidateSettings(deviceId, settings);

    public Task<Ag32PinPlanSnapshot> ReadAsync(string projectDirectory, CancellationToken cancellationToken = default)
        => engine.ReadAsync(projectDirectory, cancellationToken);

    public Task<Ag32PinPlanResult> ApplyAsync(string projectDirectory, string expectedSourceSha256,
        IReadOnlyList<Ag32PinAssignment> assignments, Ag32PinClockSettings clocks, CancellationToken cancellationToken = default)
        => engine.ApplyAsync(projectDirectory, expectedSourceSha256, assignments, clocks, cancellationToken);

    public Task<Ag32PinPlanResult> ApplyAsync(string projectDirectory, Ag32PinPlanSnapshot expectedSnapshot,
        IReadOnlyList<Ag32PinAssignment> assignments, Ag32PinClockSettings clocks, CancellationToken cancellationToken = default)
        => engine.ApplyAsync(projectDirectory, expectedSnapshot, assignments, clocks, cancellationToken);

    public Task<Ag32PinPlanResult> ApplyAsync(string projectDirectory, Ag32PinPlanSnapshot expectedSnapshot,
        IReadOnlyList<Ag32PinAssignment> assignments, Ag32PinClockSettings clocks, Ag32AnalogSettings analog,
        CancellationToken cancellationToken = default)
        => engine.ApplyAsync(projectDirectory, expectedSnapshot, assignments, clocks, analog, cancellationToken);
}
