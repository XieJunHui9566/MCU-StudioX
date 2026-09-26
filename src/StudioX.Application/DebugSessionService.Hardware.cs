namespace StudioX.Application;

using StudioX.Engine.Debugging;
using StudioX.Foundation;

public sealed partial class DebugSessionService
{
    public bool IsHardware { get; private set; }
    public string? SessionLogPath { get; private set; }
    public string? HardwareTargetName { get; private set; }
    public DebugTargetProfile? HardwareTarget { get; private set; }
    private string ModeSuffix => IsHardware ? "（实机）" : "（模拟）";
    public async Task StartHardwareAsync(HardwareDebugPreparation preparation, CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        if (IsActive) { gate.Release(); throw Error("调试会话已经启动。"); }
        try
        {
            if (!string.Equals(ProjectDirectory, preparation.ProjectDirectory, StringComparison.OrdinalIgnoreCase)) throw Error("调试准备与当前工程不一致。");
            var probe = OpenOcdDebugPlanner.ResolveProbe(preparation.Configuration);
            HardwareTarget = OpenOcdDebugPlanner.ResolveTarget(preparation.Configuration);
            IsHardware = true; SessionLogPath = preparation.LogPath;
            HardwareTargetName = probe.DisplayName + " / " + preparation.Configuration.Device.Id;
            SetState(DebugState.Starting, "连接 " + probe.DisplayName + " / OpenOCD，核对板上固件…");
            bound.Clear(); pauseRequested = false;
            breakpoints = breakpoints.Where(b => !b.SessionOnly).Select(Unbound).ToArray();
            var (transport, plan) = await GdbProcessTransport.StartAsync(preparation, token);
            var current = new GdbDebugAdapter(transport, HardwareTarget); adapter = current;
            current.Trace += Trace;
            foreach (var command in plan.InitializeCommands)
            {
                if (command.Contains("monitor verify_image ", StringComparison.Ordinal))
                    await VerifyImageAsync(current, command, preparation, token);
                else await current.SendAsync(command, token);
            }
            var sources = await current.SendAsync("-file-list-exec-source-files", token);
            if (sources.Get("files")?.Values.Any() != true) throw Error("ELF 不含源码调试信息，请使用 Debug 配置重新编译。");
            recordPump ??= ReadRecordsAsync();
            receiver = record => records.Writer.TryWrite((current, record)); current.RecordReceived += receiver;
            for (var i = 0; i < breakpoints.Length; i++) breakpoints[i] = await BindAsync(breakpoints[i], token);
            Snapshot = NormalizeSnapshot(await current.ReadAsync(watches, 0, token));
            Trace("实机会话：" + HardwareTargetName + "。板上映像与当前 ELF 校验一致；未下载。日志：" + SessionLogPath);
            SetState(DebugState.Stopped, "已连接并暂停 · 实机固件校验通过");
        }
        catch (Exception ex)
        {
            Trace(ex.ToString());
            try { await ReleaseAdapterAsync(); } catch (Exception cleanup) { Trace("连接失败后的清理：" + cleanup); }
            Snapshot = DebugSnapshot.Empty;
            SetState(DebugState.Faulted, "实机连接失败：" + ex.Message); throw;
        }
        finally { gate.Release(); }
    }
    private string NormalizeSource(string file)
    {
        if (ProjectDirectory is null || string.IsNullOrWhiteSpace(file)) return file;
        return (Path.IsPathRooted(file) ? Path.GetRelativePath(ProjectDirectory, file) : file).Replace('\\', '/');
    }
    internal static StudioXException ExplainImageVerificationFailure(StudioXException failure, string logPath)
    {
        // MI 的 ^error 常只有「monitor command failed」；只有原始会话输出能区分传输失败和逐字节差异。
        var diagnostics = failure.Message;
        try
        {
            if (File.Exists(logPath))
            {
                var text = File.ReadAllText(logPath);
                var marker = text.LastIndexOf("monitor verify_image ", StringComparison.Ordinal);
                if (marker >= 0) diagnostics += "\n" + text[marker..];
            }
        }
        catch (IOException) { /* 日志读取失败时保留未确认结论及原始异常。 */ }
        catch (UnauthorizedAccessException) { /* 同上。 */ }
        var usbFailure = new[] { "error reading USB data", "error writing USB data", "CMD_INFO failed", "CMD_CONNECT failed",
            "CMD_DISCONNECT failed", "LIBUSB_ERROR", "could not read product string", "error reading adapter response" }
            .Any(marker => diagnostics.Contains(marker, StringComparison.OrdinalIgnoreCase));
        if (usbFailure)
            return new("DEBUG_IMAGE_VERIFY_TRANSPORT", "调试探针的 USB 通信中断，固件校验未完成；本次无法判断板上固件是否匹配。请检查探针供电和 USB 连接。原始日志：" + logPath, failure);
        if (diagnostics.Contains("diff 0 address 0x", StringComparison.OrdinalIgnoreCase))
            return new("DEBUG_IMAGE_MISMATCH", "板上读回的固件字节与当前 ELF 不一致。请先确认探针连接稳定，再决定是否下载当前固件。原始日志：" + logPath, failure);
        return new("DEBUG_IMAGE_VERIFY", "固件校验未完成，无法判断是映像不同还是通信故障。请查看 OpenOCD 原始日志：" + logPath, failure);
    }
    private DebugSnapshot NormalizeSnapshot(DebugSnapshot value) => value with { Frames = value.Frames.Select(f => f with { File = NormalizeSource(f.File) }).ToArray() };
}
