namespace StudioX.DebugPluginValidation;

using StudioX.Application;
using StudioX.Application.Plugins;
using StudioX.Engine;
using StudioX.Engine.Debugging;
using StudioX.Foundation;

/// <summary>必须显式选择 --hardware：附加已授权的 F407/ST-Link，不下载、擦除或改选项字节。</summary>
internal static class HardwareChecks
{
    public static async Task RunAsync(string source, string toolRuntime, string host, string archive, string output, bool reset = false)
    {
        var root = Path.GetFullPath(output);
        if (Directory.Exists(root))
        {
            throw new InvalidOperationException("Use a new evidence directory.");
        }
        Directory.CreateDirectory(root);
        var project = Path.Combine(root, "project");
        CopySource(Path.GetFullPath(source), project);
        var info = await ProjectService.ReadAsync(project);
        if (info.DeviceId != "STM32F407ZGT6")
        {
            throw new InvalidOperationException("This acceptance is restricted to the confirmed STM32F407ZG board.");
        }
        var catalog = new ToolsetCatalog(Path.Combine(toolRuntime, "toolsets"));
        var downloads = new OpenOcdService(catalog);
        var configuration = await downloads.ConfigurationAsync(project) ?? throw new InvalidOperationException("Missing debug configuration.");
        if (configuration.Options.ProbeId != "stlink")
        {
            throw new InvalidOperationException("This acceptance requires the explicitly connected ST-Link.");
        }
        await JsonStore.WriteAsync(Path.Combine(root, "project-info.json"), new
        {
            original = source,
            isolated = project,
            info,
            configuration.Device,
            configuration.Options
        });
        Console.WriteLine("Building isolated source copy. No firmware download.");
        var build = await new BuildService(catalog).BuildAsync(project, output: new Progress<string>(Console.WriteLine));
        await JsonStore.WriteAsync(Path.Combine(root, "build.json"), build);
        if (!build.Success)
        {
            throw new InvalidOperationException(build.Summary);
        }
        var preparation = await HardwareDebugPreparer.PrepareAsync(project, downloads);
        if (reset)
        {
            // 仅显式验收模式采用已授权的硬复位连接，不变成 IDE 自动重试或下载行为。
            preparation = preparation with
            {
                ConnectUnderReset = true,
                Configuration = preparation.Configuration with
                {
                    Options = preparation.Configuration.Options with
                    {
                        SpeedKhz = 400
                    }
                }
            };
        }
        var runtime = Path.Combine(root, "plugin-runtime");
        Directory.CreateDirectory(Path.Combine(runtime, "plugin-host"));
        foreach (var file in Directory.EnumerateFiles(host))
        {
            File.Copy(file, Path.Combine(runtime, "plugin-host", Path.GetFileName(file)));
        }
        await using var manager = new PluginManagerService(runtime, Path.Combine(root, "data"));
        var entry = await manager.ImportAsync(archive);
        await manager.SetEnabledAsync(entry.Id, true);
        await using var workspace = await manager.OpenWorkspaceAsync(project, (_, _, _, _) => throw new InvalidOperationException("Acceptance plugin declares no host tools."));
        await using var debug = new DebugSessionService(Path.Combine(root, "data"));
        debug.Output += Console.WriteLine;
        await debug.OpenProjectAsync(project);
        await debug.ChangeWatchAsync("$pc", false);
        var adapter = workspace.Contributions.Single(p => p.Id == entry.Id).Contribution.DebugAdapters.Single();
        await using var view = new PluginDebugViewSession(workspace, debug, entry.Id, adapter.Id);
        var checks = new List<string>();
        void Check(bool passed, string label)
        {
            if (!passed)
            {
                throw new InvalidOperationException(label);
            }
            checks.Add(label);
            Console.WriteLine("PASS " + label);
        }
        async Task WaitAsync(Func<bool> condition)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (!condition())
            {
                await Task.Delay(25, deadline.Token);
            }
        }
        try
        {
            Console.WriteLine("Attaching and halting confirmed F407/ST-Link; board image must match ELF before source validation.");
            await debug.StartHardwareAsync(preparation);
            await WaitAsync(() => view.Current.Panel is not null);
            Check(debug.State == DebugState.Stopped && debug.IsHardware && view.Current.Hardware, "real F407/ST-Link attaches with verified board image and hardware-labelled plugin data");
            await JsonStore.WriteAsync(Path.Combine(root, "stopped.json"), new
            {
                debug.Reason,
                debug.HardwareTargetName,
                debug.Snapshot,
                view = view.Current
            });
            var revision = view.Current.Revision;
            await debug.ExecuteAsync(DebugAction.StepOver);
            Check(view.Current.Panel is null, "real resume immediately invalidates extension data");
            await WaitAsync(() => debug.State == DebugState.Stopped && view.Current.Panel is not null && view.Current.Revision > revision);
            await JsonStore.WriteAsync(Path.Combine(root, "stepped.json"), new
            {
                debug.Reason,
                debug.Snapshot,
                view = view.Current
            });
            Check(view.Current.Panel!.Widgets.Any(w => w.Id == "registers") && view.Current.Panel.Widgets.Any(w => w.Id == "frames"), "real single-step produces refreshed register and stack tables");
            await debug.ExecuteAsync(DebugAction.Continue);
            Check(view.Current.State == DebugState.Running && view.Current.Panel is null, "real continue clears plugin snapshot");
            await debug.ExecuteAsync(DebugAction.Pause);
            await WaitAsync(() => debug.State == DebugState.Stopped && view.Current.Panel is not null);
            Check(view.Current.Hardware, "real pause restores valid hardware snapshot");
        }
        catch (Exception error)
        {
            await File.WriteAllTextAsync(Path.Combine(root, "error.txt"), error.ToString());
            await JsonStore.WriteAsync(Path.Combine(root, "result.json"), new
            {
                success = false,
                hardware = true,
                downloaded = false,
                checks,
                diagnostic = error.ToString(),
                debug.SessionLogPath
            });
            throw;
        }
        finally { await debug.StopAsync(); }
        Check(view.Current.Panel is null && debug.State == DebugState.Disconnected, "real debug ends, clears plugin and resumes target without firmware download");
        await JsonStore.WriteAsync(Path.Combine(root, "result.json"), new
        {
            success = true,
            hardware = true,
            downloaded = false,
            checks,
            debug.SessionLogPath
        });
    }

    private static void CopySource(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var entry in new DirectoryInfo(source).EnumerateFileSystemInfos())
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Linked source requires manual review: " + entry.FullName);
            }
            if (entry is DirectoryInfo)
            {
                if (entry.Name is ".build" or "build" or "Build" or "Debug" or "Release" or ".git")
                {
                    continue;
                }
                CopySource(entry.FullName, Path.Combine(target, entry.Name));
            }
            else
            {
                File.Copy(entry.FullName, Path.Combine(target, entry.Name));
            }
        }
    }
}
