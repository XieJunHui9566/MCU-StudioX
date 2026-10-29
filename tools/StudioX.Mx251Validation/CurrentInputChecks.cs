using System.Security.Cryptography;
using StudioX.Ai8051Isp;
using StudioX.Engine;
using StudioX.Foundation;
using StudioX.Packages;

internal static class CurrentInputChecks
{
    private sealed class NoHardware : IMx251IspBackend
    {
        public int Accesses;
        public IReadOnlyList<HidDevice> Enumerate()
        {
            Accesses++;
            throw new Exception("Hardware enumeration forbidden in input checks");
        }
        public Mx251IspConnection Open(HidDevice device, Action<string, byte[]> trace)
        {
            Accesses++;
            throw new Exception("Hardware open forbidden in input checks");
        }
    }
    private sealed class Output(Action<string> action) : IProgress<string>
    {
        public void Report(string value) => action(value);
    }
    internal static async Task RunAsync(string packArchive, string toolsRoot, string output, bool observe)
    {
        if (Directory.Exists(output))
        {
            throw new IOException("Choose a new validation directory");
        }
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        void Check(bool ok, string label)
        {
            if (!ok)
            {
                throw new InvalidOperationException(label);
            }
            checks.Add(label);
            Console.WriteLine("PASS " + label);
        }
        var pack = await new PackRepository(Path.Combine(output, "packs")).ImportAsync(packArchive);
        var root = Path.Combine(output, "工程 inputs");
        await new ProjectService().CreateAsync(pack, "AI8051U-34K64", "registers", "inputs", root);
        var catalog = new ToolsetCatalog(toolsRoot);
        var builder = new BuildService(catalog);
        var backend = new NoHardware();
        var isp = new Mx251IspService(catalog, Path.Combine(output, "user-data"), backend);
        await isp.SaveSettingsAsync(root, new("offline-test-device"));
        var main = Path.Combine(root, "src/main.c");
        var cmakePath = Path.Combine(root, "CMakeLists.txt");
        var textInput = Path.Combine(root, "settings.txt");
        await File.WriteAllTextAsync(main, "volatile unsigned int value; void main(void){value=FEATURE;for(;;){}}\n");
        var cmake = (await File.ReadAllTextAsync(cmakePath)).Replace("mx251_firmware(app)", """
            file(READ "${CMAKE_CURRENT_SOURCE_DIR}/settings.txt" setting)
            string(STRIP "${setting}" setting)
            target_compile_definitions(app INTERFACE FEATURE=${setting})
            message(STATUS "MX251_INPUTS_READ")
            mx251_firmware(app)
            """);
        await File.WriteAllTextAsync(cmakePath, cmake);
        await File.WriteAllTextAsync(textInput, "7\n");
        string Hash(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
        async Task Build(string label)
        {
            var report = await builder.BuildAsync(root);
            await File.WriteAllTextAsync(Path.Combine(output, label + ".log"), report.Log);
            Check(report.Success, label);
        }
        async Task<bool> CanPrepare()
        {
            try
            {
                await isp.PrepareAsync(root);
                return true;
            }
            catch (StudioXException e) when (e.Code == "MX251_ISP") { return false; }
        }
        void NoFirmware(string label)
        {
            foreach (var path in new[] { ".build/firmware.hex", ".build/studiox-build-receipt.json", ".build/mx251/latest/firmware.hex", ".build/mx251/latest/receipt.json" })
            {
                Check(!File.Exists(Path.Combine(root, path)), label + " no " + path);
            }
        }
        await Build("baseline");
        var baseline = Hash(Path.Combine(root, ".build/firmware.hex"));
        Check(await CanPrepare(), "unchanged build can prepare without hardware");
        await File.WriteAllTextAsync(textInput, "9\n");
        var staleAccepted = await CanPrepare();
        await Build("changed-config");
        var changed = Hash(Path.Combine(root, ".build/firmware.hex"));
        Check(baseline != changed, "txt input changes actual machine code");
        if (observe)
        {
            Check(staleAccepted, "reproduced: changed txt accepted before fix");
            await JsonStore.WriteAsync(Path.Combine(output, "results.json"), new
            {
                checks,
                staleAccepted,
                baseline,
                changed,
                backend.Accesses,
                hardwareOpened = false
            });
            return;
        }
        Check(!staleAccepted, "changed txt rejects stale preparation");
        // 排除规则只适用于工程根缓存；嵌套 Build/Debug 目录可包含真实工程输入。
        var nested = Path.Combine(root, "config/Build/options.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(nested)!);
        await File.WriteAllTextAsync(nested, "11\n");
        var nestedCMake = cmake.Replace("/settings.txt", "/config/Build/options.txt");
        await File.WriteAllTextAsync(cmakePath, nestedCMake);
        await Build("nested-input-baseline");
        await File.WriteAllTextAsync(nested, "13\n");
        Check(!await CanPrepare(), "nested Build input rejects stale preparation");
        await Build("nested-input-rebuild");
        // 未使用输出缓存、日志变化不得产生自失效或无限重编。
        await File.WriteAllTextAsync(Path.Combine(root, ".build/unrelated.txt"), "output only");
        Check(await CanPrepare(), "root build output excluded");
        var changedDuringBuild = 0;
        try
        {
            await builder.BuildAsync(root, output: new Output(line =>
            {
                if (line.Contains("MX251_INPUTS_READ") && Interlocked.Exchange(ref changedDuringBuild, 1) == 0)
                {
                    File.WriteAllText(nested, "15\n");
                }
            }));
            throw new InvalidOperationException("Changed configure input incorrectly published");
        }
        catch (StudioXException e) when (e.Code == "MX251_SOURCE_CHANGED") { Check(changedDuringBuild == 1, "during-build input change rejects publication"); }
        NoFirmware("input change");
        await Build("after-input-change");
        await File.WriteAllTextAsync(cmakePath, nestedCMake + "\nmessage(FATAL_ERROR \"expected configure failure\")\n");
        var failed = await builder.BuildAsync(root);
        await File.WriteAllTextAsync(Path.Combine(output, "configure-failure.log"), failed.Log);
        Check(!failed.Success && failed.Log.Contains("expected configure failure"), "configure failure keeps diagnostic");
        NoFirmware("configure failure");
        Check(!await CanPrepare(), "configure failure rejects preparation");
        await File.WriteAllTextAsync(cmakePath, nestedCMake);
        await Build("configure-recovery");
        // 配置前被拒绝也必须撤销两处固件，不能只隐藏 IDE 公共副本。
        var configPath = Path.Combine(root, "mx251.json");
        var config = await File.ReadAllTextAsync(configPath);
        await File.WriteAllTextAsync(configPath, "{}");
        try
        {
            await builder.BuildAsync(root);
            throw new InvalidOperationException("Invalid configuration accepted");
        }
        catch (StudioXException e) { Check(e.Code == "MX251_CLOCK", "early invalid configuration rejected"); }
        NoFirmware("early rejection");
        await File.WriteAllTextAsync(configPath, config);
        await Build("early-recovery");
        using (var cancel = new CancellationTokenSource())
        {
            await File.WriteAllTextAsync(cmakePath, nestedCMake + "\nmessage(STATUS \"MX251_CANCEL_POINT\")\nexecute_process(COMMAND \"${CMAKE_COMMAND}\" -E sleep 10)\n");
            try
            {
                await builder.BuildAsync(root, cancellationToken: cancel.Token, output: new Output(line => { if (line.Contains("MX251_CANCEL_POINT")) { cancel.Cancel(); } }));
                throw new InvalidOperationException("Cancellation did not stop build");
            }
            catch (OperationCanceledException) { Check(cancel.IsCancellationRequested, "configuration cancellation propagated"); }
        }
        NoFirmware("cancelled");
        await File.WriteAllTextAsync(cmakePath, nestedCMake);
        await Build("final-recovery");
        Check(await CanPrepare(), "final build valid");
        Check(backend.Accesses == 0, "no hardware enumeration or access");
        await JsonStore.WriteAsync(Path.Combine(output, "results.json"), new
        {
            checks,
            staleAccepted,
            baseline,
            changed,
            backend.Accesses,
            hardwareOpened = false
        });
    }
}
