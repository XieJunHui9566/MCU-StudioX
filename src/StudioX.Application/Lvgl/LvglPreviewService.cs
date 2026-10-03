namespace StudioX.Application.Lvgl;

using System.Collections.Concurrent;
using System.Text.Json;
using SkiaSharp;
using StudioX.Engine;
using StudioX.Engine.Lvgl;
using StudioX.Foundation;

/// <summary>IDE 与 MCP 共用的预览会话、保存重建和资源观测入口。</summary>
public sealed partial class LvglPreviewService : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, Session> sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly LvglPreviewBuilder builder;
    private readonly BuildMemoryService memory;
    private readonly string settingsPath;
    private readonly CancellationTokenSource lifetime = new();
    private LvglHostToolchain? toolchain;
    public bool UsingBundledToolchain => toolchain?.IsBundled != false;
    public string? ToolchainPath => UsingBundledToolchain
        ? File.Exists(builder.BundledCompilerPath) ? builder.BundledCompilerPath : null
        : toolchain?.GccPath;
    public string ToolchainDisplayName => UsingBundledToolchain
        ? "内置 PC GCC 13.1.0（Windows x64）" + (ToolchainPath is null ? " · 发行包缺失" : "")
        : "外部 PC GCC " + toolchain!.Version + "（显式覆盖）";
    public event EventHandler<LvglPreviewSnapshot>? Changed;

    public LvglPreviewService(ToolsetCatalog toolsets, string dataDirectory)
    {
        builder = new(toolsets);
        memory = new(toolsets);
        settingsPath = Path.Combine(Path.GetFullPath(dataDirectory), "lvgl-host-toolchain.json");
        if (File.Exists(settingsPath))
        {
            try
            {
                var settings = JsonSerializer.Deserialize<LvglPreviewToolchainSettings>(File.ReadAllText(settingsPath), JsonStore.Options);
                if (settings is { FormatVersion: 1, Mode: "external", Toolchain: { IsBundled: false } selected })
                {
                    toolchain = selected;
                }
                else if (settings is not { FormatVersion: 1, Mode: "bundled" })
                {
                    SettingsDiagnostic = "旧版或无效的外置 GCC 设置已停用，当前默认使用内置 PC 开发环境组件；可在高级设置中明确覆盖。";
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                SettingsDiagnostic = "原生开发环境组件设置读取失败：" + ex.Message;
            }
        }
    }
    public string? SettingsDiagnostic
    {
        get; private set;
    }

    public async Task ConfigureToolchainAsync(string gccPath, CancellationToken token = default)
    {
        var inspected = await LvglPreviewBuilder.InspectToolchainAsync(gccPath, token);
        await JsonStore.WriteAsync(settingsPath, new LvglPreviewToolchainSettings(1, "external", inspected), token);
        toolchain = inspected;
        SettingsDiagnostic = null;
    }

    public async Task UseBundledToolchainAsync(CancellationToken token = default)
    {
        var resolved = await builder.ResolveBundledToolchainAsync(token);
        await JsonStore.WriteAsync(settingsPath, new LvglPreviewToolchainSettings(1, "bundled"), token);
        toolchain = resolved;
        SettingsDiagnostic = null;
    }

    public Task<LvglPreviewConfiguration> ReadConfigurationAsync(string project, CancellationToken token = default) =>
        LvglPreviewBuilder.ReadConfigurationAsync(project, token);
    public async Task SaveConfigurationAsync(string project, LvglPreviewConfiguration configuration, CancellationToken token = default)
    {
        var validation = await ValidateSetupAsync(project, configuration, token);
        if (!validation.IsValid)
        {
            throw new StudioXException("LVGL_SETUP_INVALID", string.Join("\n", validation.Diagnostics
            .Where(item => item.Severity.Equals("error", StringComparison.OrdinalIgnoreCase))
            .Select(item => $"[{item.Code}] {item.Message}" + (item.Path is null ? "" : $" · {item.Path}"))));
        }
        await LvglPreviewBuilder.SaveConfigurationAsync(project, configuration, token);
    }

    public LvglPreviewSnapshot GetSnapshot(string project)
    {
        var root = Path.GetFullPath(project);
        if (!sessions.TryGetValue(root, out var session))
        {
            return new(root, "Stopped", SettingsDiagnostic ?? "PC 预览尚未启动。", false, false, "", null, null);
        }
        lock (session.Sync)
        {
            return session.Snapshot;
        }
    }

    public Task<LvglPreviewSnapshot> StartAsync(string project, CancellationToken token = default) => StartCoreAsync(project, false, token);

    private async Task<LvglPreviewSnapshot> StartCoreAsync(string project, bool automatic, CancellationToken token)
    {
        var root = Path.GetFullPath(project);
        var session = sessions.GetOrAdd(root, path => new(path));
        if (!automatic)
        {
            session.StopRequested = false;
        }
        await session.Gate.WaitAsync(token);
        try
        {
            // 排队的手动启动也遵循较晚发出的停止指令；之后的新启动会在入队前重新明确运行意图。
            if (session.StopRequested)
            {
                return GetSnapshot(root);
            }
            var configuration = await ReadConfigurationAsync(root, token);
            AcquireProjectLease(session);
            session.BuildCancellation?.Dispose();
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
            session.BuildCancellation = cancellation;
            Set(session, state => state with { State = "Building", Message = "正在增量构建 PC 预览…", Configuration = state.IsRunning ? state.Configuration : configuration, Log = "", IsStale = state.IsRunning });
            LvglPreviewBuildResult result;
            try
            {
                var selected = UsingBundledToolchain
                    ? await builder.ResolveBundledToolchainAsync(cancellation.Token, new InlineProgress(text => AppendLog(session, text + "\n")))
                    : toolchain!;
                result = await builder.BuildAsync(root, selected, new InlineProgress(text => AppendLog(session, text)), cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                Set(session, state => state with { State = state.IsRunning ? "Running" : "Stopped", Message = "PC 构建已取消。", IsStale = state.IsRunning });
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or StudioXException or JsonException)
            {
                AppendLog(session, ErrorDiagnostic(ex));
                Set(session, state => state with { State = "BuildFailed", Message = "PC 构建失败：" + ex.Message, IsStale = state.IsRunning });
                return GetSnapshot(root);
            }
            if (!result.Success)
            {
                Set(session, state => state with { State = "BuildFailed", Message = state.IsRunning ? "PC 构建失败；当前窗口保留上次成功内容。" : "PC 构建失败，请查看原始诊断。", IsStale = state.IsRunning, Log = BoundedLog(result.Log) });
                return GetSnapshot(root);
            }
            cancellation.Token.ThrowIfCancellationRequested();
            var slot = session.ActiveSlot == "a" ? "b" : "a";
            var active = Path.Combine(result.BuildDirectory, "bin", "preview-active-" + slot + ".exe");
            string workingDirectory;
            try
            {
                File.Copy(result.Executable, active, overwrite: true);
                workingDirectory = PrepareResourceSlot(result, slot);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or StudioXException)
            {
                AppendLog(session, ErrorDiagnostic(ex));
                Set(session, state => state with
                {
                    State = "BuildFailed",
                    IsStale = state.IsRunning,
                    Message = "预览资源准备失败；当前窗口保留上次成功内容。"
                });
                return GetSnapshot(root);
            }
            var candidate = new LvglNativePreviewProcess(active, workingDirectory);
            var ready = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            candidate.LineReceived += line => Receive(session, candidate, line, ready);
            candidate.Exited += code => OnExited(session, candidate, code, ready);
            try
            {
                Set(session, state => state with { State = "Starting", Message = "正在启动独立 LVGL 窗口…", Log = BoundedLog(result.Log) });
                candidate.Start();
                var info = await ready.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellation.Token);
                ValidateReady(info, configuration);
                // 截图持有旧工作目录时不能切换/回收资源；新窗口就绪后再原子替换会话。
                await session.CaptureGate.WaitAsync(cancellation.Token);
                try
                {
                    var previous = session.Process;
                    var previousExecutable = session.ActiveExecutable;
                    var previousWorkingDirectory = session.ActiveWorkingDirectory;
                    session.Process = candidate;
                    session.ActiveExecutable = active;
                    session.ActiveWorkingDirectory = workingDirectory;
                    session.ActiveSlot = slot;
                    Set(session, state => state with
                    {
                        State = "Running",
                        IsRunning = true,
                        IsStale = false,
                        Message = "PC 预览运行中 · 鼠标模拟触摸。",
                        Stats = null,
                        Configuration = configuration,
                        PointerBits = Number(info, "pointerBits"),
                        LvglVersion = Text(info, "lvglVersion"),
                        DrawBufferBytes = Integer(info, "drawBufferBytes")
                    });
                    var previousStopped = true;
                    if (previous is not null)
                    {
                        try
                        {
                            await previous.DisposeAsync();
                        }
                        catch (Exception ex) { previousStopped = false; AppendLog(session, "[旧预览进程回收诊断] " + ex + "\n"); }
                    }
                    if (previousStopped && previousWorkingDirectory is not null && previousWorkingDirectory != workingDirectory)
                    {
                        TryCleanResourceSlot(session, previousWorkingDirectory);
                    }
                    if (previousExecutable is not null && !previousExecutable.Equals(active, StringComparison.OrdinalIgnoreCase) && File.Exists(previousExecutable))
                    {
                        try
                        {
                            File.Delete(previousExecutable);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppendLog(session, "[旧预览 EXE 清理失败] " + ex + "\n"); }
                    }
                }
                finally { session.CaptureGate.Release(); }
                try
                {
                    InstallWatchers(session, configuration);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    AppendLog(session, "[保存自动重建未启用] " + ex + "\n");
                    Set(session, state => state with { Message = "预览运行中；文件监视失败，请使用手动重建。" });
                }
                return GetSnapshot(root);
            }
            catch (Exception ex)
            {
                AppendLog(session, ErrorDiagnostic(ex) + "\n");
                if (session.Process == candidate)
                {
                    session.Process = null;
                }
                await candidate.DisposeAsync();
                if (File.Exists(active))
                {
                    File.Delete(active);
                }
                TryCleanResourceSlot(session, workingDirectory);
                Set(session, state => state with
                {
                    State = "StartFailed",
                    IsRunning = session.Process?.IsRunning == true,
                    IsStale = session.Process?.IsRunning == true,
                    Message = "新预览窗口启动失败；详情见日志。"
                });
                throw;
            }
        }
        finally { session.BuildCancellation = null; ReleaseProjectLeaseIfIdle(session); session.Gate.Release(); }
    }

    public async Task StopAsync(string project, CancellationToken token = default)
    {
        var root = Path.GetFullPath(project);
        if (!sessions.TryGetValue(root, out var session))
        {
            return;
        }
        session.StopRequested = true;
        try
        {
            session.BuildCancellation?.Cancel();
        }
        catch (ObjectDisposedException) { /* 构建完成与用户停止可以同时发生。 */ }
        DisableWatchers(session);
        await session.Gate.WaitAsync(token);
        try
        {
            FailCaptures(session, new StudioXException("LVGL_STOPPED", "PC 预览已停止。"));
            await session.CaptureGate.WaitAsync(token);
            try
            {
                if (session.Process is { } process)
                {
                    session.Process = null;
                    await process.DisposeAsync();
                }
                Set(session, state => state with { State = "Stopped", IsRunning = false, Message = "PC 预览已停止。" });
                await PersistRuntimeLogAsync(session);
                if (session.ActiveWorkingDirectory is { } working)
                {
                    TryCleanResourceSlot(session, working);
                }
                session.ActiveWorkingDirectory = null;
                ReleaseProjectLeaseIfIdle(session);
            }
            finally { session.CaptureGate.Release(); }
        }
        finally { session.Gate.Release(); }
    }

    public async Task SendInputAsync(string project, LvglPreviewInput input, CancellationToken token = default)
    {
        var session = Running(project);
        var configuration = GetSnapshot(project).Configuration!;
        if (input.Type == "pointer" && (input.X < 0 || input.Y < 0 || input.X >= configuration.Width || input.Y >= configuration.Height) ||
            input.Type == "wheel" && input.Delta is < -100 or > 100 || input.Type == "key" && input.Key is < 1 or > 255 ||
            input.Type is not ("pointer" or "wheel" or "key"))
        {
            throw new StudioXException("LVGL_INPUT", "输入类型或坐标无效；坐标以预览的逻辑分辨率计算。");
        }
        await session.Process!.SendAsync(JsonSerializer.Serialize(new
        {
            command = "input",
            type = input.Type,
            x = input.X,
            y = input.Y,
            pressed = input.Pressed,
            delta = input.Delta,
            key = input.Key
        }), token);
    }

    public async Task<LvglPreviewScreenshot> CaptureAsync(string project, CancellationToken token = default)
    {
        var session = Running(project);
        await session.CaptureGate.WaitAsync(token);
        try
        {
            if (session.Process is not { IsRunning: true } process)
            {
                throw new StudioXException("LVGL_STOPPED", "PC 预览已停止。");
            }
            var request = Guid.NewGuid().ToString("N");
            var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!session.Captures.TryAdd(request, completion))
            {
                throw new InvalidOperationException();
            }
            try
            {
                await process.SendAsync(JsonSerializer.Serialize(new
                {
                    command = "screenshot",
                    requestId = request
                }), token);
                var file = await completion.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
                if (file != "frame.bmp")
                {
                    throw new StudioXException("LVGL_SCREENSHOT_PATH", "宿主返回了不受支持的截图路径。");
                }
                var build = PathBoundary.Resolve(session.Project, ".build/pc-preview");
                var path = PathBoundary.Resolve(session.ActiveWorkingDirectory ?? build, file);
                var info = new FileInfo(path);
                if (!info.Exists || info.Length is < 54 or > 20 * 1024 * 1024)
                {
                    throw new StudioXException("LVGL_SCREENSHOT", "宿主截图不存在或超出容量。");
                }
                using var bitmap = SKBitmap.Decode(path) ?? throw new StudioXException("LVGL_SCREENSHOT", "BMP 截图无法解码。");
                var configuration = GetSnapshot(project).Configuration!;
                if (bitmap.Width != configuration.Width || bitmap.Height != configuration.Height)
                {
                    throw new StudioXException("LVGL_SCREENSHOT", "截图分辨率与配置不符。");
                }
                using var image = SKImage.FromBitmap(bitmap);
                using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
                var png = Path.Combine(build, "frame.png");
                await File.WriteAllBytesAsync(png, encoded.ToArray(), token);
                return new(png, "image/png", bitmap.Width, bitmap.Height);
            }
            finally { session.Captures.TryRemove(request, out _); }
        }
        finally { session.CaptureGate.Release(); }
    }

    public async Task<LvglPreviewResourceReport> ReadResourcesAsync(string project, CancellationToken token = default)
    {
        var snapshot = GetSnapshot(project);
        var configuration = snapshot.IsRunning && snapshot.Configuration is not null ? snapshot.Configuration : await ReadConfigurationAsync(project, token);
        var targetEvidence = await LvglTargetBuildEvidenceService.ReadAsync(project, configuration, token);
        var target = targetEvidence.MatchesCurrentUi ? await memory.ReadAsync(project, token)
            : new BuildMemoryReport([], targetEvidence.Message);
        return new(target, snapshot.IsRunning ? snapshot.Stats : null, snapshot.IsRunning ? snapshot.DrawBufferBytes : DrawBufferBytes(configuration),
            checked((long)configuration.Width * configuration.Height * 4), snapshot.IsRunning ? snapshot.PointerBits : 0, snapshot.IsRunning ? snapshot.LvglVersion : null,
            "MCU Flash/静态 RAM：" + targetEvidence.Message + " PC 堆：当前独立进程的 LVGL 内存池采样；缓冲区：当前配置公式。",
            "PC 对象受宿主指针宽度和 ABI 影响；堆峰值是观测峰值。PC 帧率不代表 MCU 帧率。目标运行时栈、DMA/显示总线耗时需实机测量；静态 RAM 已含保留堆和缓冲，不重复相加。",
            LvglDisplayEstimate.Calculate(configuration), targetEvidence);
    }

    private void Receive(Session session, LvglNativePreviewProcess process, string line, TaskCompletionSource<JsonElement> ready)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (Number(root, "version") != 1)
            {
                throw new FormatException("不支持的预览协议版本。");
            }
            var kind = Text(root, "kind");
            if (kind == "ready")
            {
                ready.TrySetResult(root.Clone());
                return;
            }
            if (process != session.Process)
            {
                if (kind == "error")
                {
                    AppendLog(session, line + "\n");
                }
                return;
            }
            if (kind == "stats")
            {
                var stats = new LvglPreviewStats(Integer(root, "uptimeMs"), Integer(root, "frames"), Decimal(root, "fps"), Integer(root, "flushPixels"),
                    Integer(root, "heapUsedBytes"), Integer(root, "heapFreeBytes"), Integer(root, "heapLargestFreeBytes"), Integer(root, "heapObservedPeakBytes"),
                    Decimal(root, "heapFragmentationPercent"), Integer(root, "warningCount"), Integer(root, "errorCount"),
                    !root.TryGetProperty("heapAvailable", out var available) || available.GetBoolean());
                Set(session, state => state with { Stats = stats });
            }
            else if (kind == "screenshot")
            {
                var request = Text(root, "requestId");
                if (request is not null && session.Captures.TryGetValue(request, out var completion))
                {
                    completion.TrySetResult(Text(root, "file") ?? "");
                }
            }
            else if (kind == "error")
            {
                AppendLog(session, line + "\n");
            }
            else if (kind is not "stopped")
            {
                AppendLog(session, line + "\n");
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or OverflowException)
        {
            AppendLog(session, line + "\n[宿主输出未识别：" + ex.Message + "]\n");
        }
    }

    private void OnExited(Session session, LvglNativePreviewProcess process, int code, TaskCompletionSource<JsonElement> ready)
    {
        ready.TrySetException(new StudioXException("LVGL_PROCESS_EXIT", "预览宿主在就绪前退出，退出码：" + code));
        if (process != session.Process)
        {
            return;
        }
        FailCaptures(session, new StudioXException("LVGL_PROCESS_EXIT", "预览进程已退出。"));
        DisableWatchers(session);
        Set(session, state => state with
        {
            State = code == 0 ? "Stopped" : "Crashed",
            IsRunning = false,
            Message = "PC 预览窗口已退出，退出码：" + code
        });
        _ = PersistRuntimeLogAsync(session);
        lock (session.Sync)
        {
            if (session.Gate.CurrentCount != 0)
            {
                ReleaseProjectLeaseIfIdle(session);
            }
        }
    }

    private void InstallWatchers(Session session, LvglPreviewConfiguration configuration)
    {
        DisableWatchers(session);
        if (!configuration.AutoRebuild)
        {
            return;
        }
        var lvgl = Path.GetFullPath(Path.Combine(session.Project, configuration.LvglDirectory));
        foreach (var root in new[] { session.Project, lvgl }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var watcher = new FileSystemWatcher(root) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
            watcher.Changed += (_, e) => ScheduleRebuild(session, e.FullPath);
            watcher.Created += (_, e) => ScheduleRebuild(session, e.FullPath);
            watcher.Deleted += (_, e) => ScheduleRebuild(session, e.FullPath);
            watcher.Renamed += (_, e) => { ScheduleRebuild(session, e.OldFullPath); ScheduleRebuild(session, e.FullPath); };
            watcher.Error += (_, e) =>
            {
                AppendLog(session, "[文件监视溢出，请手动重建] " + e.GetException().Message + "\n");
                Set(session, state => state with { IsStale = true, Message = "文件监视溢出；当前窗口可能过期，请手动重建。" });
            };
            watcher.EnableRaisingEvents = true;
            session.Watchers.Add(watcher);
        }
    }

    private void ScheduleRebuild(Session session, string path)
    {
        var relative = Path.GetRelativePath(session.Project, path).Replace('\\', '/');
        if (relative.StartsWith(".build/", StringComparison.OrdinalIgnoreCase) || relative.StartsWith(".git/", StringComparison.OrdinalIgnoreCase) ||
            relative.StartsWith(".studiox/", StringComparison.OrdinalIgnoreCase) && relative != LvglPreviewBuilder.ConfigurationPath)
        {
            return;
        }
        LvglPreviewConfiguration? activeConfiguration;
        lock (session.Sync)
        {
            activeConfiguration = session.Snapshot.Configuration;
        }
        var isResource = activeConfiguration?.ResourceDirectories?.Any(directory =>
        {
            var absolute = Path.GetFullPath(directory, session.Project).TrimEnd(Path.DirectorySeparatorChar);
            return path.Equals(absolute, StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(absolute + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }) == true;
        if (!isResource && relative != LvglPreviewBuilder.ConfigurationPath && Path.GetExtension(path).ToLowerInvariant() is not (".c" or ".h" or ".cpp" or ".cc" or ".cxx" or ".hpp"))
        {
            return;
        }
        CancellationTokenSource debounce;
        lock (session.Sync)
        {
            if (session.Process?.IsRunning != true)
            {
                return;
            }
            session.Debounce?.Cancel();
            session.Debounce = debounce = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        }
        Set(session, state => state with { IsStale = true, Message = "源文件已保存，准备增量重建…" });
        _ = RebuildAfterDelayAsync(session, debounce);
    }

    private async Task RebuildAfterDelayAsync(Session session, CancellationTokenSource debounce)
    {
        try
        {
            await Task.Delay(600, debounce.Token);
            await StartCoreAsync(session.Project, true, debounce.Token);
        }
        catch (OperationCanceledException) when (debounce.IsCancellationRequested) { }
        catch (Exception ex)
        {
            AppendLog(session, ErrorDiagnostic(ex));
            Set(session, state => state with { IsStale = state.IsRunning, State = "BuildFailed", Message = "自动重建失败：" + ex.Message });
        }
        finally { lock (session.Sync) { if (session.Debounce == debounce) { session.Debounce = null; } } debounce.Dispose(); }
    }

    private static void DisableWatchers(Session session)
    {
        lock (session.Sync)
        {
            session.Debounce?.Cancel();
            foreach (var watcher in session.Watchers)
            {
                watcher.Dispose();
            }
            session.Watchers.Clear();
        }
    }

    private Session Running(string project)
    {
        if (!sessions.TryGetValue(Path.GetFullPath(project), out var session) || session.Process?.IsRunning != true)
        {
            throw new StudioXException("LVGL_NOT_RUNNING", "请先启动当前工程的 PC 预览。");
        }
        return session;
    }
    private void AppendLog(Session session, string text) => Set(session, state => state with { Log = BoundedLog(state.Log + text) });
    private static string ErrorDiagnostic(Exception error) => (error is StudioXException studio ? "[" + studio.Code + "] " : "") + error;
    private static string BoundedLog(string log) => log.Length <= 256 * 1024 ? log : "[较早的会话输出已截断；完整构建日志保存在 .build/pc-preview/build.log]\n" + log[^(256 * 1024)..];
    private void Set(Session session, Func<LvglPreviewSnapshot, LvglPreviewSnapshot> update)
    {
        LvglPreviewSnapshot snapshot;
        lock (session.Sync)
        {
            snapshot = session.Snapshot = update(session.Snapshot);
        }
        Changed?.Invoke(this, snapshot);
    }
    private static void FailCaptures(Session session, Exception error)
    {
        foreach (var completion in session.Captures.Values)
        {
            completion.TrySetException(error);
        }
    }
    private static void AcquireProjectLease(Session session)
    {
        lock (session.Sync)
        {
            if (session.Lease is not null)
            {
                return;
            }
            var directory = PathBoundary.Resolve(session.Project, ".build/pc-preview");
            Directory.CreateDirectory(directory);
            try
            {
                session.Lease = new FileStream(PathBoundary.Resolve(directory, "session.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33)
            {
                throw new StudioXException("LVGL_PREVIEW_IN_USE", "另一个 StudioX 或 MCP 客户端正在预览该工程。请先停止其预览，再由本客户端启动。", ex);
            }
        }
    }
    private static void ReleaseProjectLeaseIfIdle(Session session)
    {
        lock (session.Sync)
        {
            if (session.Process?.IsRunning == true)
            {
                return;
            }
            session.Lease?.Dispose();
            session.Lease = null;
        }
    }
    private static async Task PersistRuntimeLogAsync(Session session)
    {
        try
        {
            string log;
            lock (session.Sync)
            {
                log = session.Snapshot.Log;
            }
            var directory = PathBoundary.Resolve(session.Project, ".build/pc-preview");
            if (Directory.Exists(directory))
            {
                await File.WriteAllTextAsync(Path.Combine(directory, "runtime.log"), log);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or StudioXException)
        {
            lock (session.Sync)
            {
                session.Snapshot = session.Snapshot with
                {
                    Message = session.Snapshot.Message + "；日志保存失败：" + ex.Message
                };
            }
        }
    }
    private static string? Text(JsonElement value, string name) => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    private static long Integer(JsonElement value, string name) => value.TryGetProperty(name, out var property) ? property.GetInt64() : 0;
    private static int Number(JsonElement value, string name) => checked((int)Integer(value, name));
    private static double Decimal(JsonElement value, string name) => value.TryGetProperty(name, out var property) ? property.GetDouble() : 0;
    private static long DrawBufferBytes(LvglPreviewConfiguration configuration) => checked((long)configuration.Width * configuration.DrawBufferRows * configuration.DrawBufferCount * (configuration.ColorDepth / 8));
    private static void ValidateReady(JsonElement info, LvglPreviewConfiguration configuration)
    {
        if (Number(info, "version") != 1 || Number(info, "width") != configuration.Width || Number(info, "height") != configuration.Height ||
            Number(info, "colorDepth") != configuration.ColorDepth || Integer(info, "drawBufferBytes") != DrawBufferBytes(configuration) ||
            Number(info, "pointerBits") != 64 || Text(info, "lvglVersion")?.StartsWith("8.3.", StringComparison.Ordinal) != true)
        {
            throw new StudioXException("LVGL_READY_MISMATCH", "预览宿主返回的协议、分辨率、颜色或缓冲区与当前配置不匹配。\n" + info.GetRawText());
        }
    }

    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        foreach (var session in sessions.Values)
        {
            await StopAsync(session.Project);
        }
        lifetime.Dispose();
    }
    private sealed class InlineProgress(Action<string> action) : IProgress<string>
    {
        public void Report(string value) => action(value);
    }
    private sealed class Session(string project)
    {
        public string Project { get; } = project;
        public object Sync { get; } = new();
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public SemaphoreSlim CaptureGate { get; } = new(1, 1);
        public LvglPreviewSnapshot Snapshot { get; set; } = new(project, "Stopped", "PC 预览尚未启动。", false, false, "", null, null);
        public LvglNativePreviewProcess? Process
        {
            get; set;
        }
        public string? ActiveExecutable
        {
            get; set;
        }
        public string? ActiveWorkingDirectory
        {
            get; set;
        }
        public string? ActiveSlot
        {
            get; set;
        }
        public CancellationTokenSource? BuildCancellation
        {
            get; set;
        }
        public CancellationTokenSource? Debounce
        {
            get; set;
        }
        public volatile bool StopRequested;
        public List<FileSystemWatcher> Watchers { get; } = [];
        public ConcurrentDictionary<string, TaskCompletionSource<string>> Captures { get; } = new();
        public FileStream? Lease
        {
            get; set;
        }
    }
}
