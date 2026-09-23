using StudioX.Application.Terminal;
using StudioX.Engine;
using StudioX.Foundation;
using StudioX.Packages;

var root = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
var runtime = args.Length > 1 ? Path.GetFullPath(args[1]) : Path.Combine(root, "artifacts", "git-runtime");
var git = new GitRepositoryService(runtime);
var output = Path.Combine(root, ".artifacts", "git-terminal-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
Directory.CreateDirectory(output);
var checks = 0;
void Check(bool value, string label) { if (!value) throw new Exception(label); checks++; Console.WriteLine("PASS " + label); }
async Task<string> Run(string cwd, params string[] command)
{
    var result = await new ProcessRunner().RunAsync(new(git.Executable, command, cwd, TimeSpan.FromSeconds(30), git.Environment(), RemoveEnvironment: GitRepositoryService.AmbientVariables));
    if (!result.Success) throw new Exception(result.StandardOutput + result.StandardError);
    return result.StandardOutput.Trim();
}
var pack = (await new PackRepository(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MCUStudioX", "packs")).ListCatalogAsync())
    .First(p => p.Manifest.Id == "studiox.preview.ag32vf303");
var device = pack.Manifest.Devices.First();
var project = Path.Combine(output, "中文 project");
await git.InitializeAsync(output);
await new ProjectService(git.InitializeAsync).CreateAsync(pack, device.Id, device.Templates.First().Id, "git_test", project);
Check(Directory.Exists(Path.Combine(project, ".git")), "new project creates independent repository inside parent repository");
Check((await Run(project, "rev-parse", "--show-toplevel")).Replace('/', '\\').Equals(project.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase), "staging move leaves correct work tree path");
Check(await Run(project, "symbolic-ref", "--short", "HEAD") == "main", "new repository main branch");
Directory.CreateDirectory(Path.Combine(project, ".build")); await File.WriteAllTextAsync(Path.Combine(project, ".build", "firmware.elf"), "generated");
Check((await Run(project, "check-ignore", ".build/firmware.elf")).Contains("firmware.elf"), "build artifacts ignored");
Check((await Run(project, "status", "--porcelain")).Contains(".studiox/"), "project manifest remains versioned");
var missing = Path.Combine(output, "missing-git");
try { await new ProjectService(new GitRepositoryService(Path.Combine(output, "absent-runtime")).InitializeAsync).CreateAsync(pack, device.Id, device.Templates.First().Id, "missing", missing); throw new Exception("Missing Git did not fail"); }
catch (StudioXException ex) when (ex.Code == "GIT_MISSING") { Check(!Directory.Exists(missing) && !Directory.EnumerateDirectories(output, ".studiox-create-*").Any(), "Git failure leaves no partial project"); }
await Run(project, "config", "user.name", "StudioX Test"); await Run(project, "config", "user.email", "test@example.invalid");
await Run(project, "add", "."); await Run(project, "commit", "-m", "initial fixture");
var remote = Path.Combine(output, "remote.git"); await Run(output, "init", "--bare", "--initial-branch=main", remote);
await Run(project, "remote", "add", "origin", remote); await Run(project, "push", "-u", "origin", "main");
var peer = Path.Combine(output, "peer"); await Run(output, "clone", remote, peer);
await Run(peer, "config", "user.name", "Peer Test"); await Run(peer, "config", "user.email", "peer@example.invalid");
await File.WriteAllTextAsync(Path.Combine(peer, "remote-change.txt"), "from local test remote\n");
await Run(peer, "add", "."); await Run(peer, "commit", "-m", "remote change"); await Run(peer, "push");
await using var terminal = new ProjectTerminalService(git);
async Task Wait(string text)
{
    var deadline = DateTime.UtcNow.AddSeconds(15);
    while (!terminal.Snapshot().Screen.Display.Text.Contains(text, StringComparison.Ordinal))
    {
        if (DateTime.UtcNow > deadline) throw new Exception("Terminal timeout: " + text + "\n" + terminal.Snapshot());
        await Task.Delay(50);
    }
}
async Task WaitFile(string path)
{
    var deadline = DateTime.UtcNow.AddSeconds(15);
    while (!File.Exists(path)) { if (DateTime.UtcNow > deadline) throw new Exception("File timeout: " + path); await Task.Delay(50); }
}
await terminal.StartAsync(project);
await terminal.SendAsync("git --version\r"); await Wait("git version 2.55.0.windows.5"); Check(true, "ConPTY uses bundled Git");
await terminal.SendAsync("git pull --ff-only\r"); await Wait("remote-change.txt");
Check(File.Exists(Path.Combine(project, "remote-change.txt")), "pull from local bare remote through persistent terminal");
await terminal.SendAsync("echo terminal-content>terminal-change.txt\r");
await terminal.SendAsync("git add terminal-change.txt\r");
await terminal.SendAsync("git commit -m terminal_commit\r"); await Wait("terminal-change.txt");
await terminal.SendAsync("git push\r");
await Task.Delay(1500);
Check((await Run(project, "log", "-1", "--format=%s")) == "terminal_commit", "commit through terminal");
Check(await Run(remote, "rev-parse", "main") == await Run(project, "rev-parse", "HEAD"), "push through terminal");
await terminal.SendAsync("mkdir subdir\rcd subdir\recho persistent>working-dir.txt\r");
await WaitFile(Path.Combine(project, "subdir", "working-dir.txt")); Check(true, "working directory persists across commands");
await terminal.SendAsync("echo 中文终端测试\r"); await Wait("中文终端测试"); Check(true, "Unicode console output");
await terminal.SendAsync("ping -t 127.0.0.1\r"); await Task.Delay(500); await terminal.SendAsync("\x03");
await terminal.SendAsync("echo INTERRUPT_OK>interrupt.txt\r"); await WaitFile(Path.Combine(project, "subdir", "interrupt.txt")); Check(true, "Ctrl+C returns to shell");
terminal.Resize(80, 12); await terminal.SendAsync("echo RESIZE_OK\r"); await Wait("RESIZE_OK"); Check(true, "resize while shell alive");
await File.WriteAllTextAsync(Path.Combine(output, "terminal-operations.txt"), terminal.Snapshot().Screen.Display.Text);
await terminal.SendAsync("powershell.exe -NoProfile -NonInteractive -Command \"$PID | Set-Content child.pid; Start-Sleep -Seconds 90\"\r");
var childPidFile = Path.Combine(project, "subdir", "child.pid"); await WaitFile(childPidFile); await Task.Delay(100);
using var child = System.Diagnostics.Process.GetProcessById(int.Parse((await File.ReadAllTextAsync(childPidFile)).Trim()));
await terminal.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
Check(!terminal.Snapshot().Running && child.HasExited, "close terminates console and running child process");
await terminal.StartAsync(project); await terminal.SendAsync("echo RESTART_OK\r"); await Wait("RESTART_OK"); Check(true, "restart after stop");
await File.WriteAllTextAsync(Path.Combine(output, "terminal.txt"), terminal.Snapshot().Screen.Display.Text);
await terminal.StopAsync();
var screen = new ConsoleScreen(40, 6);
screen.Feed("first\r\nsecond\x1b[1;1HREPLACED\x1b[K");
Check(screen.Snapshot().Display.Text.Split('\n').Take(2).Select(line => line.TrimEnd()).SequenceEqual(["REPLACED", "second"]), "VT absolute cursor and line erase");
screen.Feed("\x1b[2J\x1b[H\x1b[31m红色\x1b[0m\r\n");
Check(screen.Snapshot().Display.Text.StartsWith("红色") && screen.Snapshot().Display.Spans.Any(s => s.Style.Foreground is not null), "VT CJK cells and color");
screen.Feed("\x1b[?25l");
Check(!screen.Snapshot().CursorVisible && screen.Snapshot().CursorOffset >= 0, "hidden cursor still provides position for output following");
for (var i = 0; i < 1200; i++) screen.Feed("line\r\n");
Check(screen.Snapshot().Display.TrimmedLines > 0, "bounded console history");
Console.WriteLine($"PASS: {checks} Git / terminal checks. Artifacts: {output}");
