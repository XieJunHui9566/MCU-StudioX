namespace StudioX.Application;

using System.Globalization;
using System.Text;
using StudioX.Engine.Debugging;

public sealed partial class DebugSessionService
{
    private string? stopDescription;
    private async Task ReadRecordsAsync()
    {
        // 保持 MI 通知次序，尤其临时断点的 stopped → deleted，防止 UI 丢失命中原因。
        await foreach (var (source, record) in records.Reader.ReadAllAsync()) await ProcessRecordAsync(source, record);
    }
    private static SourceBreakpoint Unbound(SourceBreakpoint point) => point with
    { Verified = false, Message = null, HitCount = 0, IgnoreRemaining = point.IgnoreCount, BoundLocation = null };
    private async Task<SourceBreakpoint> BindStrictAsync(SourceBreakpoint point, CancellationToken token)
    {
        var binding = await adapter!.InsertAsync(point, token); bound[point.Id] = binding.Number;
        var location = binding.Verified ? NormalizeBoundLocation(point, binding.Location) : null;
        return point with { HitCount = 0, IgnoreRemaining = point.IgnoreCount, Verified = binding.Verified,
            BoundLocation = location, Message = binding.Verified ? BindingMessage(point, location) : "未绑定：此处没有可解析的可执行语句" };
    }
    private SourceLocation? NormalizeBoundLocation(SourceBreakpoint point, SourceLocation? location)
    {
        if (location is null) return null;
        var file = location.File.Replace('\\', '/');
        if (Path.IsPathFullyQualified(file))
        {
            var relative = Path.GetRelativePath(ProjectDirectory!, file).Replace('\\', '/');
            file = relative == ".." || relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? file : relative;
        }
        else if (string.Equals(Path.GetFileName(file), file, StringComparison.OrdinalIgnoreCase) &&
                 string.Equals(file, Path.GetFileName(point.File), StringComparison.OrdinalIgnoreCase)) file = point.File;
        return new(file, location.Line);
    }
    private string BindingMessage(SourceBreakpoint point, SourceLocation? location) =>
        "硬件断点" + (location is not null && (!location.File.Equals(point.File, StringComparison.OrdinalIgnoreCase) || location.Line != point.Line)
            ? $" · 实际位置 {location.File}:{location.Line}" : "") + ModeSuffix;
    public async Task ConfigureBreakpointAsync(string file, int line, BreakpointOptions options, CancellationToken token = default)
    {
        options = options with { Condition = options.Condition.Trim() }; options.Validate();
        await gate.WaitAsync(token);
        try
        {
            ValidateLocation(file, line); RequireEditableBreakpoints();
            var old = breakpoints.FirstOrDefault(b => !b.SessionOnly && b.File.Equals(file, StringComparison.OrdinalIgnoreCase) && b.Line == line);
            if (old is null && breakpoints.Length >= 128) throw Error("最多保存 128 个源代码断点。");
            var next = Unbound((old ?? new(Guid.NewGuid().ToString("N"), file, line)) with
            { Condition = options.Condition, IgnoreCount = options.IgnoreCount, Temporary = options.Temporary, LogMessage = options.LogMessage });
            if (adapter is not null)
            {
                // 暂停时重新绑定，避免修改到一半继续执行；失败时尽量恢复旧配置，错误不静默降级为普通断点。
                if (old is not null && bound.TryGetValue(old.Id, out var previous)) { await adapter.DeleteAsync(previous, token); bound.Remove(old.Id); }
                try { next = await BindStrictAsync(next, token); }
                catch
                {
                    if (old is not null)
                    {
                        var restored = await BindAsync(Unbound(old), CancellationToken.None);
                        breakpoints = breakpoints.Select(b => b.Id == old.Id ? restored : b).ToArray(); Changed?.Invoke();
                    }
                    throw;
                }
            }
            breakpoints = [..breakpoints.Where(b => b.Id != next.Id), next];
            await SaveAsync(token); Changed?.Invoke();
        }
        finally { gate.Release(); }
    }
    public async Task RunToCursorAsync(string file, int line, CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            RequireStopped(); ValidateLocation(file, line);
            await RemoveCursorBreakpointsAsync();
            // 与用户断点分开，即使同一行已有条件/日志断点，也不改变其设置。
            var point = new SourceBreakpoint(Guid.NewGuid().ToString("N"), file, line, SessionOnly: true);
            try
            {
                point = await BindStrictAsync(point, token);
                if (!point.Verified) throw Error("光标行没有可执行语句，无法运行到这里。");
                breakpoints = [..breakpoints, point]; pauseRequested = false; lastResumeAction = DebugAction.Continue;
                SetState(DebugState.Running, "运行到光标" + ModeSuffix);
                await adapter!.ActionAsync(DebugAction.Continue, token);
            }
            catch
            {
                if (bound.TryGetValue(point.Id, out var id)) { await adapter!.DeleteAsync(id, CancellationToken.None); bound.Remove(point.Id); }
                breakpoints = breakpoints.Where(b => b.Id != point.Id).ToArray(); SetState(DebugState.Stopped, "运行到光标未启动"); throw;
            }
        }
        finally { gate.Release(); }
    }
    private async Task RemoveCursorBreakpointsAsync()
    {
        foreach (var point in breakpoints.Where(b => b.SessionOnly).ToArray()) await RemoveCoreAsync(point.Id, CancellationToken.None);
    }
    private void UpdateBreakpointInfo(MiValue info)
    {
        var key = bound.FirstOrDefault(pair => pair.Value == info.String("number")).Key;
        if (key is null) return;
        var hits = int.TryParse(info.String("times"), CultureInfo.InvariantCulture, out var count) ? count : 0;
        var remaining = int.TryParse(info.String("ignore"), CultureInfo.InvariantCulture, out var ignore) ? ignore : 0;
        breakpoints = breakpoints.Select(b =>
        {
            if (b.Id != key) return b;
            var location = NormalizeBoundLocation(b, GdbDebugAdapter.BreakpointLocation(info, b.File)) ?? b.BoundLocation;
            return b with { HitCount = hits, IgnoreRemaining = remaining, BoundLocation = location,
                Message = b.Verified ? BindingMessage(b, location) : b.Message };
        }).ToArray();
    }
    private async Task ForgetRemoteBreakpointAsync(string remote)
    {
        var key = bound.FirstOrDefault(pair => pair.Value == remote).Key;
        if (key is null) return;
        bound.Remove(key); breakpoints = breakpoints.Where(b => b.Id != key).ToArray();
        await SaveAsync(CancellationToken.None); Changed?.Invoke();
    }
    /// <returns>日志断点已处理并继续执行时返回 true；其他停止交给标准快照流程。</returns>
    private async Task<bool> HandleBreakpointStopAsync(GdbDebugAdapter source, MiRecord record)
    {
        stopDescription = null;
        foreach (var info in await source.BreakpointInfoAsync()) UpdateBreakpointInfo(info);
        var remote = record.Data.String("bkptno"); var pointId = bound.FirstOrDefault(p => p.Value == remote).Key;
        var point = breakpoints.FirstOrDefault(b => b.Id == pointId);
        var frame = record.Data.Get("frame");
        var file = NormalizeSource(frame?.String("fullname", frame.String("file")) ?? "");
        var line = frame?.String("line");
        var cursorHit = breakpoints.Any(b => b.SessionOnly && b.File == file && b.Line.ToString(CultureInfo.InvariantCulture) == line);
        if (point is { Temporary: true }) await ForgetRemoteBreakpointAsync(remote);
        if (record.Data.String("reason") == "breakpoint-hit" && point?.LogMessage is { } template)
        {
            try
            {
                await source.SendAsync("-stack-select-frame 0");
                var text = new StringBuilder();
                foreach (var part in DebugLogTemplate.Parse(template)) text.Append(part.Expression ? await source.EvaluateAsync(part.Text) : part.Text);
                var message = $"[{DateTime.Now:HH:mm:ss}] {point.File}:{point.Line} · {text}";
                BreakpointLog?.Invoke(message); Trace("[日志断点] " + message);
                if (lastResumeAction == DebugAction.Continue && !pauseRequested && !cursorHit)
                {
                    await source.ActionAsync(DebugAction.Continue, CancellationToken.None); return true;
                }
                stopDescription = pauseRequested ? "用户暂停" : "日志已输出 · 单步暂停";
            }
            catch (Exception ex)
            {
                Trace(ex.ToString()); BreakpointLog?.Invoke("日志求值失败：" + ex.Message);
                stopDescription = "日志求值失败，保持暂停：" + ex.Message;
            }
        }
        else if (point is not null) stopDescription = point.Kind == "普通" ? "命中断点" : "命中" + point.Kind + "断点";
        if (cursorHit) stopDescription = "已运行到光标";
        await RemoveCursorBreakpointsAsync();
        return false;
    }
}
