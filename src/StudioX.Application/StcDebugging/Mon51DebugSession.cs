namespace StudioX.Application.StcDebugging;

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using StudioX.Devices;
using StudioX.Engine.Debugging;
using StudioX.Foundation;

/// <summary>IAP15F2K61S2 监控调试会话；源码须与目标匹配，断点原字节先落盘，结束时恢复并释放串口。</summary>
public sealed partial class Mon51DebugSession(string dataDirectory) : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly ConcurrentDictionary<ushort, Mon51Breakpoint> points = new();
    private Mon51Client? client;
    private CancellationTokenSource? runningLifetime;
    private Task? runningTask;
    private string? recoveryPath;
    private string? projectPath;
    private bool disposed;
    public DebugState State
    {
        get; private set;
    }
    public bool HasConnection => client is not null;
    public string Reason { get; private set; } = "未连接 Mon51";
    public DebugSnapshot Snapshot { get; private set; } = DebugSnapshot.Empty;
    public string Version { get; private set; } = "";
    public bool IsSimulated
    {
        get; private set;
    }
    public string Port { get; private set; } = "";
    public string LogPath { get; private set; } = "";
    public string InstructionBytes { get; private set; } = "";
    public ushort Pc
    {
        get; private set;
    }
    public IReadOnlyList<Mon51Breakpoint> Breakpoints => points.Values.OrderBy(p => p.Address).ToArray();
    public event Action? Changed;
    public event Action<string>? Output;

    public async Task ConnectAsync(DeviceHub devices, IDeviceTransport transport, string project, CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            if (client is not null || disposed)
            {
                throw new StudioXException("MON51_STATE", "此监控会话不能重复连接。");
            }
            Port = transport.Key;
            IsSimulated = transport.IsSimulated;
            projectPath = Path.GetFullPath(project);
            // 恢复记录按连接保存，换工程也不能绕过同一串口上的遗留断点。
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("IAP15F2K61S2|" + Port.ToUpperInvariant()))).ToLowerInvariant();
            recoveryPath = Path.Combine(dataDirectory, "debug", "mon51-recovery", key + ".json");
            downloadRecoveryPath = Path.Combine(dataDirectory, "debug", "mon51-download-recovery", key + ".json");
            if (File.Exists(downloadRecoveryPath))
            {
                userProgramVerified = false;
                if (!UserDownloadRequested)
                {
                    throw new StudioXException("MON51_DOWNLOAD_RECOVERY", "上次用户程序下载未完成核对，请明确选择重新下载完整固件，不能普通附加并续跑：" + downloadRecoveryPath);
                }
            }
            if (File.Exists(recoveryPath))
            {
                throw new StudioXException("MON51_RECOVERY", "存在未确认恢复的断点记录，请先检查对应目标和原字节：" + recoveryPath);
            }
            LogPath = Path.Combine(dataDirectory, "debug", "mon51-logs", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".log");
            SetState(DebugState.Starting, "正在连接 Mon51：暂停并核对监控版本…");
            var session = await devices.OpenAsync(transport, token);
            try
            {
                var parameters = transport is SerialTransport serial
                    ? $"baud={serial.Settings.BaudRate} · dataBits={serial.Settings.DataBits} · parity={serial.Settings.Parity} · stopBits={serial.Settings.StopBits} · flow={serial.Settings.FlowControl} · DTR={serial.Settings.Dtr} · RTS={serial.Settings.Rts}"
                    : transport.IsSimulated ? "protocol fixture" : "parameters unavailable";
                client = new Mon51Client(session, LogPath, parameters);
            }
            catch { await session.DisposeAsync(); throw; }
            Version = await client.SynchronizeAsync(true, token);
            await ReadSnapshotAsync(token);
            // 冷启动上下文的 PC=0 尚未经过监控的用户复位映射；直接 GO 会执行物理监控入口。
            // SetPc(0) 由原厂监控映射至 DBFD 跳板，仅修改执行上下文，不改写 Flash 或外设。
            if (Pc == 0)
            {
                await SetPcCoreAsync(0, token);
                Output?.Invoke("冷启动用户入口已映射至 CODE 0xDBFD，保持暂停。");
            }
            SetState(DebugState.Stopped, $"Mon51 {Version} 已连接 · PC=0x{Pc:X4} · 地址调试");
            Output?.Invoke("附加到已有程序；未执行下载。原始通信日志：" + LogPath);
        }
        catch (Exception ex)
        {
            if (client is not null)
            {
                await client.DisposeAsync();
                client = null;
            }
            SetState(DebugState.Faulted, "连接失败，目标状态未确认：" + ex.Message);
            Output?.Invoke(ex.ToString());
            Output?.Invoke("连接原始日志：" + LogPath);
            throw;
        }
        finally { gate.Release(); }
    }

    public async Task ExecuteAsync(DebugAction action, CancellationToken token = default)
    {
        if (action == DebugAction.Pause)
        {
            stepLifetime?.Cancel();
        }
        await gate.WaitAsync(token);
        try
        {
            if (action == DebugAction.Pause && State == DebugState.Stopped)
            {
                return;
            }
            RequireClient();
            if (action == DebugAction.Pause)
            {
                if (State != DebugState.Running)
                {
                    throw new StudioXException("MON51_STATE", "目标未在运行。");
                }
                runningLifetime?.Cancel();
                await client!.SynchronizeAsync(true, token);
                await RestoreAsync(token);
                ClearTemporaryStop();
                selectedFrame = 0;
                await ReadSnapshotAsync(token);
                SetState(DebugState.Stopped, $"已暂停 · PC=0x{Pc:X4}");
                return;
            }
            RequireStopped();
            // 恢复执行后条件求值和源码箭头必须回到当前执行帧，不沿用用户浏览过的调用者。
            selectedFrame = 0;
            switch (action)
            {
                case DebugAction.StepInto:
                    await StepSourceAsync(false, token);
                    break;
                case DebugAction.StepOver:
                    await StepSourceAsync(true, token);
                    break;
                case DebugAction.StepOut:
                    await StepOutCoreAsync(token);
                    break;
                case DebugAction.Reset:
                    await SetPcCoreAsync(0, token);
                    SetState(DebugState.Stopped, "逻辑复位入口已设置 · PC=0xDBFD · 尚未执行；外设未复位");
                    break;
                case DebugAction.Continue:
                    await ContinueCoreAsync(token);
                    break;
                default:
                    throw new StudioXException("MON51_ACTION", "不支持的调试操作。");
            }
        }
        catch (StudioXException ex) when (ex.Code is "MON51_CAPABILITY" or "MON51_STACK" or "MON51_SOURCE_CHANGED" or "MON51_INSTRUCTION" or "MON51_BREAKPOINT_UNBOUND")
        {
            Output?.Invoke(ex.Message);
            throw;
        }
        catch (Exception ex)
        {
            SetState(DebugState.Faulted, "监控操作未完整确认；请结束会话恢复断点：" + ex.Message);
            Output?.Invoke(ex.ToString());
            throw;
        }
        finally { gate.Release(); }
    }

    private async Task ObserveStopAsync(CancellationToken token)
    {
        try
        {
            while (true)
            {
                await Task.Delay(100, token);
                await gate.WaitAsync(token);
                try
                {
                    if (State != DebugState.Running)
                    {
                        return;
                    }
                    if (!client!.TakeStopSignal())
                    {
                        continue;
                    }
                    // 运行 ACK 和延时均不表示停止。异步通知后重新握手并读取 PC，才发布暂停状态。
                    await client.SynchronizeAsync(false, token);
                    selectedFrame = 0;
                    var before = Snapshot;
                    await ReadSnapshotAsync(token);
                    var hit = points.ContainsKey(Pc);
                    await RestoreAsync(token);
                    await ReadSnapshotAsync(token, before);
                    if (await HandleSourceStopAsync(token))
                    {
                        continue;
                    }
                    var description = temporaryStop == Pc ? temporaryReason : sourcePoints.Any(p => p.Verified && SourceAddress(p) == Pc) ? "源码断点命中" : hit ? "地址断点命中" : "监控程序停止（未匹配本会话断点）";
                    ClearTemporaryStop();
                    SetState(DebugState.Stopped, $"{description} · PC=0x{Pc:X4}");
                    return;
                }
                finally { gate.Release(); }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            SetState(DebugState.Faulted, "异步停止检查失败；断点恢复尚未确认：" + ex.Message);
            Output?.Invoke(ex.ToString());
        }
    }

    public async Task RefreshAsync(CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            RequireStopped();
            await ReadSnapshotAsync(token);
            Changed?.Invoke();
        }
        catch (Exception ex) { SetState(DebugState.Faulted, "读取寄存器失败：" + ex.Message); throw; }
        finally { gate.Release(); }
    }

    public async Task<byte[]> ReadMemoryAsync(Mon51MemorySpace space, ushort address, int count, CancellationToken token = default)
    {
        Mon51Client.ValidateRange(space, address, count);
        await gate.WaitAsync(token);
        try
        {
            RequireStopped();
            return await client!.ReadAsync(space, address, count, token);
        }
        finally { gate.Release(); }
    }

    public async Task SetPcAsync(ushort address, CancellationToken token = default)
    {
        if (address >= 0xdbfd)
        {
            throw new StudioXException("MON51_PC_RANGE", "PC 超出用户程序区。");
        }
        await gate.WaitAsync(token);
        try
        {
            RequireStopped();
            await SetPcCoreAsync(address, token);
            SetState(DebugState.Stopped, $"PC 已设置为 0x{Pc:X4}；尚未执行");
        }
        catch (Exception ex) { SetState(DebugState.Faulted, "PC 设置未完整确认：" + ex.Message); throw; }
        finally { gate.Release(); }
    }

    public async Task AddBreakpointAsync(ushort address, CancellationToken token = default)
    {
        if (address is < 3 or >= 0xdbfd)
        {
            throw new StudioXException("MON51_BREAKPOINT_RANGE", "断点地址范围为 CODE 0x0003–0xDBFC。");
        }
        await gate.WaitAsync(token);
        try
        {
            RequireStopped();
            if (symbols?.FunctionAt(address) is not null && !sourceInstructions.ContainsKey(address))
            {
                throw new StudioXException("MON51_BREAKPOINT_BOUNDARY", "该地址处于已核对指令的中间，不能布置 A5 断点。");
            }
            if (points.ContainsKey(address))
            {
                manualPoints.Add(address);
                return;
            }
            if (points.Count >= 16)
            {
                throw new StudioXException("MON51_BREAKPOINT_LIMIT", "当前最多设置 16 个地址断点。");
            }
            var original = (await client!.ReadAsync(Mon51MemorySpace.Code, address, 1, token))[0];
            if (original == 0xa5)
            {
                throw new StudioXException("MON51_BREAKPOINT_ORIGINAL", "目标地址已是 A5，请先确认它是否为遗留断点；不能作为原指令保存。");
            }
            points.TryAdd(address, new(address, original));
            manualPoints.Add(address);
            Changed?.Invoke();
        }
        finally { gate.Release(); }
    }

    public async Task RemoveBreakpointAsync(ushort address, CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            RequireStopped();
            manualPoints.Remove(address);
            if (temporaryStop != address && !sourcePoints.Any(p => p.Enabled && p.Verified && SourceAddress(p) == address))
            {
                points.TryRemove(address, out _);
            }
            Changed?.Invoke();
        }
        finally { gate.Release(); }
    }

    private async Task SetPcCoreAsync(ushort address, CancellationToken token)
    {
        await client!.SetPcAsync(address, token);
        selectedFrame = 0;
        await ReadSnapshotAsync(token);
        var expected = address == 0 ? 0xdbfd : address;
        if (Pc != expected)
        {
            throw new StudioXException("MON51_PC_VERIFY", $"PC 回读 0x{Pc:X4} 与预期 0x{expected:X4} 不一致。");
        }
    }

    private async Task StepCoreAsync(CancellationToken token)
    {
        var reply = await client!.StepAsync(token);
        await ReadSnapshotAsync(token);
        if ((reply[0] << 8 | reply[1]) != Pc)
        {
            throw new StudioXException("MON51_STEP_VERIFY", "单步应答 PC 与寄存器快照不一致。");
        }
    }

    private async Task ReadSnapshotAsync(CancellationToken token, DebugSnapshot? comparison = null)
    {
        var raw = await client!.RegistersAsync(token);
        Pc = (ushort)(raw[12] << 8 | raw[13]);
        var rows = new List<DebugRegister> { new("PC", $"0x{Pc:X4}"), new("A", $"0x{raw[0]:X2}"), new("B", $"0x{raw[1]:X2}"), new("DPTR", $"0x{raw[10]:X2}{raw[11]:X2}"), new("PSW", $"0x{raw[14]:X2}"), new("SP", $"0x{raw[15]:X2}") };
        rows.AddRange(Enumerable.Range(0, 8).Select(i => new DebugRegister("R" + i, $"0x{raw[2 + i]:X2}")));
        rows.Add(new("CY", ((raw[14] >> 7) & 1).ToString()));
        rows.Add(new("AC", ((raw[14] >> 6) & 1).ToString()));
        rows.Add(new("OV", ((raw[14] >> 2) & 1).ToString()));
        rows.Add(new("P", (raw[14] & 1).ToString()));
        rows.Add(new("寄存器组", ((raw[14] >> 3) & 3).ToString()));
        rows = rows.Select(r => r with { Changed = (comparison ?? Snapshot).Registers.Any(p => p.Name == r.Name && p.Value != r.Value) }).ToList();
        Snapshot = new(rows.ToArray(), [], [], []);
        InstructionBytes = Pc < 0xdc00 ? Convert.ToHexString(await client.ReadAsync(Mon51MemorySpace.Code, Pc, Math.Min(8, 0xdc00 - Pc), token)) : "PC 位于用户 CODE 区外";
        rawRegisters = raw;
        await ReadSourceSnapshotAsync(comparison, token);
    }

    private async Task SaveRecoveryAsync(CancellationToken token)
    {
        var installed = points.Values.Where(p => p.Installed).ToArray();
        if (installed.Length == 0)
        {
            if (File.Exists(recoveryPath))
            {
                File.Delete(recoveryPath!);
            }
            return;
        }
        await JsonStore.WriteAsync(recoveryPath!, new
        {
            FormatVersion = 1,
            Port,
            Project = projectPath,
            Target = "IAP15F2K61S2",
            Version,
            LogPath,
            Breakpoints = installed
        }, token);
    }

    private async Task InstallAsync(CancellationToken token)
    {
        foreach (var point in points.Values.ToArray())
        {
            if ((await client!.ReadAsync(Mon51MemorySpace.Code, point.Address, 1, token))[0] != point.Original)
            {
                throw new StudioXException("MON51_CODE_CHANGED", $"CODE 0x{point.Address:X4} 与设置断点时不一致，请重新连接并核对程序。");
            }
            points[point.Address] = point with
            {
                Installed = true
            };
            await SaveRecoveryAsync(token);
            await client.WriteBreakpointByteAsync(point.Address, 0xa5, token);
        }
    }

    private async Task RestoreAsync(CancellationToken token)
    {
        foreach (var point in points.Values.Where(p => p.Installed).ToArray())
        {
            var actual = (await client!.ReadAsync(Mon51MemorySpace.Code, point.Address, 1, token))[0];
            if (actual != 0xa5 && actual != point.Original)
            {
                throw new StudioXException("MON51_RESTORE_CHANGED", $"CODE 0x{point.Address:X4} 已变为 {actual:X2}，无法确认目标程序一致；保留原字节 {point.Original:X2} 的恢复记录。");
            }
            if (actual == 0xa5)
            {
                await client.WriteBreakpointByteAsync(point.Address, point.Original, token);
            }
            points[point.Address] = point with
            {
                Installed = false
            };
            await SaveRecoveryAsync(token);
        }
    }

    public async Task StopAsync()
    {
        stepLifetime?.Cancel();
        runningLifetime?.Cancel();
        await gate.WaitAsync();
        try
        {
            if (client is null)
            {
                if (points.Values.Any(p => p.Installed))
                {
                    throw new StudioXException("MON51_RECOVERY", "串口已关闭，但断点原字节恢复尚未确认：" + recoveryPath);
                }
                return;
            }
            if (!userProgramVerified)
            {
                await client.DisposeAsync();
                client = null;
                SetState(DebugState.Disconnected, "用户程序下载未核对完成 · 未执行续跑 · 串口已释放，请重新下载完整固件");
                return;
            }
            SetState(DebugState.Stopping, "正在暂停、恢复断点并释放串口…");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await client.SynchronizeAsync(true, timeout.Token);
            await RestoreAsync(timeout.Token);
            await client.RunAsync(timeout.Token);
            await client.DisposeAsync();
            client = null;
            SetState(DebugState.Disconnected, "调试已结束 · 原指令已恢复 · 用户程序继续运行 · 串口已释放");
        }
        catch (Exception ex)
        {
            SetState(DebugState.Faulted, "结束未完整确认：" + ex.Message + "；恢复记录：" + recoveryPath);
            Output?.Invoke(ex.ToString());
            throw;
        }
        finally
        {
            try
            {
                if (client is not null)
                {
                    await client.DisposeAsync();
                    client = null;
                }
            }
            finally { gate.Release(); }
        }
        if (runningTask is not null)
        {
            await runningTask;
        }
        runningLifetime?.Dispose();
        runningLifetime = null;
        runningTask = null;
    }

    private void RequireClient()
    {
        if (client is null)
        {
            throw new StudioXException("MON51_STATE", "监控串口未连接。");
        }
    }
    private void RequireStopped()
    {
        RequireClient();
        if (State != DebugState.Stopped)
        {
            throw new StudioXException("MON51_STATE", "暂停后才可操作寄存器、内存和断点。");
        }
    }
    private void SetState(DebugState state, string reason)
    {
        State = state;
        Reason = reason;
        Changed?.Invoke();
    }
    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }
        try
        {
            await StopAsync();
        }
        finally { disposed = true; runningLifetime?.Dispose(); }
    }
}
