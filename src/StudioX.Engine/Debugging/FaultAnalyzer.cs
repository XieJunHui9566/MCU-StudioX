namespace StudioX.Engine.Debugging;

using System.Globalization;
using System.Text.RegularExpressions;
using StudioX.Foundation;

/// <summary>解码 Arm CMSIS 状态位和 ESP Panic 地址；线索不等同于根因证明。</summary>
public static class FaultAnalyzer
{
    public static FaultAnalysisReport Analyze(FaultEvidence evidence)
    {
        if (evidence.FormatVersion != 1 || evidence.Raw is null || evidence.Raw.Length > 1024 * 1024)
        {
            throw new StudioXException("FAULT_FORMAT", "故障现场格式不支持或超过 1 MiB。");
        }
        var findings = new List<string>();
        var addresses = new List<uint>();
        addresses.AddRange(evidence.Backtrace ?? []);
        if (evidence.Cfsr is { } cfsr)
        {
            findings.Add($"CFSR=0x{cfsr:X8}");
            (int Bit, string Text)[] bits =
            [
                (0, "IACCVIOL：指令访问违反内存保护"), (1, "DACCVIOL：数据访问违反内存保护"),
                (3, "MUNSTKERR：异常出栈时发生内存保护错误"), (4, "MSTKERR：异常入栈时发生内存保护错误"),
                (5, "MLSPERR：浮点惰性保存发生内存保护错误"), (8, "IBUSERR：取指总线错误"),
                (9, "PRECISERR：精确数据总线错误"), (10, "IMPRECISERR：非精确总线错误，堆栈 PC 不一定指向故障指令"),
                (11, "UNSTKERR：异常出栈总线错误"), (12, "STKERR：异常入栈总线错误"),
                (13, "LSPERR：浮点惰性保存总线错误"), (16, "UNDEFINSTR：未定义指令"), (17, "INVSTATE：无效执行状态"),
                (18, "INVPC：异常返回 PC 无效"), (19, "NOCP：访问不可用的协处理器"),
                (24, "UNALIGNED：未对齐访问"), (25, "DIVBYZERO：除零")
            ];
            foreach (var bit in bits)
            {
                if ((cfsr & (1u << bit.Bit)) != 0)
                {
                    findings.Add(bit.Text);
                }
            }
            if ((cfsr & (1u << 7)) != 0 && evidence.Mmfar is { } mmfar)
            {
                findings.Add($"MMARVALID：有效故障地址 0x{mmfar:X8}");
            }
            if ((cfsr & (1u << 15)) != 0 && evidence.Bfar is { } bfar)
            {
                findings.Add($"BFARVALID：有效总线故障地址 0x{bfar:X8}");
            }
            if (cfsr == 0)
            {
                findings.Add("CFSR 未记录可配置故障；不能据此确认程序没有异常。");
            }
        }
        if (evidence.Hfsr is { } hfsr)
        {
            findings.Add($"HFSR=0x{hfsr:X8}");
            if ((hfsr & (1u << 30)) != 0)
            {
                findings.Add("FORCED：可配置故障升级为 HardFault，请结合 CFSR。");
            }
            if ((hfsr & 2) != 0)
            {
                findings.Add("VECTTBL：读取异常向量时发生总线错误。");
            }
        }
        if (evidence.StackedPc is { } pc)
        {
            addresses.Add(pc);
            findings.Add($"异常堆栈 PC=0x{pc:X8}");
        }
        if (evidence.StackedLr is { } lr && (lr & 0xfffffff0) != 0xfffffff0)
        {
            addresses.Add(lr);
        }
        // 只解析明确的回溯行，避免把寄存器值或 ESP 的 SP 误当作代码地址。
        foreach (var line in evidence.Raw.Split('\n').Where(l => l.Contains("Backtrace:", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (Match match in Regex.Matches(line, @"0x([0-9a-fA-F]{8}):0x[0-9a-fA-F]{8}", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
            {
                addresses.Add(uint.Parse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
            }
        }
        foreach (var line in evidence.Raw.Split('\n').Where(l => l.Contains("Guru Meditation", StringComparison.OrdinalIgnoreCase) || l.StartsWith("panic", StringComparison.OrdinalIgnoreCase)).Take(8))
        {
            findings.Add(line.Trim());
        }
        if (findings.Count == 0)
        {
            findings.Add("未找到可解码的故障状态或回溯；原始日志保留，请导入完整现场。");
        }
        findings.Add("状态位和回溯是诊断线索；优化、堆栈损坏或旧 ELF 会影响定位。");
        return new(evidence, findings, addresses.Distinct().Take(64).ToArray());
    }
}
