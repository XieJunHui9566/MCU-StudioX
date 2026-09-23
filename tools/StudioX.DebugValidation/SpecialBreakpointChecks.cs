using StudioX.Application;
using StudioX.Engine.Debugging;
using StudioX.Foundation;

internal static class SpecialBreakpointChecks
{
    public static async Task RunAsync(DebugSessionService session, string project, Action<string> pass)
    {
        static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        static async Task Wait(Func<bool> condition)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!condition()) await Task.Delay(15, timeout.Token);
        }
        static async Task Reject(Func<Task> action)
        {
            try { await action(); } catch (Exception ex) when (ex is ArgumentException or StudioXException) { return; }
            throw new InvalidOperationException("Expected invalid breakpoint options to be rejected.");
        }
        var file = F407DebugExample.RelativeFile;
        int Counter() => int.Parse(session.Snapshot.Watches.Single(v => v.Name == "app_counter").Value, System.Globalization.CultureInfo.InvariantCulture);
        Task Set(int line, BreakpointOptions options) => session.ConfigureBreakpointAsync(file, line, options);
        async Task Fresh()
        {
            await session.StopAsync();
            foreach (var point in session.Breakpoints.ToArray()) await session.ChangeBreakpointAsync(point.Id, null);
            await session.StartOfflineAsync();
        }
        async Task RunStop()
        { await session.ExecuteAsync(DebugAction.Continue); await Wait(() => session.State == DebugState.Stopped); }
        long Lookup(string name) => name == "app_counter" ? 3 : throw new InvalidOperationException("unexpected symbol evaluation");
        Check(DebugExpression.Parse("app_counter >= 3 && (app_counter & 1) == 1").Evaluate(Lookup) == 1, "Conditional expression");
        Check(DebugExpression.Parse("1 + 2 * 3 == 7 && (8 >> 1) == 4").Evaluate(Lookup) == 1, "Expression precedence");
        Check(DebugExpression.Parse("0 && 1/0").Evaluate(Lookup) == 0 && DebugExpression.Parse("1 || missing").Evaluate(Lookup) == 1, "Short circuit");
        Check(DebugExpression.Parse("0xff ^ 0x0f").Evaluate(Lookup) == 240 && DebugExpression.Parse("!0 + -2").Evaluate(Lookup) == -1, "Bitwise/unary");
        foreach (var expression in new[] { "app_counter=3", "++app_counter", "function()", "x;quit", "app_counter--", "*ptr", "app_counter &&", "1(2)" })
            await Reject(() => { DebugExpression.Parse(expression); return Task.CompletedTask; });
        var template = DebugLogTemplate.Parse("{{计数}}={app_counter}, next={app_counter+1}");
        Check(template.Count(x => x.Expression) == 2 && template[0].Text == "{计数}=", "Template escaped braces");
        foreach (var invalid in new[] { "{", "value={}", "x={call()}", "x={a=4}", "bad}\n" }) await Reject(() => { DebugLogTemplate.Parse(invalid); return Task.CompletedTask; });
        pass("Read-only expressions: precedence, bitwise/logical operators, short circuit, side-effect rejection, log brace validation");

        await Fresh(); await Set(F407DebugExample.Call, new("app_counter >= 3 && (app_input & 1) == 1")); await RunStop();
        Check(Counter() == 3 && session.Breakpoints.Single().HitCount == 3, "False conditions must continue until counter=3");
        pass("Conditional breakpoint stops at the first matching loop iteration, with arrival count");

        await Fresh(); await Set(F407DebugExample.Call, new(IgnoreCount: 2)); await RunStop();
        Check(Counter() == 3 && session.Breakpoints.Single().IgnoreRemaining == 0, "Ignore first two visits");
        await RunStop(); Check(Counter() == 4, "Ignore count must not reset on continue");
        await Fresh(); await Set(F407DebugExample.Call, new("app_counter >= 4", IgnoreCount: 2)); await RunStop();
        Check(Counter() == 4 && session.Breakpoints.Single().HitCount == 4, "Ignore-before-condition semantics");
        pass("Hit-count skipping is consumed once; combined count and condition apply in order");

        await Fresh(); await Set(F407DebugExample.Call, new("app_counter >= 3", 1, Temporary: true)); await RunStop();
        Check(Counter() == 3 && session.Breakpoints.Count == 0, "Temporary breakpoint must not delete on ignored/false visits");
        await session.StopAsync(); await session.OpenProjectAsync(project);
        Check(session.Breakpoints.Count == 0, "Temporary breakpoint deletion must persist");
        pass("Temporary conditional/count breakpoint deletes only on trigger, including persisted settings");

        var logs = new List<string>(); var logSync = new object();
        void OnLog(string message) { lock (logSync) logs.Add(message); }
        int LogCount() { lock (logSync) return logs.Count; }
        void ClearLogs() { lock (logSync) logs.Clear(); }
        session.BreakpointLog += OnLog;
        try
        {
            await Fresh(); ClearLogs();
            await Set(F407DebugExample.Call, new("app_counter >= 2", 1, true, "{{sample}} count={app_counter}, doubled={app_input*2}, pc={$pc}"));
            await Set(F407DebugExample.Delay, new("app_counter >= 3")); await RunStop();
            Check(Counter() == 3 && LogCount() == 1 && session.Breakpoints.Count == 1, "Temporary log must auto-continue and delete");
            lock (logSync) Check(logs[0].Contains("{sample} count=2, doubled=4, pc=0x", StringComparison.Ordinal), "Interpolated log values");
            pass("Conditional temporary logpoint emits interpolated values once and continues to a later stopping breakpoint");

            await Fresh(); ClearLogs(); await Set(F407DebugExample.Call, new(LogMessage: "step={app_counter}"));
            for (var i = 0; i < 3; i++) { await session.ExecuteAsync(DebugAction.StepOver); await Wait(() => session.State == DebugState.Stopped); }
            Check(session.Snapshot.Frames[0].Line == F407DebugExample.Call && LogCount() == 1 && Counter() == 1, "Step onto logpoint must stay stopped");
            await Task.Delay(150); Check(session.State == DebugState.Stopped, "Logpoint resumed a user step");
            pass("Stepping onto a logpoint logs once and respects the user-requested step pause");

            await Fresh(); await Set(F407DebugExample.Call, new("app_counter >= 2"));
            var old = session.Breakpoints.Single();
            await Reject(() => Set(F407DebugExample.Call, new("missing_symbol > 0")));
            Check(session.Breakpoints.Single() is { Verified: true } restored && restored.Id == old.Id && restored.Condition == old.Condition, "Failed edit must restore the old remote breakpoint");
            await Reject(() => Set(F407DebugExample.Call, new("app_counter=9")));
            await RunStop(); Check(Counter() == 2, "Restored condition must still trigger");
            pass("Invalid expressions are rejected and a failed live rebind restores the prior breakpoint");

            await Fresh(); await Set(F407DebugExample.Call, new("10 / (app_counter - app_counter) > 0")); await RunStop();
            Check(session.Reason.Contains("条件求值失败", StringComparison.Ordinal) && Counter() == 1, "Condition evaluation error must stop with diagnostic");
            await Fresh(); ClearLogs(); await Set(F407DebugExample.Call, new(LogMessage: "value={missing_symbol}")); await RunStop();
            Check(session.Reason.Contains("日志求值失败", StringComparison.Ordinal) && LogCount() == 1 && Counter() == 1, "Log evaluation error must not silently continue");
            pass("Runtime condition/log errors remain paused and preserve diagnostics");

            await Fresh(); ClearLogs(); await Set(F407DebugExample.Call, new("1", Temporary: true, LogMessage: "disabled"));
            await session.ChangeBreakpointAsync(session.Breakpoints.Single().Id, false);
            await Set(F407DebugExample.Delay, new("app_counter >= 2")); await RunStop();
            Check(LogCount() == 0 && session.Breakpoints.First(b => !b.Enabled).HitCount == 0 && session.Breakpoints.Count == 2, "Disabled special breakpoint fired");
            pass("Disabled temporary/log/conditional breakpoint neither counts nor emits/deletes");

            await Fresh(); await Set(F407DebugExample.Call, new("app_counter > 99", LogMessage: "existing"));
            var saved = session.Breakpoints.Single();
            await Reject(() => session.RunToCursorAsync(file, 2));
            Check(!session.Breakpoints.Any(b => b.SessionOnly) && session.State == DebugState.Stopped, "Pending cursor target must not run");
            await session.RunToCursorAsync(file, F407DebugExample.Call); await Wait(() => session.State == DebugState.Stopped);
            Check(session.Reason.Contains("运行到光标", StringComparison.Ordinal) && session.Breakpoints.Count == 1 && session.Breakpoints[0].Id == saved.Id && session.Breakpoints[0].Condition == saved.Condition, "Run-to-cursor must preserve same-line user breakpoint");
            await Set(F407DebugExample.Function, new());
            await session.RunToCursorAsync(file, F407DebugExample.Delay); await Wait(() => session.State == DebugState.Stopped);
            Check(session.Snapshot.Frames[0].Line == F407DebugExample.Function && session.Breakpoints.All(b => !b.SessionOnly), "Earlier breakpoint should interrupt/cancel run-to-cursor");
            pass("Run-to-cursor validates executable lines, preserves existing rules and cleans up on another stop");

            await Fresh(); ClearLogs(); await Set(F407DebugExample.Call, new(LogMessage: "running={app_counter}"));
            await session.ExecuteAsync(DebugAction.Continue); await Wait(() => LogCount() > 0);
            await session.ExecuteAsync(DebugAction.Pause); await Wait(() => session.State == DebugState.Stopped);
            await Task.Delay(180); Check(session.State == DebugState.Stopped, "Logpoint auto-continue overrode pause");
            await session.StopAsync(); await session.OpenProjectAsync(project);
            Check(session.Breakpoints.Single().LogMessage == "running={app_counter}" && session.Breakpoints.Single().HitCount == 0, "Rule persistence/runtime count separation");
            pass("Pause wins over log auto-continue; rules persist and runtime counters reset across sessions");
        }
        finally { session.BreakpointLog -= OnLog; await session.StopAsync(); }
    }
}
