namespace StudioX.Engine;

using System.Text.RegularExpressions;
using StudioX.Foundation;

/// <summary>检查已生成的 naked 控制流是否可落入下一函数；不替代汇编器，也不猜测宏/原始字节的行为。</summary>
internal static class Mx251NakedDiagnostics
{
    internal static void Validate(string run)
    {
        foreach (var file in Directory.EnumerateFiles(run, "*.asm"))
        {
            var lines = File.ReadAllLines(file);
            var function = "";
            for (var i = 0; i < lines.Length; i++)
            {
                var name = Regex.Match(lines[i], @"^;\s+function\s+(\w+)");
                if (name.Success)
                {
                    function = name.Groups[1].Value;
                }
                if (!lines[i].Contains("naked function: no prologue.", StringComparison.Ordinal))
                {
                    continue;
                }
                var start = i + 1;
                while (i < lines.Length && !lines[i].Contains("naked function: no epilogue.", StringComparison.Ordinal))
                {
                    i++;
                }
                if (ReachesEnd(lines[start..i]))
                {
                    throw new StudioXException("MX251_NAKED_FALLTHROUGH",
                    $"{file}:{start}:1: error: __naked 函数 {function} 存在未返回路径，会落入后续代码。普通 C 函数请移除 __naked；纯汇编函数须完整返回或跳转。阻塞延时可直接使用 Delay_MS(毫秒数)。");
                }
            }
        }
    }

    private static bool ReachesEnd(string[] lines)
    {
        var code = new List<(string Op, string Args)>();
        var labels = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            var text = line.Split(';')[0].Trim();
            var label = Regex.Match(text, @"^([\w.$]+):+\s*");
            if (label.Success)
            {
                labels[label.Groups[1].Value] = code.Count;
                text = text[label.Length..];
            }
            if (text.Length == 0 || Regex.IsMatch(text, @"^\w+\s*="))
            {
                continue;
            }
            var instruction = Regex.Match(text, @"^(\S+)\s*(.*)$");
            code.Add((instruction.Groups[1].Value.ToLowerInvariant(), instruction.Groups[2].Value.Trim()));
        }
        var pending = new Stack<int>();
        pending.Push(0);
        var visited = new HashSet<int>();
        while (pending.TryPop(out var index))
        {
            if (index == code.Count)
            {
                return true;
            }
            if (!visited.Add(index))
            {
                continue;
            }
            var (op, args) = code[index];
            if (op is "ret" or "eret" or "reti")
            {
                continue;
            }
            if (op is "sjmp" or "ljmp" or "ajmp" or "ejmp" or "jmp")
            {
                if (labels.TryGetValue(args, out var target))
                {
                    pending.Push(target);
                }
                continue;
            }
            if (op.StartsWith('j') || op is "djnz" or "cjne")
            {
                if (labels.TryGetValue(args.Split(',')[^1].Trim(), out var target))
                {
                    pending.Push(target);
                }
                pending.Push(index + 1);
                continue;
            }
            if (op is "mov" or "movx" or "movc" or "movh" or "movz" or "movs" or "push" or "pop" or
                "nop" or "inc" or "dec" or "add" or "addc" or "sub" or "subb" or "cmp" or "anl" or "orl" or
                "xrl" or "clr" or "cpl" or "setb" or "swap" or "rl" or "rr" or "rlc" or "rrc" or "sll" or
                "srl" or "sra" or "mul" or "div" or "xch" or "xchd" or "da" or "ecall" or "lcall" or "acall")
            {
                pending.Push(index + 1);
            }
            // 未识别的宏/数据不是“确定会落下”的证据，不假装完成任意手写汇编验证。
        }
        return false;
    }
}
