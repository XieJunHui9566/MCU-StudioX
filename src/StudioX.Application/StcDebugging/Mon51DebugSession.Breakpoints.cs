namespace StudioX.Application.StcDebugging;

using System.Text;
using StudioX.Engine.Debugging;
using StudioX.Foundation;

public sealed partial class Mon51DebugSession
{
    private ushort? temporaryStop;
    private int? temporarySp;
    private string temporaryReason = "临时停止位置已到达";

    private ushort? SourceAddress(SourceBreakpoint point) => point.BoundLocation is { } location ? symbols?.Bind(Path.Combine(projectPath!, location.File), location.Line)?.Address : null;
    private void ClearTemporaryStop()
    {
        var address = temporaryStop;
        temporaryStop = null;
        temporarySp = null;
        if (address is { } value && !manualPoints.Contains(value) && !sourcePoints.Any(p => p.Enabled && p.Verified && SourceAddress(p) == value))
        {
            points.TryRemove(value, out _);
        }
    }

    private async Task EnsurePointAsync(ushort address, CancellationToken token)
    {
        if (address is < 3 or >= 0xdbfd)
        {
            throw new StudioXException("MON51_BREAKPOINT_RANGE", "断点必须避开复位向量与监控跳板。");
        }
        if (points.ContainsKey(address))
        {
            return;
        }
        if (points.Count >= 16)
        {
            throw new StudioXException("MON51_BREAKPOINT_LIMIT", "最多同时布置 16 个不同地址的断点（含内部临时断点）。");
        }
        var original = (await client!.ReadAsync(Mon51MemorySpace.Code, address, 1, token))[0];
        if (original == 0xa5)
        {
            throw new StudioXException("MON51_BREAKPOINT_ORIGINAL", "目标字节已是 A5，不能确认为原指令。");
        }
        if (artifact is { } image && image.Present[address] && image.Code[address] != original)
        {
            throw new StudioXException("MON51_IMAGE_MISMATCH", "断点位置的指令与已加载的固件不同。");
        }
        points[address] = new(address, original);
    }

    private async Task RebindSourcePointsAsync(CancellationToken token)
    {
        foreach (var address in points.Keys.Where(a => !manualPoints.Contains(a) && a != temporaryStop).ToArray())
        {
            points.TryRemove(address, out _);
        }
        var result = new List<SourceBreakpoint>();
        foreach (var point in sourcePoints)
        {
            var location = symbols?.Bind(PathBoundary.Resolve(projectPath!, point.File), point.Line);
            var next = point with
            {
                Verified = location is not null,
                BoundLocation = location is null ? null : new(point.File, location.Line),
                Message = location is null ? symbols is null ? "未绑定：" + SymbolsStatus : "该行无完整可执行指令，可能已被优化" : $"Mon51 软件断点 · CODE 0x{location.Address:X4}"
            };
            if (point.Enabled && location is not null)
            {
                if (!points.ContainsKey(location.Address) && points.Count >= 16)
                {
                    next = next with
                    {
                        Verified = false,
                        BoundLocation = null,
                        Message = "不同地址的断点超过 16 个；请禁用其它断点后重新启用此项"
                    };
                }
                else
                {
                    await EnsurePointAsync(location.Address, token);
                }
            }
            result.Add(next);
        }
        sourcePoints = result.ToArray();
    }

    public async Task ConfigureSourceBreakpointAsync(SourceBreakpoint point, CancellationToken token = default)
    {
        new BreakpointOptions(point.Condition, point.IgnoreCount, point.Temporary, point.LogMessage).Validate();
        _ = PathBoundary.Resolve(projectPath!, point.File);
        if (point.Line < 1 || point.Line > 1000000)
        {
            throw new StudioXException("MON51_BREAKPOINT", "源码行无效。");
        }
        await gate.WaitAsync(token);
        try
        {
            RequireStopped();
            if (!HasSymbols)
            {
                throw new StudioXException("MON51_CAPABILITY", "请先核对并加载 SDCC CDB 源码符号。");
            }
            var previous = sourcePoints;
            sourcePoints = [.. sourcePoints.Where(p => p.Id != point.Id), point with { HitCount = 0, IgnoreRemaining = point.IgnoreCount }];
            try
            {
                await RebindSourcePointsAsync(token);
            }
            catch { sourcePoints = previous; await RebindSourcePointsAsync(CancellationToken.None); throw; }
            if (PreferencesChanged is { } save)
            {
                await save();
            }
            Changed?.Invoke();
        }
        finally { gate.Release(); }
    }

