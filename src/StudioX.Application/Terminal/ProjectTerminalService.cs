namespace StudioX.Application.Terminal;

using StudioX.Engine;
using StudioX.Foundation;

public sealed record ProjectTerminalSnapshot(bool Running, string? Directory, string Status, ConsoleSnapshot Screen);

public sealed class ProjectTerminalService(GitRepositoryService git) : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object sync = new();
    private ConsoleScreen screen = new();
    private PseudoConsoleProcess? shell;
    private string? directory;
    private bool running;
    private string status = "打开工程后启动终端。";
    private Task monitor = Task.CompletedTask;
    public ProjectTerminalSnapshot Snapshot() { lock (sync) return new(running, directory, status, screen.Snapshot()); }
    public async Task StartAsync(string projectDirectory)
    {
        await gate.WaitAsync();
        try
        {
            await StopCoreAsync();
            if (!System.IO.Directory.Exists(projectDirectory)) throw new DirectoryNotFoundException(projectDirectory);
            var source = new PseudoConsoleProcess();
            lock (sync)
            {
                screen = new(); directory = Path.GetFullPath(projectDirectory); status = "正在启动工程终端…";
                screen.Reply += reply => _ = ReplyAsync(source, reply);
            }
            source.Output += text => { lock (sync) screen.Feed(text); };
            source.ReadError += ex => { lock (sync) status = ex.ToString(); };
            var environment = System.Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
                .Where(p => !((string)p.Key).StartsWith("GIT_", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(p => (string)p.Key, p => (string)p.Value!, StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in git.Environment()) environment[k] = v;
            environment["PROMPT"] = "$P$G";
            try { await Task.Run(() => source.Start(Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.System), "cmd.exe"), "/d /q /k \"chcp 65001 >nul\"", directory, environment, 120, 20)); }
            catch { await source.DisposeAsync(); throw; }
            shell = source;
            lock (sync) { running = true; status = "命令提示符 · 内置 Git · UTF-8"; }
            monitor = MonitorAsync(source);
        }
        finally { gate.Release(); }
    }
    private async Task MonitorAsync(PseudoConsoleProcess source)
    {
        await source.Completion;
        lock (sync) { running = false; status = $"终端已结束 · 退出代码 {source.ExitCode}"; }
    }
    private async Task ReplyAsync(PseudoConsoleProcess source, string reply)
    { try { await source.WriteAsync(reply); } catch (Exception ex) { lock (sync) status = ex.Message; } }
    public Task SendAsync(string input) => shell?.WriteAsync(input) ?? throw new InvalidOperationException("请先启动工程终端。");
    public void ClearHistory() { lock (sync) screen.ClearHistory(); }
    public void Resize(int columns, int rows)
    {
        columns = Math.Clamp(columns, 40, 240); rows = Math.Clamp(rows, 6, 80);
        lock (sync) { if (screen.Columns == columns && screen.Rows == rows) return; screen.Resize(columns, rows); }
        shell?.Resize(columns, rows);
    }
    public async Task StopAsync() { await gate.WaitAsync(); try { await StopCoreAsync(); } finally { gate.Release(); } }
    private async Task StopCoreAsync()
    {
        if (shell is { } source)
        {
            // Stop 杀掉 Job 后等待 monitor 完成，再允许新会话接管状态。
            var stop = source.DisposeAsync().AsTask();
            await monitor; await stop; shell = null;
        }
        lock (sync) running = false;
    }
    public async ValueTask DisposeAsync() => await StopAsync();
}
