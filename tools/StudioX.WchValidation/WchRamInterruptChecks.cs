using System.Security.Cryptography;
using System.Text;
using StudioX.Engine;
using StudioX.Foundation;

internal static class WchRamInterruptChecks
{
    private sealed record Image(string Name, uint Entry, string Sha256);
    public static async Task PrepareAsync(string runtime, string output)
    {
        var root = Path.GetFullPath(output);
        if (Directory.Exists(root)) throw new ArgumentException("Use a new output directory.");
        var tools = await Resolve(runtime);
        Directory.CreateDirectory(root);
        var fixture = Path.Combine(AppContext.BaseDirectory, "InterruptRam");
        foreach (var name in new[] { "main.c", "start.S", "ram.ld" }) File.Copy(Path.Combine(fixture, name), Path.Combine(root, name));
        var assembly = new StringBuilder(".section .text.register_probe,\"ax\",@progbits\n.global register_probe\nregister_probe:\n    addi sp, sp, -64\n");
        int[] saved = [1, 3, 4, 8, 9, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27];
        for (var i = 0; i < saved.Length; i++) assembly.AppendLine($"    sw x{saved[i]}, {i * 4}(sp)");
        // 保留 ABI 的 gp/tp/sp；其他通用寄存器设为哨兵，两次快照覆盖 x0..x31。
        for (var reg = 1; reg < 32; reg++)
            if (reg is not (2 or 3 or 4)) assembly.AppendLine($"    li x{reg}, 0x{(reg == 5 ? 0xe000e204u : reg == 6 ? 1u << 12 : reg == 27 ? 0x2000d200u : 0x11000000u + (uint)reg):x8}");
        for (var reg = 0; reg < 32; reg++) assembly.AppendLine($"    sw x{reg}, {reg * 4}(s11)");
        assembly.AppendLine("    sw t1, 0(t0)\n    .rept 64\n    nop\n    .endr");
        for (var reg = 0; reg < 32; reg++) assembly.AppendLine($"    sw x{reg}, {128 + reg * 4}(s11)");
        for (var i = 0; i < saved.Length; i++) assembly.AppendLine($"    lw x{saved[i]}, {i * 4}(sp)");
        assembly.AppendLine("    addi sp, sp, 64\n    ret");
        await File.WriteAllTextAsync(Path.Combine(root, "registers.S"), assembly.ToString());
        var images = new List<Image>();
        foreach (var optimization in new[] { "O0", "Og", "O1", "O2", "O3", "Os" })
        {
            var elf = Path.Combine(root, optimization + ".elf");
            var binary = Path.Combine(root, optimization + ".bin");
            await RunTool(tools, root, "gcc", ["-march=rv32imac_xw", "-mabi=ilp32", "-msmall-data-limit=8", "-msave-restore",
                "-" + optimization, "-g3", "-ffreestanding", "-fno-builtin", "-ffunction-sections", "-fdata-sections", "-Wall", "-Wextra", "-Werror",
                "-nostdlib", "-nostartfiles", "-T", "ram.ld", "start.S", "registers.S", "main.c", "-lgcc", "-o", elf]);
            var bytes = await File.ReadAllBytesAsync(elf);
            if (!bytes.AsSpan(0, 7).SequenceEqual(new byte[] { 0x7f, 69, 76, 70, 1, 1, 1 })) throw new InvalidOperationException("Expected little-endian ELF32.");
            var entry = BitConverter.ToUInt32(bytes, 24);
            if (entry is < 0x20000000 or >= 0x2000c000) throw new InvalidOperationException("Entry must be in SRAM.");
            var offset = BitConverter.ToUInt32(bytes, 28);
            var size = BitConverter.ToUInt16(bytes, 42);
            var count = BitConverter.ToUInt16(bytes, 44);
            for (var i = 0; i < count; i++)
            {
                var p = checked((int)offset + i * size);
                if (BitConverter.ToUInt32(bytes, p) != 1) continue;
                var address = BitConverter.ToUInt32(bytes, p + 12);
                var length = BitConverter.ToUInt32(bytes, p + 20);
                if (address < 0x20000000 || (ulong)address + length > 0x2000c000) throw new InvalidOperationException("Load segment outside SRAM.");
            }
            await RunTool(tools, root, "objcopy", ["-O", "binary", elf, binary]);
            var data = await File.ReadAllBytesAsync(binary);
            if (data.Length is < 448 or > 49152) throw new InvalidOperationException("Unexpected RAM image size.");
            images.Add(new(optimization, entry, Convert.ToHexString(SHA256.HashData(data))));
            await File.WriteAllTextAsync(Path.Combine(root, optimization + ".disassembly.txt"), await RunTool(tools, root, "objdump", ["-d", elf]));
            Console.WriteLine($"PREPARED {optimization}: {data.Length} bytes, SRAM entry 0x{entry:x8}");
        }
        await JsonStore.WriteAsync(Path.Combine(root, "images.json"), images);
    }

