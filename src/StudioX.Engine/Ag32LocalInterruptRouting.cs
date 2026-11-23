namespace StudioX.Engine;

using System.Text.RegularExpressions;

internal static class Ag32LocalInterruptRouting
{
    // EXT_INT 输入缓冲已经由厂商转换器按目标封装实例化。复用同一个低有效输入，
    // 仅连接处理器的对应 local_int 位，其余位仍来自原宏或厂商默认连线。
    internal static async Task ApplyAsync(string directory, byte[] ve, CancellationToken token, string wrapper = "pins.vx")
    {
        var setup = Ag32Setup.Read(ve);
        var enabled = setup.Units.Keys.Where(k => k.StartsWith("LOCAL_INT", StringComparison.Ordinal)).ToArray();
        if (enabled.Length == 0)

        {
            return;
        }
        var path = Path.Combine(directory, wrapper);
        var netlist = await File.ReadAllTextAsync(path, token);
        var cpu = Regex.Matches(netlist, @"\balta_rv32\s+rv32\s*\((?<ports>[\s\S]*?)\);");
        if (cpu.Count != 1)

        {
            throw Ag32Setup.Error("无法核对厂商网表的 RV32 实例。");
        }
        var ports = cpu[0].Groups["ports"];
        var external = Regex.Match(ports.Value, @"\.ext_int\s*\(\s*\{([^{}]+)\}\s*\)");
        var local = Regex.Matches(ports.Value, @"\.local_int\s*\(\s*local_int\s*\)");
        var signals = external.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries);
        if (!external.Success || signals.Length != 8 || local.Count != 1)

        {
            throw Ag32Setup.Error("无法核对厂商网表的中断端口。");
        }
        var wire = Enumerable.Range(0, 4).Reverse().Select(i => $"local_int[{i}]").ToArray();
        foreach (var unit in enabled)
        {
            var input = setup.Value(unit, "Source");
            var signal = signals[7 - (input[^1] - '0')];
            if (!Regex.IsMatch(signal, @"\A!PIN_\d+_in\z"))

            {
                throw Ag32Setup.Error(input + " 没有接到已分配的物理输入缓冲器。");
            }
            wire[3 - (unit[^1] - '0')] = signal;
        }
        var field = local[0];
        var index = ports.Index + field.Index;
        netlist = netlist.Remove(index, field.Length).Insert(index, ".local_int ({" + string.Join(", ", wire) + "})");
        await File.WriteAllTextAsync(path, netlist, token);
    }
}
