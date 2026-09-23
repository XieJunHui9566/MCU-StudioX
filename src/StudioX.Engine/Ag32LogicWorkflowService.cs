namespace StudioX.Engine;

using System.Text.RegularExpressions;
using StudioX.Foundation;

/// <summary>检查 AG32 逻辑子工程和厂商工具交接产物；不启动综合或烧录程序。</summary>
public sealed class Ag32LogicWorkflowService
{
    public const string QuartusEnvironmentVariable = "STUDIOX_AG32_QUARTUS";
    public const string SupraEnvironmentVariable = "STUDIOX_AG32_SUPRA";
    public const long ReservedLogicBytes = 100 * 1024;
    private static readonly Regex PinPattern = new(@"\bPIN_(\d+)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public Task<Ag32LogicInspection> InspectAsync(string projectDirectory, CancellationToken token = default) =>
        Task.Run(() => InspectCoreAsync(projectDirectory, token), token);

    private static async Task<Ag32LogicInspection> InspectCoreAsync(string projectDirectory, CancellationToken token)
    {
        var root = Path.GetFullPath(projectDirectory);
        var project = await ProjectService.ReadAsync(root, token);
        var logic = project.Logic ?? throw new StudioXException("AG32_LOGIC_DISABLED", "当前工程未在创建时启用 AG32 逻辑/Verilog 特殊模式。");
        if (project.Kind != ProjectKind.Pack || project.DeviceId != "AG32VF303CCT6" || logic.TargetDevice != "AGRV2KL48" ||
            logic.VerilogFile != "logic/user_logic.v" || logic.PinMapFile != "logic/pins.ve")
            throw new StudioXException("AG32_LOGIC_TARGET", "逻辑模式目前只支持 AG32VF303CCT6 的 LQFP48 / AGRV2KL48 工程骨架。");

        var verilog = PathBoundary.Resolve(root, logic.VerilogFile);
        var pinMap = PathBoundary.Resolve(root, logic.PinMapFile);
        // 厂商以 VE 文件名作为设计名；输出 BIN 与 MCU 固件构建结果分开存放。
        var binary = Path.ChangeExtension(pinMap, ".bin");
        var errors = new List<string>();
        var notes = new List<string>();
        if (!File.Exists(verilog)) errors.Add("缺少逻辑源码：" + logic.VerilogFile);
        if (!File.Exists(pinMap)) errors.Add("缺少引脚映射：" + logic.PinMapFile);

        var assignedPins = new Dictionary<int, int>();
        if (File.Exists(pinMap))
        {
            var lines = await File.ReadAllLinesAsync(pinMap, token);
            for (var index = 0; index < lines.Length; index++)
            {
                // VE 的 # 注释不参与 PIN 映射；这里只检查封装范围，重复脚需结合厂商复用规则人工核对。
                var line = lines[index].Split('#', 2)[0];
                foreach (Match match in PinPattern.Matches(line))
                {
                    if (!int.TryParse(match.Groups[1].Value, out var number) || number is < 1 or > 48)
                    {
                        errors.Add($"{logic.PinMapFile}:{index + 1}：{match.Value} 超出 LQFP48 的 1–48 脚范围。");
                        continue;
                    }
                    if (assignedPins.TryGetValue(number, out var previous))
                        notes.Add($"{logic.PinMapFile}:{index + 1}：PIN_{number} 也出现在第 {previous} 行，请按 AGM 复用规则和板级原理图核对。");
                    else assignedPins[number] = index + 1;
                }
            }
            if (assignedPins.Count == 0) notes.Add("pins.ve 尚无实际 PIN_N 映射；请按板级原理图填写，不能套用 100 脚示例。");
        }
        notes.Add("静态检查仅覆盖引脚编号和重复映射；固定功能、电气约束及 MCU/CPLD 复用需在厂商工具和板级资料中核对。");

        long? binaryBytes = null;
        if (File.Exists(binary))
        {
            var info = new FileInfo(binary);
            binaryBytes = info.Length;
            if (info.Length == 0 || info.Length > ReservedLogicBytes)
                errors.Add($"逻辑镜像大小 {info.Length} 字节，不在 1–{ReservedLogicBytes} 字节的保留逻辑区范围内。");
            if (File.Exists(verilog) && info.LastWriteTimeUtc < File.GetLastWriteTimeUtc(verilog) ||
                File.Exists(pinMap) && info.LastWriteTimeUtc < File.GetLastWriteTimeUtc(pinMap))
                notes.Add("逻辑 BIN 的修改时间早于源码或 VE；请重新执行 Quartus II 和 Supra 编译。");
        }
        else notes.Add("尚未找到逻辑 BIN；Quartus II 生成 VO 后，需要用 Supra 编译，并将产物放在 logic/pins.bin。");

        return new(root, logic, verilog, pinMap, binary, assignedPins.Count, binaryBytes,
            FindTool(QuartusEnvironmentVariable, "quartus_sh.exe", "quartus.exe"),
            FindTool(SupraEnvironmentVariable, "Supra.exe"), errors, notes);
    }

    private static string? FindTool(string environmentVariable, params string[] names)
    {
        var configured = Environment.GetEnvironmentVariable(environmentVariable)?.Trim().Trim('"');
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (File.Exists(configured) && names.Contains(Path.GetFileName(configured), StringComparer.OrdinalIgnoreCase))
                return Path.GetFullPath(configured);
            if (Directory.Exists(configured))
                foreach (var name in names)
                {
                    var path = Path.Combine(configured, name);
                    if (File.Exists(path)) return Path.GetFullPath(path);
                    path = Path.Combine(configured, "bin", name);
                    if (File.Exists(path)) return Path.GetFullPath(path);
                }
        }
        foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var directory = entry.Trim().Trim('"');
            if (directory.Length == 0) continue;
            foreach (var name in names)
            {
                var path = Path.Combine(directory, name);
                if (File.Exists(path)) return Path.GetFullPath(path);
            }
        }
        return null;
    }
}

public sealed record Ag32LogicInspection(string ProjectDirectory, Ag32LogicProjectSettings Settings,
    string VerilogPath, string PinMapPath, string BinaryPath, int AssignedPinCount, long? BinaryBytes,
    string? QuartusExecutable, string? SupraExecutable, IReadOnlyList<string> Errors, IReadOnlyList<string> Notes);
