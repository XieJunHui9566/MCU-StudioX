namespace StudioX.Application.Mcp;

using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;
using StudioX.Engine;
using StudioX.Engine.Debugging;
using StudioX.Foundation;

/// <summary>调试 MCP 入口复用应用层状态机；工具本身不执行 GDB 命令，也不隐式下载固件。</summary>
public sealed partial class StudioXMcpTools
{
    private readonly SemaphoreSlim mcpDebugGate = new(1, 1);
    private Action? mcpDebugChanged;
    private int mcpDebugOwned;

    [McpServerTool(Name = "debug_status")]
    [Description("读取当前工程的调试状态和断点摘要。可观察 IDE 会话，但不改变目标状态。")]
    public async Task<string> DebugStatusAsync(CancellationToken cancellationToken = default)
    {
        await mcpDebugGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var debug = Services.Debugger;
            if (!DebugProjectMatches(debug))
                return JsonSerializer.Serialize(new { project = Project, available = false,
                    message = "IDE 调试器当前绑定其他工程；不会读取或操作该工程的会话。" });
            return DebugStateJson(debug);
        }
        finally { mcpDebugGate.Release(); }
    }

    [McpServerTool(Name = "debug_wait")]
    [Description("等待当前工程调试器进入指定状态或超时，适合运行后等待断点；不轮询、不连接或修改芯片。")]
    public async Task<string> DebugWaitAsync(
        [Description("目标状态：Stopped、Running、Faulted、Disconnected，或 any（任意状态通知）；默认 Stopped。")]
        string expectedState = "Stopped",
        [Description("最长等待毫秒数，范围 1–60000；默认 10000。")]
        int timeoutMs = 10_000,
        CancellationToken cancellationToken = default)
    {
        if (timeoutMs is < 1 or > 60_000)
            throw new ArgumentOutOfRangeException(nameof(timeoutMs), "等待时间必须在 1–60000 毫秒之间。");
        var any = string.Equals(expectedState, "any", StringComparison.OrdinalIgnoreCase);
        DebugState selected = default;
        if (!any && (!Enum.TryParse<DebugState>(expectedState, true, out selected) ||
                     !Enum.IsDefined(selected)))
            throw new ArgumentException("状态必须是 Stopped、Running、Faulted、Disconnected 或 any。", nameof(expectedState));

        // 等待期间不能占住 MCP 调试门；IDE 和其他工具仍须能够继续/暂停调试会话。
        var debug = RequireMatchingDebugProject();
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged()
        {
            if (debug.ProjectDirectory is null || !DebugProjectMatches(debug) || any || debug.State == selected)
                changed.TrySetResult();
        }
        debug.Changed += OnChanged;
        try
        {
            if (!any && debug.State == selected) changed.TrySetResult();
            var timeout = Task.Delay(timeoutMs, cancellationToken);
            var completed = await Task.WhenAny(changed.Task, timeout).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var timedOut = completed != changed.Task;
            if (debug.ProjectDirectory is null || !DebugProjectMatches(debug))
                throw new StudioXException("DEBUG_PROJECT", "等待期间 IDE 已切换到其他工程的调试会话。");
            var frame = debug.Snapshot.Frames.FirstOrDefault();
            return JsonSerializer.Serialize(new
            {
                project = Project, timedOut, state = debug.State.ToString(), debug.Reason,
                active = debug.IsActive, hardware = debug.IsActive && debug.IsHardware,
                simulated = debug.IsActive && !debug.IsHardware,
                frame = frame is null ? null : new { frame.Function, frame.File, frame.Line, frame.Address }
            });
        }
        finally { debug.Changed -= OnChanged; }
    }

    [McpServerTool(Name = "debug_log")]
    [Description("按字节偏移读取当前工程最近实机调试会话的 OpenOCD/GDB 原始日志；默认读取尾部，可用 nextOffset 续页。只读，不连接芯片。")]
    public async Task<string> DebugLogAsync(
        [Description("起始字节偏移；省略时读取日志尾部。")]
        long? offset = null,
        [Description("每页最多 4000 字节；默认 4000。")]
        int maxBytes = 4_000,
        CancellationToken cancellationToken = default)
    {
        if (offset is < 0 || maxBytes is < 1 or > 4_000)
            throw new ArgumentOutOfRangeException(nameof(offset), "offset 必须非负，maxBytes 为 1–4000。");
        var debug = RequireMatchingDebugProject();
        var logPath = debug.SessionLogPath;
        if (string.IsNullOrWhiteSpace(logPath))
            throw new StudioXException("DEBUG_LOG", "当前工程没有实机调试日志；离线会话不生成 OpenOCD/GDB 日志。");
        var relative = Path.GetRelativePath(Project, logPath).Replace('\\', '/');
        if (relative.StartsWith("../", StringComparison.Ordinal) || relative == "..")
            throw new StudioXException("DEBUG_LOG_PATH", "调试日志不属于当前工程。");
        var safePath = PathBoundary.Resolve(Project, relative);
        if (!File.Exists(safePath))
            throw new StudioXException("DEBUG_LOG", "当前调试日志尚未写入磁盘。");
        await using var stream = new FileStream(safePath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var length = stream.Length;
        var start = Math.Min(offset ?? Math.Max(0, length - maxBytes), length);
        stream.Position = start;
        var bytes = new byte[(int)Math.Min(maxBytes, length - start)];
        var read = 0;
        while (read < bytes.Length)
        {
            var count = await stream.ReadAsync(bytes.AsMemory(read), cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            read += count;
        }
        return JsonSerializer.Serialize(new
        {
            project = Project,
            logPath = relative,
            offset = start,
            nextOffset = start + read,
            totalBytes = length,
            hasMore = start + read < length,
            text = Encoding.UTF8.GetString(bytes, 0, read)
        });
    }

    [McpServerTool(Name = "debug_start_offline")]
    [Description("经用户批准后启动当前工程的 STM32F407 离线调试示例；只使用模拟传输，不连接芯片。")]
    public async Task<string> DebugStartOfflineAsync(CancellationToken cancellationToken = default)
    {
        await mcpDebugGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RequireSavedDocumentsAsync().ConfigureAwait(false);
            await ProjectService.ReadAsync(Project, cancellationToken).ConfigureAwait(false);
            EnsureDebugCanStart();
            await RequireApprovalAsync("debug_start_offline", "启动当前工程的 STM32F407 模拟调试会话；不连接硬件。",
                StudioXMcpPermission.DebugControl, cancellationToken).ConfigureAwait(false);
            await BindDebugProjectAsync(cancellationToken).ConfigureAwait(false);
            EnsureDebugCanStart();
            TrackOwnedDebugSession();
            try { await Services.Debugger.StartOfflineAsync(cancellationToken).ConfigureAwait(false); }
            catch { ClearOwnedDebugSession(); throw; }
            return DebugStateJson(Services.Debugger);
        }
        finally { mcpDebugGate.Release(); }
    }

    [McpServerTool(Name = "debug_start_hardware")]
    [Description("经用户明确批准后使用当前工程的 OpenOCD/GDB 配置附加实机，并校验板上固件与 ELF；不编译、不下载、不烧录。")]
    public async Task<string> DebugStartHardwareAsync(CancellationToken cancellationToken = default)
    {
        await mcpDebugGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RequireSavedDocumentsAsync().ConfigureAwait(false);
            await ProjectService.ReadAsync(Project, cancellationToken).ConfigureAwait(false);
            EnsureDebugCanStart();
            var configuration = await Services.Downloads.ConfigurationAsync(Project, cancellationToken).ConfigureAwait(false)
                ?? throw new StudioXException("DEBUG_TARGET", "当前工程缺少调试配置。");
            var probe = OpenOcdDebugPlanner.ResolveProbe(configuration);
            await RequireApprovalAsync("debug_start_hardware",
                $"附加 {configuration.Device.Id} / {probe.DisplayName} 实机调试，并只读校验板上固件与 ELF；不下载或烧录。",
                StudioXMcpPermission.HardwareConnect, cancellationToken).ConfigureAwait(false);
            await BindDebugProjectAsync(cancellationToken).ConfigureAwait(false);
            EnsureDebugCanStart();
            // 准备阶段会校验锁定工具、源码快照与 ELF，仅复制受校验产物，不接触硬件。
            var preparation = await HardwareDebugPreparer.PrepareAsync(Project, Services.Downloads, cancellationToken)
                .ConfigureAwait(false);
            EnsureDebugCanStart();
            TrackOwnedDebugSession();
            try { await Services.Debugger.StartHardwareAsync(preparation, cancellationToken).ConfigureAwait(false); }
            catch { ClearOwnedDebugSession(); throw; }
            return DebugStateJson(Services.Debugger);
        }
        finally { mcpDebugGate.Release(); }
    }

    [McpServerTool(Name = "debug_control")]
    [Description("经用户逐次批准后控制当前工程的调试会话：pause、continue、step_into、step_over、step_out 或 stop；不提供复位或下载。")]
    public async Task<string> DebugControlAsync(
        [Description("pause、continue、step_into、step_over、step_out 或 stop。")]
        string action,
        CancellationToken cancellationToken = default)
    {
        var selected = action?.Trim().ToLowerInvariant() switch
        {
            "pause" => DebugAction.Pause,
            "continue" => DebugAction.Continue,
            "step_into" => DebugAction.StepInto,
            "step_over" => DebugAction.StepOver,
            "step_out" => DebugAction.StepOut,
            "stop" => (DebugAction?)null,
            _ => throw new ArgumentException("action 必须是 pause、continue、step_into、step_over、step_out 或 stop。", nameof(action))
        };
        await mcpDebugGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var debug = RequireMatchingDebugProject();
            if (!debug.IsActive && !(selected is null && debug.State == DebugState.Faulted))
                throw new StudioXException("DEBUG_STATE", "当前工程没有可控制的调试会话。");
            var ownership = Volatile.Read(ref mcpDebugOwned) != 0 ? "本 Agent 创建的" : "IDE 已有的";
            await RequireStableDebugApprovalAsync(debug, "debug_control",
                $"对{ownership} {Project} 调试会话执行 {action}。", cancellationToken).ConfigureAwait(false);
            if (selected is { } command) await debug.ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
            else
            {
                await debug.StopAsync().ConfigureAwait(false);
                ClearOwnedDebugSession();
            }
            return DebugStateJson(debug);
        }
        finally { mcpDebugGate.Release(); }
    }

    [McpServerTool(Name = "debug_breakpoint_set")]
    [Description("经用户逐次批准后在当前工程源文件设置或更新断点；可设置条件和跳过次数，运行中不可修改。")]
    public async Task<string> DebugBreakpointSetAsync(
        [Description("当前工程内使用正斜杠的相对源文件路径。")]
        string file,
        [Description("从 1 开始的源代码行号。")]
        int line,
        [Description("可选条件表达式；空字符串为普通断点。")]
        string condition = "",
        [Description("跳过前 N 次命中，默认 0。")]
        int ignoreCount = 0,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(file) || line is < 1 or > 1_000_000)
            throw new ArgumentException("断点文件或行号无效。");
        _ = PathBoundary.Resolve(Project, file);
        var options = new BreakpointOptions(condition ?? "", ignoreCount);
        options.Validate();
        await mcpDebugGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ProjectService.ReadAsync(Project, cancellationToken).ConfigureAwait(false);
            if (!DebugProjectMatches(Services.Debugger))
                throw new StudioXException("DEBUG_PROJECT", "IDE 调试器已绑定其他工程，不能设置断点。");
            var debug = Services.Debugger;
            await RequireStableDebugApprovalAsync(debug, "debug_breakpoint_set",
                $"在当前工程 {file}:{line} 设置或更新断点。", cancellationToken).ConfigureAwait(false);
            await BindDebugProjectAsync(cancellationToken).ConfigureAwait(false);
            await debug.ConfigureBreakpointAsync(file, line, options, cancellationToken).ConfigureAwait(false);
            var point = debug.Breakpoints.FirstOrDefault(item => !item.SessionOnly &&
                item.File.Equals(file, StringComparison.OrdinalIgnoreCase) && item.Line == line);
            return JsonSerializer.Serialize(new { project = Project, breakpoint = point });
        }
        finally { mcpDebugGate.Release(); }
    }

    [McpServerTool(Name = "debug_breakpoint_remove")]
    [Description("经用户逐次批准后按 debug_status 返回的断点 ID 移除当前工程断点；运行中不可修改。")]
    public async Task<string> DebugBreakpointRemoveAsync(
        [Description("debug_status 返回的断点 ID。")]
        string id,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128)
            throw new ArgumentException("断点 ID 无效。", nameof(id));
        await mcpDebugGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!DebugProjectMatches(Services.Debugger))
                throw new StudioXException("DEBUG_PROJECT", "IDE 调试器已绑定其他工程，不能移除断点。");
            await BindDebugProjectAsync(cancellationToken).ConfigureAwait(false);
            var debug = RequireMatchingDebugProject();
            var point = debug.Breakpoints.FirstOrDefault(item => !item.SessionOnly && item.Id == id)
                ?? throw new StudioXException("DEBUG_BREAKPOINT", "当前工程找不到该断点。");
            await RequireStableDebugApprovalAsync(debug, "debug_breakpoint_remove",
                $"移除当前工程 {point.File}:{point.Line} 的断点。", cancellationToken).ConfigureAwait(false);
            await debug.ChangeBreakpointAsync(id, null, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Serialize(new { project = Project, removed = true, breakpointId = id });
        }
        finally { mcpDebugGate.Release(); }
    }

    [McpServerTool(Name = "debug_read_memory")]
    [Description("只读读取当前工程已暂停调试目标从指定十六进制地址起的 1–256 字节（默认 64）；返回 nextAddress 供后续分段读取，不写入目标内存。")]
    public async Task<string> DebugReadMemoryAsync(
        [Description("十六进制地址，例如 0x20000000。")]
        string address,
        [Description("本次读取字节数，范围 1–256；省略时读取 64 字节。")]
        int byteCount = 64,
        CancellationToken cancellationToken = default)
    {
        var text = address?.Trim();
        if (text?.StartsWith("0x", StringComparison.OrdinalIgnoreCase) == true) text = text[2..];
        if (string.IsNullOrEmpty(text) || !uint.TryParse(text, NumberStyles.HexNumber,
                CultureInfo.InvariantCulture, out var parsed))
            throw new ArgumentException("请输入 32 位十六进制地址，例如 0x20000000。", nameof(address));
        if (byteCount is < 1 or > 256)
            throw new ArgumentOutOfRangeException(nameof(byteCount), "单次内存读取必须为 1–256 字节。");
        if ((ulong)parsed + (uint)byteCount > (ulong)uint.MaxValue + 1)
            throw new ArgumentOutOfRangeException(nameof(address), "读取范围超出 32 位地址空间。");
        await mcpDebugGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var debug = RequireMatchingDebugProject();
            if (debug.State != DebugState.Stopped)
                throw new StudioXException("DEBUG_STATE", "暂停目标后才能读取内存。");
            var hex = await debug.ReadMemoryAsync(parsed, byteCount, cancellationToken).ConfigureAwait(false);
            byte[] bytes;
            try { bytes = Convert.FromHexString(hex); }
            catch (FormatException ex)
            {
                throw new StudioXException("GDB_MEMORY", "调试器返回了无效的内存十六进制数据。", ex);
            }
            if (bytes.Length is 0 || bytes.Length > byteCount)
                throw new StudioXException("GDB_MEMORY", "调试器返回的内存字节数超出请求范围。");
            var next = (ulong)parsed + (uint)bytes.Length;
            return JsonSerializer.Serialize(new { project = Project, address = $"0x{parsed:X8}", hex,
                requestedByteCount = byteCount, byteCount = bytes.Length,
                complete = bytes.Length == byteCount,
                nextAddress = next <= uint.MaxValue ? $"0x{next:X8}" : null,
                simulated = !debug.IsHardware });
        }
        finally { mcpDebugGate.Release(); }
    }

    [McpServerTool(Name = "debug_snapshot")]
    [Description("读取当前工程最近一次暂停时的寄存器、调用栈、局部变量和观察项有界快照；运行中快照可能已经过时。")]
    public async Task<string> DebugSnapshotAsync(CancellationToken cancellationToken = default)
    {
        await mcpDebugGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var debug = RequireMatchingDebugProject();
            var snapshot = debug.Snapshot;
            return JsonSerializer.Serialize(new
            {
                project = Project,
                state = debug.State.ToString(),
                simulated = !debug.IsHardware,
                staleWhileRunning = debug.State == DebugState.Running,
                snapshot.SelectedFrame,
                registerCount = snapshot.Registers.Length,
                registers = snapshot.Registers.Take(96).Select(item => new
                { item.Name, value = LimitOutput(item.Value, 256), item.Changed }).ToArray(),
                frameCount = snapshot.Frames.Length,
                frames = snapshot.Frames.Take(32).Select(item => new
                { item.Level, item.Function, item.File, item.Line, item.Address }).ToArray(),
                localCount = snapshot.Locals.Length,
                locals = snapshot.Locals.Take(64).Select(item => new
                { item.Name, value = LimitOutput(item.Value, 256), item.Type, item.Changed }).ToArray(),
                watchCount = snapshot.Watches.Length,
                watches = snapshot.Watches.Take(32).Select(item => new
                { item.Name, value = LimitOutput(item.Value, 256), item.Type, item.Changed }).ToArray()
            });
        }
        finally { mcpDebugGate.Release(); }
    }

    private bool DebugProjectMatches(DebugSessionService debug) => debug.ProjectDirectory is null ||
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(debug.ProjectDirectory))
            .Equals(Project, StringComparison.OrdinalIgnoreCase);

    private DebugSessionService RequireMatchingDebugProject()
    {
        var debug = Services.Debugger;
        if (debug.ProjectDirectory is null || !DebugProjectMatches(debug))
            throw new StudioXException("DEBUG_PROJECT", "调试器未绑定当前 MCP 工程；请先在 IDE 打开该工程。控制其他工程的会话被拒绝。");
        return debug;
    }

    private void EnsureDebugCanStart()
    {
        var debug = Services.Debugger;
        if (!DebugProjectMatches(debug))
            throw new StudioXException("DEBUG_PROJECT", "IDE 调试器已绑定其他工程，不能由 MCP 切换或停止该会话。");
        if (debug.IsActive)
            throw new StudioXException("DEBUG_STATE", "已有活动调试会话；请先在原会话中结束，不会由 MCP 自动停止。");
    }

    private async Task BindDebugProjectAsync(CancellationToken token)
    {
        if (Services.Debugger.ProjectDirectory is null)
            await Services.Debugger.OpenProjectAsync(Project, token).ConfigureAwait(false);
        else if (!DebugProjectMatches(Services.Debugger))
            throw new StudioXException("DEBUG_PROJECT", "IDE 调试器已绑定其他工程，不能由 MCP 切换。");
    }

    private async Task RequireStableDebugApprovalAsync(DebugSessionService debug, string tool,
        string summary, CancellationToken token)
    {
        var changes = 0;
        void OnChanged() => Interlocked.Increment(ref changes);
        debug.Changed += OnChanged;
        try
        {
            await RequireApprovalAsync(tool, summary, StudioXMcpPermission.DebugControl, token)
                .ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (Volatile.Read(ref changes) != 0 || !DebugProjectMatches(debug))
                throw new StudioXException("DEBUG_SESSION_CHANGED", "审批期间调试会话已改变，请重新读取状态后再操作。");
        }
        finally { debug.Changed -= OnChanged; }
    }

    private void TrackOwnedDebugSession()
    {
        // IDE 结束或替换会话会触发 Disconnected/Faulted，避免沿用旧会话的 Agent 所有权标记。
        Interlocked.Exchange(ref mcpDebugOwned, 1);
        if (mcpDebugChanged is null)
        {
            mcpDebugChanged = OnMcpDebugChanged;
            Services.Debugger.Changed += mcpDebugChanged;
        }
    }

    private void OnMcpDebugChanged()
    {
        if (Services.Debugger.State is DebugState.Disconnected or DebugState.Faulted)
            ClearOwnedDebugSession();
    }

    private void ClearOwnedDebugSession()
    {
        Interlocked.Exchange(ref mcpDebugOwned, 0);
        var changed = Interlocked.Exchange(ref mcpDebugChanged, null);
        if (changed is not null) Services.Debugger.Changed -= changed;
    }

    private async Task DisposeMcpDebugAsync()
    {
        await mcpDebugGate.WaitAsync().ConfigureAwait(false);
        try
        {
            // MCP 宿主退出或切换工程时只结束本实例启动的会话，保留 IDE 自己的会话。
            if (Volatile.Read(ref mcpDebugOwned) != 0 &&
                DebugProjectMatches(Services.Debugger) && Services.Debugger.IsActive)
                await Services.Debugger.StopAsync().ConfigureAwait(false);
            ClearOwnedDebugSession();
        }
        finally { mcpDebugGate.Release(); }
    }

    private string DebugStateJson(DebugSessionService debug)
    {
        var points = debug.Breakpoints.Where(item => !item.SessionOnly).ToArray();
        return JsonSerializer.Serialize(new
        {
            project = Project,
            available = debug.ProjectDirectory is not null,
            state = debug.State.ToString(),
            debug.Reason,
            active = debug.IsActive,
            simulated = debug.IsActive && !debug.IsHardware,
            hardware = debug.IsActive && debug.IsHardware,
            target = debug.IsActive ? debug.HardwareTargetName : null,
            ownedByAgent = debug.IsActive && Volatile.Read(ref mcpDebugOwned) != 0,
            logPath = debug.IsActive ? debug.SessionLogPath : null,
            breakpointCount = points.Length,
            breakpoints = points.Take(32).Select(item => new
            { item.Id, item.File, item.Line, item.Enabled, item.Verified, item.Message,
                item.Condition, item.IgnoreCount, item.HitCount }).ToArray(),
            omittedBreakpoints = Math.Max(0, points.Length - 32)
        });
    }
}
