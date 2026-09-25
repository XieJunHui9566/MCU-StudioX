namespace StudioX.Application;

using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using StudioX.Engine;
using StudioX.Engine.Debugging;
using StudioX.Foundation;
using StudioX.Packages;

public sealed record DebugPreferences(int FormatVersion, SourceBreakpoint[] Breakpoints, string[] Watches);

/// <summary>会话状态、串行命令、项目隔离与用户断点；离线和实机共用交互状态机。</summary>
public sealed partial class DebugSessionService(string dataDirectory) : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private GdbDebugAdapter? adapter;
    private Action<string>? receiver;
    private readonly Dictionary<string, string> bound = [];
    private readonly Channel<(GdbDebugAdapter Source, string Record)> records = Channel.CreateUnbounded<(GdbDebugAdapter, string)>(new() { SingleReader = true });
    private Task? recordPump;
    private DebugAction lastResumeAction = DebugAction.Continue;
    private volatile bool pauseRequested;
    private string? settingsPath;
    private SourceBreakpoint[] breakpoints = [];
    private string[] watches = ["app_counter", "app_input", "app_output", "$pc"];
    public string? ProjectDirectory { get; private set; }
    public DebugState State { get; private set; }
    public string Reason { get; private set; } = "未启动调试";
    public DebugSnapshot Snapshot { get; private set; } = DebugSnapshot.Empty;
    public IReadOnlyList<SourceBreakpoint> Breakpoints => breakpoints;
    public IReadOnlyList<string> Watches => watches;
    public bool IsActive => State is DebugState.Starting or DebugState.Stopped or DebugState.Running or DebugState.Stopping;
    public event Action? Changed;
    public event Action<string>? Output;
    public event Action<string>? BreakpointLog;

    public async Task OpenProjectAsync(string? directory, CancellationToken token = default)
    {
        await StopAsync(); await gate.WaitAsync(token);
        try
        {
            ProjectDirectory = directory is null ? null : Path.GetFullPath(directory);
            breakpoints = []; watches = ["app_counter", "app_input", "app_output", "$pc"];
            // 实机 RISC-V 工程不预填 F407 离线示例变量；已保存的用户观察项照常恢复。
            if (ProjectDirectory is not null)
            {
                var project = await ProjectService.ReadAsync(ProjectDirectory, token);
                if (project.ToolsetId is "wch.riscv" or "agm.agrv" || project.DeviceId == Rp2350DebugTarget.DeviceId) watches = ["$pc"];
            }
            settingsPath = directory is null ? null : Path.Combine(dataDirectory, "debug", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ProjectDirectory!.ToUpperInvariant()))).ToLowerInvariant() + ".json");
            if (settingsPath is not null && File.Exists(settingsPath))
            {
                var settings = await JsonStore.ReadAsync<DebugPreferences>(settingsPath, token);
                if (settings.FormatVersion != 1) throw new StudioXException("DEBUG_SETTINGS", "不支持的调试设置版本。");
                breakpoints = settings.Breakpoints.Where(b => !b.SessionOnly).Select(b => { ValidateLocation(b.File, b.Line); BreakpointOptions.From(b).Validate(); return Unbound(b); }).ToArray();
                watches = settings.Watches.Where(ValidWatch).Distinct(StringComparer.Ordinal).Take(32).ToArray();
            }
            Changed?.Invoke();
        }
        finally { gate.Release(); }
    }
    public async Task StartOfflineAsync(CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        if (IsActive) { gate.Release(); throw Error("调试会话已经启动。"); }
        try
        {
            if (ProjectDirectory is null) throw Error("请先打开 STM32F407 离线调试示例。");
            var project = await ProjectService.ReadAsync(ProjectDirectory, token);
            var text = await File.ReadAllTextAsync(PathBoundary.Resolve(ProjectDirectory, F407DebugExample.RelativeFile), token);
            if (!project.DeviceId.StartsWith("STM32F407", StringComparison.OrdinalIgnoreCase) || text.Replace("\r\n", "\n").TrimEnd() != F407DebugExample.Source.Replace("\r\n", "\n").TrimEnd())
                throw Error("离线模型只对应内置 F407 调试示例，不能执行任意工程。请使用“打开离线调试示例”。");
            SetState(DebugState.Starting, "正在初始化离线会话"); bound.Clear(); pauseRequested = false;
            IsHardware = false; SessionLogPath = null; HardwareTargetName = null; HardwareTarget = null;
            breakpoints = breakpoints.Where(b => !b.SessionOnly).Select(Unbound).ToArray();
            var current = new GdbDebugAdapter(new SimulatedF407Transport()); adapter = current;
            recordPump ??= ReadRecordsAsync();
            receiver = record => records.Writer.TryWrite((current, record));
            current.RecordReceived += receiver; current.Trace += Trace;
            await current.SendAsync("-gdb-set mi-async on", token);
            for (var i = 0; i < breakpoints.Length; i++) breakpoints[i] = await BindAsync(breakpoints[i], token);
            Snapshot = await current.ReadAsync(watches, 0, token);
            Trace("离线会话：STM32F407 / ST-Link / OpenOCD MI。所有数值为模拟数据，未启动硬件进程。");
            SetState(DebugState.Stopped, "已暂停 · main 入口（模拟）");
        }
        catch
        {
            await ReleaseAdapterAsync(); SetState(DebugState.Faulted, "调试启动失败，查看调试输出"); throw;
        }
        finally { gate.Release(); }
    }
    public async Task ExecuteAsync(DebugAction action, CancellationToken token = default)
    {
        if (action == DebugAction.Pause) pauseRequested = true;
        await gate.WaitAsync(token);
        try
        {
            // 日志断点处理期间收到暂停：事件处理器已保持暂停时，不再发送 interrupt。
            if (action == DebugAction.Pause && State == DebugState.Stopped) return;
            if (adapter is null || (action == DebugAction.Pause ? State != DebugState.Running : State != DebugState.Stopped)) throw Error(action == DebugAction.Pause ? "当前没有运行中的目标。" : "请先暂停目标。");
            if (action == DebugAction.StepOut && Snapshot.Frames.Length < 2) throw Error("已处于最外层函数，不能跳出。");
            var previousState = State;
            if (action is DebugAction.Continue or DebugAction.StepInto or DebugAction.StepOver or DebugAction.StepOut)
            {
                pauseRequested = false; lastResumeAction = action;
                SetState(DebugState.Running, "运行中 · 暂停后刷新寄存器与变量" + ModeSuffix);
            }
            try { await adapter.ActionAsync(action, token); }
            catch (StudioXException ex) when (ex.Code == "GDB_COMMAND") { SetState(previousState, "命令被拒绝，目标状态未改变"); throw; }
            catch
            {
                try { await ReleaseAdapterAsync(); } catch (Exception cleanup) { Trace("清理异常：" + cleanup); }
                Snapshot = DebugSnapshot.Empty; SetState(DebugState.Faulted, "调试命令中断，会话已结束"); throw;
            }
            if (action == DebugAction.Reset && IsHardware)
            {
                // monitor 复位不会给 GDB 发新的 stopped 通知，须同时丢弃寄存器、栈与内存缓存。
                await adapter.SendAsync("-interpreter-exec console \"maintenance flush register-cache\"", token);
                await adapter.SendAsync("-interpreter-exec console \"maintenance flush dcache\"", token);
                Snapshot = NormalizeSnapshot(await adapter.ReadAsync(watches, 0, token));
                SetState(DebugState.Stopped, "已复位并暂停（实机）");
            }
        }
        finally { gate.Release(); }
    }
    private async Task ProcessRecordAsync(GdbDebugAdapter source, string text)
    {
        // 不在 transport 的回调栈内执行命令；源实例检查挡住关闭/切换工程后的迟到事件。
        await gate.WaitAsync();
        try
        {
            if (adapter != source) return;
            Trace("< " + text); var record = MiRecord.Parse(text);
            if (record.Kind == '=' && record.Class == "studiox-transport-error") throw Error(record.Data.String("msg"));
            if (record.Kind == '=' && record.Class == "breakpoint-modified" && record.Data.Get("bkpt") is { } info)
            { UpdateBreakpointInfo(info); Changed?.Invoke(); return; }
            if (record.Kind == '=' && record.Class == "breakpoint-deleted")
            { await ForgetRemoteBreakpointAsync(record.Data.String("id")); return; }
            if (record.Kind == '*' && record.Class == "running") { SetState(DebugState.Running, "运行中 · 暂停后刷新" + ModeSuffix); return; }
            if (record.Kind != '*' || record.Class != "stopped") return;
            if (await HandleBreakpointStopAsync(source, record)) return;
            var previous = Snapshot; Snapshot = Highlight(NormalizeSnapshot(await source.ReadAsync(watches, 0, CancellationToken.None)), previous);
            var reason = stopDescription ?? (record.Data.String("reason") switch { "breakpoint-hit" => "命中断点", "end-stepping-range" => "单步完成", "reset" => "已复位并暂停", "signal-received" => "用户暂停", "condition-error" => "条件求值失败：" + record.Data.String("msg"), _ => "已暂停" });
            SetState(DebugState.Stopped, reason + ModeSuffix);
        }
        catch (Exception ex)
        {
            Trace(ex.ToString());
            try { await ReleaseAdapterAsync(); } catch (Exception cleanup) { Trace("清理异常：" + cleanup); }
            Snapshot = DebugSnapshot.Empty; SetState(DebugState.Faulted, "调试响应异常：" + ex.Message);
        }
        finally { gate.Release(); }
    }
    public async Task RefreshAsync(int? frame = null, CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            RequireStopped(); var previous = Snapshot;
            Snapshot = Highlight(NormalizeSnapshot(await adapter!.ReadAsync(watches, frame ?? Snapshot.SelectedFrame, token)), previous); Changed?.Invoke();
        }
        finally { gate.Release(); }
    }
    public Task<string> ReadMemoryAsync(uint address, CancellationToken token = default) =>
        ReadMemoryAsync(address, 64, token);

    public async Task<string> ReadMemoryAsync(uint address, int byteCount, CancellationToken token = default)
    {
        if (byteCount is < 1 or > 256)
            throw new ArgumentOutOfRangeException(nameof(byteCount), "单次内存读取必须为 1–256 字节。");
        if ((ulong)address + (uint)byteCount > (ulong)uint.MaxValue + 1)
            throw new ArgumentOutOfRangeException(nameof(address), "读取范围超出 32 位地址空间。");
        await gate.WaitAsync(token);
        try { RequireStopped(); return await adapter!.ReadMemoryAsync(address, byteCount, token); }
        finally { gate.Release(); }
    }
    public async Task ToggleBreakpointAsync(string file, int line, CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            ValidateLocation(file, line); RequireEditableBreakpoints();
            var found = breakpoints.FirstOrDefault(b => b.File.Equals(file, StringComparison.OrdinalIgnoreCase) && b.Line == line);
            if (found is not null) await RemoveCoreAsync(found.Id, token);
            else
            {
                if (breakpoints.Length >= 128) throw Error("最多保存 128 个源代码断点。");
                var point = new SourceBreakpoint(Guid.NewGuid().ToString("N"), file, line);
                if (adapter is not null) point = await BindAsync(point, token);
                breakpoints = [..breakpoints, point];
            }
            await SaveAsync(token); Changed?.Invoke();
        }
        finally { gate.Release(); }
    }
    public async Task ChangeBreakpointAsync(string id, bool? enabled, CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            RequireEditableBreakpoints(); var index = Array.FindIndex(breakpoints, b => b.Id == id);
            if (index < 0) return;
            if (enabled is null) await RemoveCoreAsync(id, token);
            else
            {
                if (adapter is not null && bound.TryGetValue(id, out var remote)) await adapter.EnableAsync(remote, enabled.Value, token);
                breakpoints[index] = breakpoints[index] with { Enabled = enabled.Value };
                if (adapter is not null && !bound.ContainsKey(id)) breakpoints[index] = await BindAsync(breakpoints[index], token);
            }
            await SaveAsync(token); Changed?.Invoke();
        }
        finally { gate.Release(); }
    }
    public async Task UpdateLinesAsync(IReadOnlyDictionary<string, int> lines, CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            if (IsActive) return;
            breakpoints = breakpoints.Select(b => lines.TryGetValue(b.Id, out var line) ? b with { Line = Math.Max(1, line), Verified = false, BoundLocation = null } : b).ToArray();
            await SaveAsync(token); Changed?.Invoke();
        }
        finally { gate.Release(); }
    }
    public async Task ChangeWatchAsync(string expression, bool remove, CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            if (!ValidWatch(expression)) throw Error("请输入变量、成员、固定下标或 $寄存器名；此阶段不执行赋值和函数调用。");
            var next = watches.Where(w => w != expression).ToList(); if (!remove) next.Add(expression);
            if (next.Count > 32) throw Error("最多观察 32 个变量。"); watches = next.ToArray(); await SaveAsync(token);
            if (State == DebugState.Stopped) Snapshot = Highlight(NormalizeSnapshot(await adapter!.ReadAsync(watches, Snapshot.SelectedFrame, token)), Snapshot);
            Changed?.Invoke();
        }
        finally { gate.Release(); }
    }
    private async Task<SourceBreakpoint> BindAsync(SourceBreakpoint point, CancellationToken token)
    {
        try { return await BindStrictAsync(point, token); }
        catch (StudioXException ex) when (ex.Code == "GDB_COMMAND") { Trace(ex.Message); return point with { Verified = false, Message = ex.Message }; }
    }
    private async Task RemoveCoreAsync(string id, CancellationToken token)
    {
        if (adapter is not null && bound.TryGetValue(id, out var remote)) await adapter.DeleteAsync(remote, token);
        bound.Remove(id); breakpoints = breakpoints.Where(b => b.Id != id).ToArray();
    }
    public async Task StopAsync()
    {
        pauseRequested = true;
        await gate.WaitAsync();
        try
        {
            if (adapter is not null) SetState(DebugState.Stopping, "正在结束调试");
            await ReleaseAdapterAsync(); Snapshot = DebugSnapshot.Empty;
            breakpoints = breakpoints.Where(b => !b.SessionOnly).Select(Unbound).ToArray();
            SetState(DebugState.Disconnected, "未启动调试");
        }
        catch (Exception ex)
        {
            Snapshot = DebugSnapshot.Empty; breakpoints = breakpoints.Where(b => !b.SessionOnly).Select(Unbound).ToArray();
            SetState(DebugState.Faulted, "结束调试未完整确认：" + ex.Message); Trace(ex.ToString()); throw;
        }
        finally { gate.Release(); }
    }
    private async Task ReleaseAdapterAsync()
    {
        var previous = adapter; adapter = null; bound.Clear();
        if (previous is null) return;
        if (receiver is not null) previous.RecordReceived -= receiver;
        previous.Trace -= Trace; receiver = null; await previous.DisposeAsync();
    }
    private void ValidateLocation(string file, int line)
    {
        if (ProjectDirectory is null || line < 1 || line > 1000000) throw Error("断点位置无效。");
        _ = PathBoundary.Resolve(ProjectDirectory, file);
    }
    private void RequireEditableBreakpoints() { if (State is not (DebugState.Disconnected or DebugState.Stopped or DebugState.Faulted)) throw Error("请先暂停目标，再修改断点。"); }
    private void RequireStopped() { if (State != DebugState.Stopped || adapter is null) throw Error("暂停后才可读取目标状态。"); }
    private Task SaveAsync(CancellationToken token) => settingsPath is null ? Task.CompletedTask : JsonStore.WriteAsync(settingsPath, new DebugPreferences(1, breakpoints.Where(b => !b.SessionOnly).Select(Unbound).ToArray(), watches), token);
    private void SetState(DebugState state, string reason) { State = state; Reason = reason; Changed?.Invoke(); }
    private void Trace(string text) => Output?.Invoke(text);
    private static StudioXException Error(string text) => new("DEBUG_STATE", text);
    [GeneratedRegex(@"^(?:\$?[A-Za-z_]\w*)(?:(?:\.|->)[A-Za-z_]\w*|\[\d+\])*$", RegexOptions.CultureInvariant)]
    private static partial Regex WatchPattern();
    private static bool ValidWatch(string text) => text.Length is > 0 and < 200 && WatchPattern().IsMatch(text);
    private static DebugSnapshot Highlight(DebugSnapshot next, DebugSnapshot previous)
    {
        return next with
        {
            Registers = next.Registers.Select(r => r with { Changed = previous.Registers.Any(p => p.Name == r.Name && p.Value != r.Value) }).ToArray(),
            Locals = next.Locals.Select(v => v with { Changed = next.SelectedFrame == previous.SelectedFrame && previous.Locals.Any(p => p.Name == v.Name && p.Value != v.Value) }).ToArray(),
            Watches = next.Watches.Select(v => v with { Changed = previous.Watches.Any(p => p.Name == v.Name && p.Value != v.Value) }).ToArray()
        };
    }
    public async ValueTask DisposeAsync()
    {
        try { await StopAsync(); }
        finally { records.Writer.TryComplete(); if (recordPump is not null) await recordPump; }
    }

    public static async Task<string> CreateExampleAsync(PackRepository packs, string data, string packagedPacks, CancellationToken token = default)
    {
        var pack = (await packs.ListCatalogAsync(token)).FirstOrDefault(p => p.Manifest.Id == "studiox.stm32f407" && p.Manifest.Version == "0.1.1");
        if (pack is null && Directory.Exists(packagedPacks))
        {
            var archive = Directory.EnumerateFiles(packagedPacks, "studiox.stm32f407-0.1.1.mcupack", SearchOption.AllDirectories).FirstOrDefault();
            if (archive is not null) pack = await packs.ImportAsync(archive, token);
        }
        if (pack is null) throw new StudioXException("DEBUG_PACK", "请先导入随软件提供的 STM32F407 0.1.1 器件包，再打开离线调试示例。");
        var directory = Path.Combine(data, "debug-examples", "F407_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", System.Globalization.CultureInfo.InvariantCulture) + "_" + Guid.NewGuid().ToString("N")[..6]);
        await new ProjectService().CreateAsync(pack, "STM32F407ZG", "hal", "F407_Debug", directory, token);
        await File.WriteAllTextAsync(Path.Combine(directory, F407DebugExample.RelativeFile), F407DebugExample.Source, token);
        return directory;
    }
}
