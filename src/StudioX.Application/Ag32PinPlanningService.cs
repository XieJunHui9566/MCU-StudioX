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

    public static string PreviewUart(Ag32UartSettings uart, IReadOnlyList<Ag32PinAssignment> pins, Ag32PinClockSettings clocks, Ag32Setup? setup = null)
    {
        return Ag32UartInitialization.InitCode(uart, pins, PlannedPeripheralClockHz(clocks), setup);
    }

    public Task<Ag32PinPlanResult> ApplyAsync(string root, Ag32PinPlanSnapshot snapshot,
        IReadOnlyList<Ag32PinAssignment> pins, Ag32PinClockSettings clocks, Ag32AnalogSettings analog,
        IReadOnlyList<Ag32UartSettings> uarts, CancellationToken token)
        => engine.ApplyAsync(root, snapshot, pins, clocks, analog, uarts, token);

    public Task<Ag32PinPlanResult> ApplyAsync(string root, Ag32PinPlanSnapshot snapshot,
        IReadOnlyList<Ag32PinAssignment> pins, Ag32PinClockSettings clocks, Ag32AnalogSettings analog,
        IReadOnlyList<Ag32UartSettings> uarts, IReadOnlyList<Ag32BusSettings> buses, CancellationToken token)
        => engine.ApplyAsync(root, snapshot, pins, clocks, analog, uarts, buses, token);

    public Task<Ag32PinPlanResult> ApplyAsync(string root, Ag32PinPlanSnapshot snapshot,
        IReadOnlyList<Ag32PinAssignment> pins, Ag32PinClockSettings clocks, Ag32AnalogSettings analog,
        IReadOnlyList<Ag32UartSettings> uarts, IReadOnlyList<Ag32BusSettings> buses,
        IReadOnlyList<Ag32TimerSettings> timers, CancellationToken token)
        => engine.ApplyAsync(root, snapshot, pins, clocks, analog, uarts, buses, timers, token);

    public Task<Ag32PinPlanResult> ApplyAsync(string root, Ag32PinPlanSnapshot snapshot,
        IReadOnlyList<Ag32PinAssignment> pins, Ag32PinClockSettings clocks, Ag32AnalogSettings analog,
        IReadOnlyList<Ag32UartSettings> uarts, IReadOnlyList<Ag32BusSettings> buses, IReadOnlyList<Ag32TimerSettings> timers,
        Ag32SystemPeripherals peripherals, CancellationToken token)
        => engine.ApplyAsync(root, snapshot, pins, clocks, analog, uarts, buses, timers, peripherals, token);

    public static void ValidateSystemPeripherals(Ag32SystemPeripherals settings, Ag32PinClockSettings clocks)
        => settings.Validate(PlannedPeripheralClockHz(clocks));

    public async Task<(string Path, int Offset, string Text, string Append)> LocateCallbackAsync(string root, string main,
        string name, IReadOnlyDictionary<string, string> buffers, ProjectFileService files, CancellationToken token)
    {
        var module = Ag32CallbackSource.ModuleFile(name);
        var generated = await files.ReadAsync(root, module, token).ConfigureAwait(false);
        var (_, signature) = Ag32CallbackSource.Resolve(name, generated.Text);
        var index = await new Editing.WorkspaceDiscoveryService(files).CreateIndexAsync(root, token).ConfigureAwait(false);
        async Task<string> Source(string path) => buffers.TryGetValue(path, out var text) ? text :
            (await files.ReadAsync(root, path, token).ConfigureAwait(false)).Text;
        foreach (var path in index.Paths.Prepend(main).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Path.GetExtension(path).ToLowerInvariant() is not (".c" or ".cpp" or ".cc" or ".cxx") || path.StartsWith("device/sdk/", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("device/studiox/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var text = await Source(path).ConfigureAwait(false);
            if (!text.Contains(name, StringComparison.Ordinal) || Ag32SystemSupport.IsManagedFile(path, text))
            {
                continue;
            }
            var offset = Ag32CallbackSource.Find(text, name);
            if (offset >= 0)
            {
                return (path, offset, text, "");
            }
        }
        var original = await Source(main).ConfigureAwait(false);
        var nl = original.Contains("\r\n") ? "\r\n" : "\n";
        // 关闭图形外设后 main 的受管 include 会移除，用户回调仍需 SDK 类型定义。
        var include = original.Contains("#include \"alta.h\"", StringComparison.Ordinal) ||
            original.Contains("#include \"StudioX_System.h\"", StringComparison.Ordinal) ? "" : $"#include \"alta.h\"{nl}{nl}";
        var linkage = main.EndsWith(".c", StringComparison.OrdinalIgnoreCase) ? "" : "extern \"C\" ";
        var addition = $"{nl}{nl}{include}{linkage}{signature}{nl}{{{nl}    /* 用户中断处理 */{nl}}}{nl}";
        return (main, original.Length + addition.IndexOf("/*", StringComparison.Ordinal), original, addition);
    }

    public static string PreviewTimer(Ag32TimerSettings timer, IReadOnlyList<Ag32PinAssignment> pins,
        Ag32PinClockSettings clocks, out string timing, Ag32Setup? setup = null)
        => Ag32TimerInitialization.InitCode(timer, pins, PlannedPeripheralClockHz(clocks), out timing, setup);

    public static string PreviewBus(Ag32BusSettings bus, IReadOnlyList<Ag32PinAssignment> pins,
        Ag32PinClockSettings clocks, out string timing, Ag32Setup? setup = null)
    {
        var hz = PlannedPeripheralClockHz(clocks);
        timing = Ag32BusInitialization.Describe(bus, hz, setup);
        return Ag32BusInitialization.InitCode(bus, pins, hz, setup);
    }

    private static uint PlannedPeripheralClockHz(Ag32PinClockSettings clocks)
    {
        var mhz = clocks.BusMhz is > 0 ? clocks.BusMhz.Value : clocks.SysMhz ?? 10m;
        if (mhz is < 0.000001m or > 248m)

        {
            throw new StudioX.Foundation.StudioXException("AG32_SYSTEM_CLOCK", "外设时钟超出范围，请检查 SYSCLK 和 BUSCLK。");
        }
        return (uint)(mhz * 1000000m);
    }

    public static Ag32AnalogPin[] GetAnalogPins(string deviceId) => Ag32PeripheralSupport.Pins(deviceId);
    public static Ag32AnalogPin[] GetReservedAnalogPins(string deviceId, Ag32AnalogSettings settings)
        => Ag32PeripheralSupport.ReservedPins(deviceId, settings);
    public static Ag32AnalogPin[] GetReservedAnalogPins(string deviceId, Ag32AnalogSettings settings, Ag32Setup setup)
        => Ag32PeripheralSupport.ReservedPins(deviceId, settings, setup);
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