    public async Task ChangeSourceBreakpointAsync(string id, bool? enabled, CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            RequireStopped();
            sourcePoints = enabled is null ? sourcePoints.Where(p => p.Id != id).ToArray() : sourcePoints.Select(p => p.Id == id ? p with { Enabled = enabled.Value } : p).ToArray();
            await RebindSourcePointsAsync(token);
            if (PreferencesChanged is { } save)
            {
                await save();
            }
            Changed?.Invoke();
        }
        finally { gate.Release(); }
    }

    public async Task ChangeWatchAsync(string expression, bool remove, CancellationToken token = default)
    {
        if (expression.Length is < 1 or > 256)
        {
            throw new StudioXException("MON51_EXPRESSION", "观察表达式长度须为 1–256 字符。");
        }
        await gate.WaitAsync(token);
        try
        {
            RequireStopped();
            if (!HasSymbols && !expression.StartsWith('$'))
            {
                throw new StudioXException("MON51_CAPABILITY", "变量观察需要已核对的 CDB；当前可以观察 $PC、$A 等 8051 寄存器。");
            }
            // 先验证表达式语法；变量优化或离开作用域则由观察窗口明确显示不可用。
            if (!System.Text.RegularExpressions.Regex.IsMatch(expression, @"^\$?\w+(?:(?:\.|->)\w+|\[\d+\])*$"))
            {
                DebugExpression.Parse(expression);
            }
            var next = watchExpressions.Where(w => w != expression).ToList();
            if (!remove)
            {
                next.Add(expression);
            }
            if (next.Count > 32)
            {
                throw new StudioXException("MON51_EXPRESSION", "最多观察 32 个表达式。");
            }
            watchExpressions = next.ToArray();
            await ReadSnapshotAsync(token);
            if (PreferencesChanged is { } save)
            {
                await save();
            }
            Changed?.Invoke();
        }
        finally { gate.Release(); }
    }

    private async Task<bool> HandleSourceStopAsync(CancellationToken token)
    {
        var hitPoints = sourcePoints.Where(p => p.Enabled && p.Verified && SourceAddress(p) == Pc).ToArray();
        var mustStop = manualPoints.Contains(Pc);
        var failure = false;
        foreach (var point in hitPoints)
        {
            var next = point with
            {
                HitCount = point.HitCount + 1
            };
            try
            {
                var eligible = point.Condition.Length == 0 || await EvaluateCoreAsync(point.Condition, token) != 0;
                if (eligible && point.IgnoreRemaining > 0)
                {
                    next = next with
                    {
                        IgnoreRemaining = point.IgnoreRemaining - 1
                    };
                    eligible = false;
                }
                if (eligible)
                {
                    if (point.LogMessage is { } message)
                    {
                        var text = new StringBuilder();
                        foreach (var part in DebugLogTemplate.Parse(message))
                        {
                            text.Append(part.Expression ? (await EvaluateCoreAsync(part.Text, token)).ToString(System.Globalization.CultureInfo.InvariantCulture) : part.Text);
                        }
                        Output?.Invoke($"[日志断点] {point.File}:{point.Line} · {text}");
                    }
                    else
                    {
                        mustStop = true;
                    }
                    if (point.Temporary)
                    {
                        sourcePoints = sourcePoints.Where(p => p.Id != point.Id).ToArray();
                    }
                }
            }
            catch (Exception ex) when (ex is StudioXException or InvalidOperationException or ArgumentException or OverflowException)
            {
                failure = mustStop = true;
                Output?.Invoke("断点表达式无法求值，保持暂停：" + ex.Message);
            }
            sourcePoints = sourcePoints.Select(p => p.Id == next.Id ? next : p).ToArray();
        }
        var temporaryHit = temporaryStop == Pc && (temporarySp is null || rawRegisters[15] == temporarySp);
        if (hitPoints.Length > 0)
        {
            await RebindSourcePointsAsync(token);
            if (PreferencesChanged is { } save)
            {
                await save();
            }
        }
        if (temporaryHit || mustStop || failure || hitPoints.Length == 0 && temporaryStop != Pc)
        {
            return false;
        }
        // 假条件、日志点或递归内层的同一返回地址：先执行原指令，再重新布置断点并继续等待通知。
        await ContinueCoreAsync(token, false);
        return true;
    }

    private async Task ContinueCoreAsync(CancellationToken token, bool startObserver = true)
    {
        if (sourcePoints.FirstOrDefault(point => point.Enabled && !point.Verified) is { } pendingPoint)
        {
            throw new StudioXException("MON51_BREAKPOINT_UNBOUND", $"源码断点 {pendingPoint.File}:{pendingPoint.Line} 尚未绑定，未启动程序。{pendingPoint.Message}。请核对并加载对应构建；如只需地址调试，请在源码断点表禁用或删除此请求。");
        }
        await CheckSourcesAsync(token);
        if (points.ContainsKey(Pc))
        {
            await StepCoreAsync(token);
        }
        await InstallAsync(token);
        await client!.RunAsync(token);
        SetState(DebugState.Running, temporaryStop is null ? "用户程序运行中 · 等待断点或手动暂停" : temporaryReason + " · 运行中，可暂停");
        if (startObserver)
        {
            runningLifetime?.Dispose();
            runningLifetime = new CancellationTokenSource();
            runningTask = ObserveStopAsync(runningLifetime.Token);
        }
    }

    private async Task RunTemporaryAsync(ushort address, string reason, int? stackPointer, CancellationToken token)
    {
        ClearTemporaryStop();
        await EnsurePointAsync(address, token);
        temporaryStop = address;
        temporaryReason = reason;
        temporarySp = stackPointer;
        await ContinueCoreAsync(token);
    }

    public async Task RunToCursorAsync(string file, int line, CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            RequireStopped();
            var location = symbols?.Bind(PathBoundary.Resolve(projectPath!, file), line) ?? throw new StudioXException("MON51_CAPABILITY", "光标行没有已核对的可执行源码映射。");
            await RunTemporaryAsync(location.Address, "已运行到光标", null, token);
        }
        finally { gate.Release(); }
    }
}
