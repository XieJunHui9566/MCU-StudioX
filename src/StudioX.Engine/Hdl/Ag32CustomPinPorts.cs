namespace StudioX.Engine.Hdl;

using System.Text;
using System.Text.RegularExpressions;
using StudioX.Foundation;

internal sealed partial class Ag32CustomPinPorts(Ag32VerilogPort[] ports, string signature,
    IReadOnlySet<string> declared, IReadOnlySet<string> internalPorts)
{
    internal const string Marker = "#@StudioX:VERILOG_PINS 1";
    internal Ag32VerilogPort[] Ports { get; } = ports;
    internal string Signature { get; } = signature;

    internal static bool IsManaged(byte[] source) => MarkerPattern().IsMatch(Encoding.UTF8.GetString(source).TrimStart('\uFEFF'));

    internal static byte[] Manage(byte[] source)
    {
        if (IsManaged(source))
        {
            return source;
        }
        var nl = Encoding.UTF8.GetString(source).Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        return [.. source, .. Encoding.UTF8.GetBytes((source.LastOrDefault() is (byte)'\r' or (byte)'\n' || source.Length == 0 ? "" : nl) + Marker + nl)];
    }

    internal static async Task<Ag32CustomPinPorts> ReadAsync(ToolsetCatalog catalog, ResolvedToolset converter,
        string root, Ag32NativeBuildSettings settings, CancellationToken token,
        ResolvedToolset? nativeToolset = null, bool readExternalPins = true)
    {
        var input = settings.Inputs;
        HdlSchematicInputs.Validate(root, input);
        if (!input.Sources.Contains("logic/user_logic.v", StringComparer.OrdinalIgnoreCase))

        {
            throw Error("逻辑构建源文件需要包含 logic/user_logic.v。");
        }
        var work = Directory.CreateTempSubdirectory("studiox-ve-ports-").FullName;
        try
        {
            var source = Path.Combine(work, "source");
            var hashes = await HdlSchematicInputs.SnapshotAsync(root, source, input, token);
            var includes = HdlSchematicInputs.PrepareIncludes(source, input, hashes.Keys, token);
            var runtime = Path.GetDirectoryName(catalog.RootDirectory)!;
            var executable = nativeToolset?.Tool("mapper") ??
                (await new HdlSchematicEngine(runtime).ResolveFrontendAsync(token)).Executable;
            var arguments = string.Join(" ", includes.Keys.Select(d => "-I" + d).Concat(input.Defines.Select(d => "-D" + d)));
            var files = string.Join(" ", input.Sources.Select(HdlSchematicEngine.Quote));
            // -lib 只保留接口，顶层声明的参数默认值和位宽仍由 Verilog 前端计算。
            var script = $"read_verilog -sv -lib -nodpi {arguments} {files}\nhierarchy -top user_logic\nwrite_json ../ports.json\n";
            await File.WriteAllTextAsync(Path.Combine(work, "ports.ys"), script, token);
            var result = await new ProcessRunner().RunAsync(new(executable, ["-Q", "-T", "-s", "../ports.ys"],
                source, TimeSpan.FromSeconds(15), nativeToolset is null ? null : ToolsetEnvironment.Create(nativeToolset),
                ContainProcessTree: true), token);
            var json = Path.Combine(work, "ports.json");
            if (!result.Success || !File.Exists(json))

            {
                throw Error("无法读取 user_logic 顶层端口。请修正已保存的 Verilog 或构建配置。\n" + result.StandardError + result.StandardOutput);
            }
            if (!await HdlSchematicInputs.IsCurrentAsync(root, hashes, input, token))

            {
                throw Error("Verilog 源码在端口读取时发生变化，请重新读取。");
            }
            var top = YosysNetlistReader.Read(await File.ReadAllTextAsync(json, token)).Single(m => m.Name == "user_logic");
            var vendor = await File.ReadAllTextAsync(converter.Tool("converter"), token);
            // 转换器自动提供系统时钟、复位和 MCU 内部总线的命名端口。
            var internalPorts = ReservedPattern().Matches(vendor).Select(m => m.Groups["port"].Value)
                .Where(name => name is "sys_clock" or "bus_clock" or "resetn" or "stop" or "local_int" ||
                    name.StartsWith("mem_ahb_", StringComparison.Ordinal) || name.StartsWith("slave_ahb_", StringComparison.Ordinal) ||
                    name.StartsWith("ext_dma_", StringComparison.Ordinal) || name.StartsWith("pll_phase", StringComparison.Ordinal))
                .ToHashSet(StringComparer.Ordinal);
            var ports = top.Ports.Where(port => readExternalPins && !internalPorts.Contains(port.Name)).Select(port =>
            {
                if (!IdentifierPattern().IsMatch(port.Name) || port.Offset < 0 || port.Bits.Length is < 1 or > 4096 ||
                    (long)port.Offset + port.Bits.Length > int.MaxValue)

                {
                    throw Error($"端口 {port.Name} 超出引脚规划支持的标识符或位宽范围。");
                }
                return new Ag32VerilogPort(port.Name, port.Direction.ToUpperInvariant(),
                    port.IndexAt(port.Bits.Length - 1), port.IndexAt(0));
            }).ToArray();
            if (ports.Sum(port => (long)Math.Abs(port.Left - port.Right) + 1) > 8192)

            {
                throw Error("顶层可分配端口总位数超过 8192。");
            }
            return new(ports, HdlSchematicInputs.Digest(hashes, input),
                top.Ports.Select(port => port.Name).ToHashSet(StringComparer.Ordinal), internalPorts);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    // 稀疏总线、升序总线和单个带下标的位不能直接交给厂商的连续降序拼接。
    internal Dictionary<string, string> Aliases(IReadOnlyList<Ag32PinAssignment> assignments)
    {
        var assigned = assignments.Select(pin => pin.Function).ToHashSet(StringComparer.Ordinal);
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var port in Ports.Where(port => port.IsBus))
        {
            var signals = port.Signals.ToArray();
            var mapped = signals.Where(assigned.Contains).ToArray();
            if (mapped.Length == 0 || mapped.Length > 1 && mapped.Length == signals.Length && port.Left > port.Right)

            {
                continue;
            }
            foreach (var signal in mapped)
            {
                var index = signal[(port.Name.Length + 1)..^1];
                var alias = $"studiox_io_{port.Name}_{index}";
                if (assigned.Contains(alias) || Ports.Any(other => other.Name == alias))

                {
                    throw Error($"端口 {signal} 的转换名称 {alias} 与已有端口冲突。");
                }
                aliases.Add(signal, alias);
            }
        }
        return aliases;
    }

    internal static byte[] ConverterSource(byte[] source, IReadOnlyDictionary<string, string> aliases)
    {
        var text = Encoding.UTF8.GetString(source).TrimStart('\uFEFF');
        return Encoding.UTF8.GetBytes(AssignmentNamePattern().Replace(text, match =>
            aliases.TryGetValue(match.Groups["name"].Value, out var alias)
                ? match.Groups["indent"].Value + alias : match.Value));
    }

    internal async Task ReconnectAsync(string wrapper, string template, IReadOnlyDictionary<string, string> aliases, CancellationToken token)
    {
        var text = await File.ReadAllTextAsync(wrapper, token);
        var instance = InstancePattern().Match(text);
        if (!instance.Success)
        {
            throw Error("厂商输出中缺少 user_logic 实例。");
        }
        // 厂商每行写入一个命名连接，保留总线拼接表达式中的逗号。
        var connections = instance.Groups["ports"].Value.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd(',')).ToList();
        var decl = new StringBuilder();
        var omitted = connections.Select(connection => (Line: connection, Port: ConnectionPattern().Match(connection)))
            .Where(item => item.Port.Success && internalPorts.Contains(item.Port.Groups["name"].Value) &&
                !declared.Contains(item.Port.Groups["name"].Value)).ToArray();
        if (omitted.Length != 0)
        {
            // tri0/tri1 由同次转换生成。省略模块输出时，按模板的空闲电平驱动 MCU 侧。
            var defaults = TemplatePortPattern().Matches(await File.ReadAllTextAsync(template, token))
                .ToDictionary(match => match.Groups["name"].Value, StringComparer.Ordinal);
            foreach (var (line, port) in omitted)
            {
                var name = port.Groups["name"].Value;
                if (!defaults.TryGetValue(name, out var definition))

                {
                    throw Error($"无法核对 MCU 内部端口 {name} 的厂商模板。");
                }
                if (definition.Groups["direction"].Value == "output")
                {
                    var level = definition.Groups["level"].Value;
                    var wire = port.Groups["wire"].Value.Trim();
                    if (level.Length == 0 || !IdentifierPattern().IsMatch(wire))

                    {
                        throw Error($"无法生成 MCU 内部端口 {name} 的默认电平。");
                    }
                    var width = definition.Groups["left"].Success
                        ? Math.Abs(int.Parse(definition.Groups["left"].Value) - int.Parse(definition.Groups["right"].Value)) + 1
                        : 1;
                    decl.AppendLine($"assign {wire} = {width}'b{new string(level[0], width)};");
                }
                connections.Remove(line);
            }
        }
        if (omitted.Length == 0 && aliases.Count == 0)
        {
            return;
        }
        foreach (var port in Ports)
        {
            var signals = port.Signals.ToArray();
            if (!signals.Any(aliases.ContainsKey))
            {
                continue;
            }
            var wires = new List<string>();
            foreach (var signal in signals)
            {
                if (aliases.TryGetValue(signal, out var alias))
                {
                    if (connections.RemoveAll(c => ConnectionPattern().Match(c).Groups["name"].Value == alias) != 1)

                    {
                        throw Error("厂商输出中缺少唯一的接线：" + signal);
                    }
                    wires.Add(alias);
                }
                else if (port.Direction == "INPUT")

                {
                    wires.Add("1'b0");
                }
                else
                {
                    var unused = "studiox_unused_" + signal.Replace('[', '_').TrimEnd(']');
                    decl.AppendLine("wire " + unused + ";");
                    wires.Add(unused);
                }
            }
            connections.Add("." + port.Name + "(" + (wires.Count == 1 ? wires[0] : "{" + string.Join(", ", wires) + "}") + ")");
        }
        var replacement = decl.ToString() + "user_logic macro_inst(\n  " + string.Join(",\n  ", connections) + "\n);";
        await File.WriteAllTextAsync(wrapper, text.Remove(instance.Index, instance.Length).Insert(instance.Index, replacement), token);
    }

    private static StudioXException Error(string message) => new("AG32_VERILOG_PORTS", message);
    [GeneratedRegex(@"(?m)^#@StudioX:VERILOG_PINS 1\r?$")]
    private static partial Regex MarkerPattern();
    [GeneratedRegex(@"\A[A-Za-z_][A-Za-z0-9_$]*\z")]
    private static partial Regex IdentifierPattern();
    [GeneratedRegex("""PinWire\("(?<port>[A-Za-z_][A-Za-z0-9_]*)",""")]
    private static partial Regex ReservedPattern();
    [GeneratedRegex(@"(?m)^(?<indent>[ \t]*)(?<name>[A-Za-z_][A-Za-z0-9_$]*(?:\[[0-9]+\])?)(?=[ \t]+PIN_[0-9]+\b)")]
    private static partial Regex AssignmentNamePattern();
    [GeneratedRegex(@"(?ms)^user_logic macro_inst\(\r?\n(?<ports>.*?)\r?\n\);")]
    private static partial Regex InstancePattern();
    [GeneratedRegex(@"^\.(?<name>[A-Za-z_][A-Za-z0-9_$]*)\s*\((?<wire>.+)\)$")]
    private static partial Regex ConnectionPattern();
    [GeneratedRegex(@"(?m)^[ \t]*(?<direction>input|output|inout)[ \t]+(?:tri(?<level>[01])[ \t]+)?(?:\[(?<left>[0-9]+):(?<right>[0-9]+)\][ \t]+)?(?<name>[A-Za-z_][A-Za-z0-9_$]*)[ \t]*,?\r?$")]
    private static partial Regex TemplatePortPattern();
}
