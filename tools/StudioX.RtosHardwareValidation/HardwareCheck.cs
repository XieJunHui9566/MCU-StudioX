namespace StudioX.RtosHardwareValidation;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StudioX.Application;
using StudioX.Application.Mcp;
using StudioX.Engine;
using StudioX.Engine.Debugging;
using StudioX.Foundation;

/// <summary>准备阶段不执行工具或写工程；硬件阶段只有显式 --attach 才可到达。</summary>
internal sealed class HardwareCheck
{
    private readonly string project;
    private readonly string runtime;
    private readonly string output;
    private readonly string[] symbols;

    public HardwareCheck(string project, string runtime, string output, string? objectSymbols)
    {
        this.project = FullPath(project);
        this.runtime = FullPath(runtime);
        this.output = FullPath(output);
        if (Within(this.output, this.project) || Within(this.output, this.runtime))
            throw new ArgumentException("Validation output must be outside the firmware project and tool runtime.");
        symbols = objectSymbols?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) ?? [];
        if (symbols.Any(symbol => !FreeRtosInspector.IsObjectSymbol(symbol)))
            throw new ArgumentException("Object symbols must be plain C identifiers or dot-separated member paths.");
    }

    public async Task<int> RunAsync(bool attach)
    {
        Directory.CreateDirectory(output);
        await using var services = new WorkbenchService(runtime, Path.Combine(output, "isolated-user-data"));
        var plan = await PrepareAsync(services);
        await JsonStore.WriteAsync(Path.Combine(output, "plan.json"), plan);
        Console.WriteLine($"Local preparation ready={plan.Ready}; {Path.Combine(output, "plan.json")}");
        if (!attach || !plan.Ready) return plan.Ready ? 0 : 2;

        // 准备操作不会授予硬件权限；只有调用者显式选择 --attach 后才构造有限授权器。
        var authorizer = new ValidationAuthorizer(project);
        var trace = new BoundedTrace();
        services.Debugger.Output += trace.Add;
        var observations = new Dictionary<string, object?>();
        var errors = new List<Exception>();
        var stopConfirmed = false;
        var targetRestoredRunning = false;
        var leaseAvailable = false;
        var imageEvidenceVerified = false;
        string? sessionLogPath = null;
        await using var session = await StudioXMcpSession.CreateAsync(new StudioXMcpTools(services, project, authorizer));
        try
        {
            await services.Debugger.OpenProjectAsync(project);
            if (services.Debugger.Breakpoints.Count != 0)
                throw new StudioXException("RTOS_BREAKPOINTS", "验收数据目录含既有断点，拒绝将其绑定到目标；请选择新的输出目录。");
            var tools = await session.ListToolsAsync();
            if (!tools.Any(tool => tool.Name == "debug_rtos_snapshot"))
                throw new StudioXException("RTOS_MCP", "真实 MCP 握手未发现 debug_rtos_snapshot。");
            observations["toolDiscovered"] = true;
            observations["attach"] = await CallAsync(session, "debug_start_hardware", new { });
            sessionLogPath = services.Debugger.SessionLogPath;
            var verification = await ReadImageVerificationEvidenceAsync(sessionLogPath ?? throw new StudioXException("RTOS_IMAGE_VERIFY", "附加后没有原始会话日志，不能核对映像匹配证据。"));
            observations["imageVerificationEvidence"] = verification;
            imageEvidenceVerified = verification.Accepted;
            if (!imageEvidenceVerified)
                throw new StudioXException("RTOS_IMAGE_VERIFY", "原始会话日志存在实际映像差异、USB 通信失败或没有 positive verified 字节输出，拒绝读取 RTOS 快照。");
            observations["initialStatus"] = await CallAsync(session, "debug_status", new { });
            var before = await CallAsync(session, "debug_rtos_snapshot", new { objectSymbols = symbols });
            observations["initialRtos"] = before;
            RequireHardware(before);
            observations["kernelAvailable"] = before.GetProperty("snapshot").GetProperty("isAvailable").GetBoolean();
            ValidateSnapshot(before, "initialRtosAcceptance", plan, observations);
            observations["continue"] = await CallAsync(session, "debug_control", new { action = "continue" });
            await Task.Delay(200);
            observations["pause"] = await CallAsync(session, "debug_control", new { action = "pause" });
            observations["wait"] = await CallAsync(session, "debug_wait", new { expectedState = "Stopped", timeoutMs = 10_000 });
            if (services.Debugger.State != DebugState.Stopped)
                throw new StudioXException("RTOS_STOP", "暂停请求未得到 Stopped 状态，不能读取后续内核快照。");
            var after = await CallAsync(session, "debug_rtos_snapshot", new { objectSymbols = symbols });
            observations["secondRtos"] = after;
            RequireHardware(after);
            ValidateSnapshot(after, "secondRtosAcceptance", plan, observations);
            observations["finalStatusBeforeStop"] = await CallAsync(session, "debug_status", new { });
            var tickBefore = Tick(before);
            var tickAfter = Tick(after);
            observations["tickObservation"] = new
            {
                before = tickBefore,
                after = tickAfter,
                changed = tickBefore is not null && tickAfter is not null ? tickBefore != tickAfter : (bool?)null,
                note = "记录短暂继续后的观测差异；不预设 Tick 必须推进，计数不可用或未变化也保留证据。"
            };
        }
        catch (Exception ex) { errors.Add(ex); }
        finally
        {
            // 先保留本次会话路径；结束后的 Disconnected 状态本身不能证明芯片恢复运行。
            sessionLogPath ??= services.Debugger.SessionLogPath;
            // 附加失败也执行应用层清理；不发送 reset、download、memory-write 或断点创建命令。
            try
            {
                if (services.Debugger.IsActive || services.Debugger.State == DebugState.Faulted)
                    observations["stop"] = await CallAsync(session, "debug_control", new { action = "stop" });
                else await services.Debugger.StopAsync();
                stopConfirmed = services.Debugger.State == DebugState.Disconnected;
            }
            catch (Exception ex)
            {
                errors.Add(ex);
                // 保留第一次清理异常，同时尽力释放本次会话资源；第二次失败也必须记录。
                try { await services.Debugger.StopAsync(); }
                catch (Exception retry) { errors.Add(retry); }
            }
            try
            {
                using var lease = ProbeLease.Acquire();
                leaseAvailable = true;
            }
            catch (Exception ex)
            {
                observations["probeLeaseDiagnostic"] = ex.Message;
                if (observations.ContainsKey("attach")) errors.Add(new StudioXException("RTOS_PROBE_LEASE", "已附加会话结束后探针租约仍不可用，不能确认探针释放。", ex));
            }
            try
            {
                targetRestoredRunning = sessionLogPath is not null && await ReadDetachedRunningMarkerAsync(sessionLogPath);
                observations["detachedRunningMarkerObserved"] = targetRestoredRunning;
                if (sessionLogPath is not null)
                    observations["sessionLogRelativePath"] = Path.GetRelativePath(project, sessionLogPath).Replace('\\', '/');
                if (observations.ContainsKey("attach") && !targetRestoredRunning)
                    errors.Add(new StudioXException("RTOS_RESTORE", "原始 OpenOCD 会话日志没有 STUDIOX_DETACHED_RUNNING 输出，不能确认目标已恢复运行。"));
            }
            catch (Exception ex) { errors.Add(ex); }
            if (!stopConfirmed) errors.Add(new StudioXException("RTOS_CLEANUP", "未确认调试器已结束，不能把本次验收标记成功。"));
            services.Debugger.Output -= trace.Add;
            await File.WriteAllTextAsync(Path.Combine(output, "debug-trace.log"), trace.Text);
            await JsonStore.WriteAsync(Path.Combine(output, "result.json"), new
            {
                project,
                hardware = true,
                simulated = false,
                hardwareSessionVerified = observations.ContainsKey("attach") && imageEvidenceVerified,
                success = errors.Count == 0 && stopConfirmed && targetRestoredRunning && leaseAvailable && observations.GetValueOrDefault("kernelAvailable") is true,
                observationCompleted = errors.Count == 0 && stopConfirmed && targetRestoredRunning && leaseAvailable,
                kernelAvailable = observations.GetValueOrDefault("kernelAvailable"),
                stopConfirmed,
                targetRestoredRunning,
                probeLeaseAvailableAfterStop = leaseAvailable,
                observations,
                authorizationRequests = authorizer.Requests,
                errors = errors.Select(ex => new { type = ex.GetType().Name, ex.Message }).ToArray(),
                trace.DroppedLines,
                evidence = "显式 --attach 的 MCP/GDB 观测；板上固件匹配须有原始日志 positive verified 且无实际 diff/USB 失败。快照一致性单独验收。未编译、下载、擦除、复位、写内存或创建断点。"
            });
        }
        if (errors.Count > 0) throw new AggregateException("RTOS hardware observation failed; inspect result.json and debug-trace.log.", errors);
        var available = observations.GetValueOrDefault("kernelAvailable") is true;
        Console.WriteLine($"Hardware observation recorded; kernelAvailable={available}, cleanupConfirmed={stopConfirmed}, restoredRunning={targetRestoredRunning}, leaseAvailable={leaseAvailable}. Inspect partial fields and tick observations in result.json.");
        return stopConfirmed && targetRestoredRunning && leaseAvailable && available ? 0 : 2;
    }

    private async Task<PreparationPlan> PrepareAsync(WorkbenchService services)
    {
        var diagnostics = new List<string>();
        ProjectManifest? manifest = null;
        string? device = null, probe = null, toolFingerprint = null, sourceStamp = null, elf = null, elfSha256 = null;
        int? speedKhz = null;
        bool? serialConfigured = null;
        string? imageSha256 = null;
        var ready = false;
        uint? ramBytes = null;
        try
        {
            manifest = await ProjectService.ReadAsync(project);
            var configuration = await services.Downloads.ConfigurationAsync(project)
                ?? throw new StudioXException("DEBUG_TARGET", "当前工程缺少调试配置。");
            device = configuration.Device.Id;
            ramBytes = configuration.Device.RamBytes;
            probe = configuration.Options.ProbeId;
            speedKhz = configuration.Options.SpeedKhz;
            serialConfigured = !string.IsNullOrEmpty(configuration.Options.Serial);
            var stm32 = device == "STM32F407ZG" && probe == "stlink";
            var ch32v307 = (device is "CH32V307VCT6" or "CH32V307RCT6" or "CH32V307WCU6") &&
                probe == "wch-link" && WchDebugTarget.Find(configuration.Device) is not null;
            // 精确型号白名单与领域配置验证同时成立，不能把支持其他 WCH 型号扩大为本次 RTOS 板卡验收。
            if (manifest.DeviceId != device || !stm32 && !ch32v307)
                throw new StudioXException("RTOS_TARGET", "此验收工具只允许 STM32F407ZG / ST-Link，或 CH32V307VCT6、RCT6、WCU6 / WCH-Link 的匹配工程配置。");
            _ = OpenOcdDebugPlanner.ResolveProbe(configuration);
            var settings = await ProjectBuildSettings.ReadAsync(project);
            if (settings.DebugInfo == CompilerDebugInfo.None)
                throw new StudioXException("DEBUG_SYMBOLS", "现有构建设置为 -g0，不能附加源码/RTOS 调试。");
            // Preview 校验工具集、构建凭据、源码摘要和唯一映像，不创建下载目录或运行工具。
            var preview = await services.Downloads.PreviewAsync(project, configuration.Options);
            imageSha256 = preview.Sha256;
            var receipt = await JsonStore.ReadAsync<JsonElement>(PathBoundary.Resolve(project, ".build/studiox-build-receipt.json"));
            sourceStamp = receipt.GetProperty("sourceStamp").GetString();
            toolFingerprint = receipt.GetProperty("toolFingerprint").GetString();
            var image = receipt.GetProperty("images")[0];
            var symbolPath = image.GetProperty("symbolsPath").GetString();
            var expectedHash = image.GetProperty("symbolsSha256").GetString();
            if (string.IsNullOrEmpty(symbolPath) || string.IsNullOrEmpty(expectedHash))
                throw new StudioXException("DEBUG_SYMBOLS", "现有构建凭据缺少 ELF 路径或 SHA-256。");
            elf = PathBoundary.Resolve(project, symbolPath);
            await using var stream = File.OpenRead(elf);
            elfSha256 = Convert.ToHexString(await SHA256.HashDataAsync(stream));
            if (!elfSha256.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new StudioXException("DEBUG_SYMBOLS", "ELF 内容与构建凭据不一致。");
            diagnostics.Add("本地工具锁、源码摘要和映像/ELF哈希已校验；没有执行工具、连接探针或证明板上固件一致。");
            ready = true;
        }
        catch (Exception ex) when (ex is StudioXException or IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException)
        { diagnostics.Add(ex.GetType().Name + ": " + ex.Message); }
        return new(ready,
            project, runtime, manifest, device, probe, speedKhz, serialConfigured, toolFingerprint, sourceStamp,
            elf, elfSha256, imageSha256, ramBytes, symbols, diagnostics.ToArray(),
            ["真实 MCP 握手", "debug_start_hardware（核对现有 ELF，暂停附加）", "debug_status / debug_rtos_snapshot",
             "debug_control continue → 等待约200ms → pause", "debug_wait / debug_rtos_snapshot", "debug_control stop（恢复运行并释放探针）"]);
    }

    private static async Task<JsonElement> CallAsync(StudioXMcpSession session, string tool, object arguments)
    {
        var result = JsonSerializer.Deserialize<JsonElement>(await session.CallToolAsync(tool, JsonSerializer.Serialize(arguments)));
        if (result.TryGetProperty("error", out var error))
            throw new StudioXException(result.TryGetProperty("code", out var code) ? code.GetString() ?? "RTOS_MCP" : "RTOS_MCP", error.GetString() ?? "MCP tool failed.");
        return result;
    }

    private static void RequireHardware(JsonElement response)
    {
        if (!response.GetProperty("hardware").GetBoolean() || response.GetProperty("simulated").GetBoolean())
            throw new StudioXException("RTOS_EVIDENCE", "此硬件验收不能接收模拟会话的数据。");
    }

    private static ulong? Tick(JsonElement response) => response.GetProperty("snapshot").GetProperty("tickCount") is var value &&
        value.ValueKind == JsonValueKind.Number && value.TryGetUInt64(out var tick) ? tick : null;

    private static void ValidateSnapshot(JsonElement response, string stage, PreparationPlan plan, IDictionary<string, object?> observations)
    {
        var snapshot = response.GetProperty("snapshot").Deserialize<FreeRtosSnapshot>(JsonStore.Options)
            ?? throw new StudioXException("RTOS_SNAPSHOT_INVALID", "RTOS 快照没有可读取数据。");
        var report = SnapshotAcceptance.Validate(snapshot, (ulong)(plan.RamBytes ?? 0));
        observations[stage] = report;
        if (!report.Accepted)
            throw new StudioXException("RTOS_SNAPSHOT_INVALID", "内核快照未满足验收一致性：" + string.Join("；", report.Errors));
    }

    private async Task<ImageVerificationEvidence> ReadImageVerificationEvidenceAsync(string logPath)
    {
        var relative = Path.GetRelativePath(project, logPath).Replace('\\', '/');
        var safePath = PathBoundary.Resolve(project, relative);
        await using var stream = new FileStream(safePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream);
        var evidence = new ImageVerificationEvidence();
        while (await reader.ReadLineAsync() is { } line) evidence.AddLogLine(line);
        return evidence;
    }

    private async Task<bool> ReadDetachedRunningMarkerAsync(string logPath)
    {
        var relative = Path.GetRelativePath(project, logPath).Replace('\\', '/');
        var safePath = PathBoundary.Resolve(project, relative);
        await using var stream = new FileStream(safePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync() is { } line)
        {
            // 仅认可 OpenOCD 实际输出，不能把启动脚本内的 echo 命令当成恢复证明。
            const string prefix = " OpenOCD < ";
            var marker = line.IndexOf(prefix, StringComparison.Ordinal);
            if (marker < 0) continue;
            var payload = line[(marker + prefix.Length)..].Trim();
            if (payload == "STUDIOX_DETACHED_RUNNING" || payload == "Info : STUDIOX_DETACHED_RUNNING") return true;
        }
        return false;
    }

    private static string FullPath(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    private static bool Within(string candidate, string root) => candidate.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}

internal sealed record PreparationPlan(bool Ready, string Project, string Runtime, ProjectManifest? Manifest,
    string? Device, string? Probe, int? SpeedKhz, bool? SerialConfigured, string? ToolFingerprint,
    string? SourceStamp, string? Elf, string? ElfSha256, string? ImageSha256, uint? RamBytes, string[] ObjectSymbols,
    string[] Diagnostics, string[] IntendedOperations);

internal sealed class ValidationAuthorizer(string project) : IStudioXMcpAuthorizer
{
    public List<object> Requests { get; } = [];
    public Task<bool> ApproveAsync(StudioXMcpApprovalRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var bound = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.Project)).Equals(project, StringComparison.OrdinalIgnoreCase);
        var allowed = bound && (request.Permission == StudioXMcpPermission.HardwareConnect && request.Tool == "debug_start_hardware" ||
            request.Permission == StudioXMcpPermission.DebugControl && request.Tool == "debug_control" &&
            new[] { "continue", "pause", "stop" }.Any(action => request.Summary.EndsWith("执行 " + action + "。", StringComparison.Ordinal)));
        Requests.Add(new { request.Tool, permission = request.Permission.ToString(), approved = allowed });
        return Task.FromResult(allowed);
    }
}

internal sealed class BoundedTrace
{
    private const int MaximumCharacters = 1024 * 1024;
    private readonly StringBuilder builder = new();
    private readonly object sync = new();
    public int DroppedLines { get; private set; }
    public void Add(string line)
    {
        // 栈变量并非 RTOS 验收证据，不记录其原值；同样过滤可能包含凭据的命名行。
        if (line.Contains("variables=[", StringComparison.Ordinal) || line.Contains("args=[", StringComparison.Ordinal) ||
            new[] { "api_key", "apikey", "authorization", "password", "secret", "token", "bearer ", "sk-" }
                .Any(marker => line.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            line = "[调试输出行已过滤：局部变量或可能的凭据内容]";
        lock (sync)
        {
            if (builder.Length + line.Length + Environment.NewLine.Length > MaximumCharacters) { DroppedLines++; return; }
            builder.AppendLine(line);
        }
    }
    public string Text { get { lock (sync) return builder + $"\n[omitted-lines={DroppedLines}]\n"; } }
}