    public static async Task RunAsync(string runtime, string output, string backup)
    {
        var root = Path.GetFullPath(output); var saved = Path.GetFullPath(backup);
        if (Directory.Exists(saved)) throw new ArgumentException("Use a new backup directory.");
        var tools = await Resolve(runtime);
        var images = await JsonStore.ReadAsync<Image[]>(Path.Combine(root, "images.json"));
        if (!images.Select(i => i.Name).SequenceEqual(new[] { "O0", "Og", "O1", "O2", "O3", "Os" }))
            throw new InvalidOperationException("Expected all six optimization levels in order.");
        foreach (var item in images)
        {
            if (item.Name is not ("O0" or "Og" or "O1" or "O2" or "O3" or "Os") || item.Entry is < 0x20000000 or >= 0x2000c000)
                throw new InvalidOperationException("Invalid RAM test manifest.");
            var bytes = await File.ReadAllBytesAsync(Path.Combine(root, item.Name + ".bin"));
            if (bytes.Length is < 448 or > 49152 || Convert.ToHexString(SHA256.HashData(bytes)) != item.Sha256)
                throw new InvalidOperationException("RAM image has changed.");
        }
        var recipe = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../examples/packs/wch.ch32v307"));
        var target = (await File.ReadAllTextAsync(Path.Combine(recipe, "ch32v307.cfg.in")))
            .Replace("@CHIP_ID@", "0x30700508").Replace("@DEVICE_ID@", "CH32V307VCT6");
        await File.WriteAllTextAsync(Path.Combine(root, "target.cfg"), target);
        Directory.CreateDirectory(saved);
        using var lease = ProbeLease.Acquire();
        var script = new StringBuilder("init\nhalt\nstudiox_check_target\n");
        script.AppendLine($"dump_image {Quote(Path.Combine(saved, "flash-before.bin"))} 0x08000000 0x48000");
        script.AppendLine($"dump_image {Quote(Path.Combine(saved, "options-before.bin"))} 0x1ffff800 16");
        script.AppendLine($"dump_image {Quote(Path.Combine(saved, "ram-before.bin"))} 0x20000000 0x10000");
        script.AppendLine("set failed [catch {");
        foreach (var item in images)
        {
            script.AppendLine("reset halt");
            // 唯一映像写入命令固定 SRAM 地址和 bin 格式；不调用 GDB load 或 Flash 编程命令。
            script.AppendLine($"load_image {Quote(Path.Combine(root, item.Name + ".bin"))} 0x20000000 bin");
            script.AppendLine($"verify_image {Quote(Path.Combine(root, item.Name + ".bin"))} 0x20000000 bin");
            script.AppendLine("mww 0x2000d000 0 96\nmww 0x2000d200 0 64");
            script.AppendLine($"resume 0x{item.Entry:x8}\nsleep 3000\nhalt");
            script.AppendLine($"dump_image {Quote(Path.Combine(root, item.Name + "-result.bin"))} 0x2000d000 384");
            script.AppendLine($"dump_image {Quote(Path.Combine(root, item.Name + "-registers.bin"))} 0x2000d200 256");
            script.AppendLine("set result [read_memory 0x2000d000 32 14]");
            script.AppendLine($"echo \"RAM_IRQ_RESULT {item.Name} $result\"");
            script.AppendLine("if {[lindex $result 0] != 0x3071abcd || [lindex $result 1] != 0x600d600d || [lindex $result 4] != 4000 || [lindex $result 5] != 2000 || [lindex $result 6] != 2000 || [lindex $result 7] != 1000 || [lindex $result 8] != 0} {error \"RAM interrupt check failed\"}");
        }
        script.AppendLine("} detail]");
        script.AppendLine($"dump_image {Quote(Path.Combine(saved, "flash-after.bin"))} 0x08000000 0x48000");
        script.AppendLine($"dump_image {Quote(Path.Combine(saved, "options-after.bin"))} 0x1ffff800 16");
        script.AppendLine("reset halt\nresume\npoll\nif {[[target current] curstate] ne \"running\"} {error \"Original firmware not running\"}\necho ORIGINAL_FLASH_RUNNING\nif {$failed} {error $detail}\nshutdown");
        var file = Path.Combine(root, "run-ram.cfg");
        await File.WriteAllTextAsync(file, script.ToString());
        var common = new[] { "-c", "gdb_port disabled", "-c", "tcl_port disabled", "-c", "telnet_port disabled",
            "-f", Path.Combine(recipe, "wch-link.cfg"), "-c", "transport select sdi", "-f", Path.Combine(root, "target.cfg"),
            "-c", "adapter speed 6000", "-c", "gdb_flash_program disable", "-c", "wch_riscv.cpu.0 configure -work-area-size 0" };
        var run = await OpenOcd([.. common, "-f", file], "hardware.log");
        if (!(run.StandardOutput + run.StandardError).Contains("ORIGINAL_FLASH_RUNNING", StringComparison.Ordinal))
            await OpenOcd([.. common, "-c", "init; halt; studiox_check_target; reset halt; resume; poll; if {[[target current] curstate] ne \"running\"} {error \"Recovery failed\"}; echo ORIGINAL_FLASH_RUNNING; shutdown"], "recovery.log");
        var hashes = new List<object>();
        foreach (var prefix in new[] { "flash", "options" })
        {
            if (!File.Exists(Path.Combine(saved, prefix + "-after.bin")))
                throw new InvalidOperationException("Missing " + prefix + " readback; see hardware/recovery logs.");
            var before = await File.ReadAllBytesAsync(Path.Combine(saved, prefix + "-before.bin"));
            var after = await File.ReadAllBytesAsync(Path.Combine(saved, prefix + "-after.bin"));
            var expectedLength = prefix == "flash" ? 0x48000 : 16;
            if (before.Length != expectedLength || after.Length != expectedLength)
                throw new InvalidOperationException($"Incomplete {prefix} readback ({before.Length}/{after.Length}, expected {expectedLength}); timeout={run.TimedOut}. Not evidence of a content change.");
            if (!before.AsSpan().SequenceEqual(after)) throw new InvalidOperationException(prefix + " readback changed!");
            hashes.Add(new { region = prefix, length = before.Length, sha256 = Convert.ToHexString(SHA256.HashData(before)), equal = true });
        }
        await JsonStore.WriteAsync(Path.Combine(saved, "comparison.json"), hashes);
        if (!run.Success) throw new InvalidOperationException("Hardware test failed; see hardware.log and readback comparison.");
        await File.WriteAllTextAsync(Path.Combine(root, "hardware-result.txt"), "PASS: CH32V307VCT6, six optimization levels; each has 1000 single fast, triple fast nesting, software-stack, mixed four-level nesting iterations. Full integer-register snapshots match, 9000 interrupts per level. Flash/option bytes unchanged; original firmware reset and running.\n");
        Console.WriteLine(await File.ReadAllTextAsync(Path.Combine(root, "hardware-result.txt")));

        async Task<ProcessResult> OpenOcd(string[] arguments, string log)
        {
            var result = await new ProcessRunner().RunAsync(new(tools.Tool("openocd"), arguments, root, TimeSpan.FromSeconds(120),
                ToolsetEnvironment.Create(tools), RemoveEnvironment: ToolsetEnvironment.AmbientVariables, Output: new ConsoleProgress()));
            await File.WriteAllTextAsync(Path.Combine(root, log), result.StandardOutput + result.StandardError);
            return result;
        }
    }
    private static string Quote(string value)
    {
        if (value.IndexOfAny(['"', '$', '[', ']', '\r', '\n']) >= 0) throw new ArgumentException("Unsupported Tcl path.");
        return "\"" + value.Replace('\\', '/') + "\"";
    }
    private static Task<ResolvedToolset> Resolve(string runtime) => new ToolsetCatalog(Path.Combine(Path.GetFullPath(runtime), "toolsets"))
        .ResolveAsync("wch.riscv", "1.0.0", "wch-gcc-12.2.0-v1.4");
    private sealed class ConsoleProgress : IProgress<string>
    {
        public void Report(string value) => Console.Write(value);
    }
    private static async Task<string> RunTool(ResolvedToolset tools, string root, string role, string[] args)
    {
        var result = await new ProcessRunner().RunAsync(new(tools.Tool(role), args, root, TimeSpan.FromSeconds(30),
            ToolsetEnvironment.Create(tools), RemoveEnvironment: ToolsetEnvironment.AmbientVariables));
        if (!result.Success) throw new InvalidOperationException(result.StandardOutput + result.StandardError);
        return result.StandardOutput;
    }
}
