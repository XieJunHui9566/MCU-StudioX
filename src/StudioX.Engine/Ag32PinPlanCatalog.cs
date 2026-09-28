namespace StudioX.Engine;

using System.Globalization;
using System.Text.RegularExpressions;
using StudioX.Foundation;

/// <summary>从已校验的转换器查询封装与功能，不以营销型号字符串猜测引脚。</summary>
internal sealed record Ag32PinPlanCatalog(Ag32PackagePin[] Pins, Ag32PinFunction[] Functions)
{
    internal static async Task<Ag32PinPlanCatalog> ReadAsync(ResolvedToolset tools, string target, int pinCount,
        string workingDirectory, CancellationToken token)
    {
        var result = await new ProcessRunner().RunAsync(new(tools.Tool("python"),
            ["-I", "-B", "-X", "utf8", tools.Tool("converter"), "-d", target, "-p", "-f"], workingDirectory,
            TimeSpan.FromSeconds(30), ToolsetEnvironment.Create(tools),
            RemoveEnvironment: ToolsetEnvironment.AmbientVariables.Append("ALTA_HOME").ToArray()), token);
        if (!result.Success || result.OutputTruncated)
        {
            throw new StudioXException("AG32_PIN_PLAN_CATALOG", "无法读取厂商引脚目录：\n" + result.StandardOutput + result.StandardError);
        }
        var pins = Regex.Matches(result.StandardOutput, @"^PIN_([0-9]+)\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)
            .Select(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)).ToHashSet();
        if (pins.Count == 0 || pins.Any(pin => pin < 1 || pin > pinCount))
        {
            throw new StudioXException("AG32_PIN_PLAN_CATALOG", "厂商引脚列表与已确认封装不一致。");
        }
        var source = await File.ReadAllTextAsync(tools.Tool("converter"), token);
        var functions = new Dictionary<string, Ag32PinFunction>(StringComparer.Ordinal);
        foreach (Match gpio in Regex.Matches(result.StandardOutput, @"^(GPIO[0-9]+_[0-9]+)(?:,|\s*$)",
            RegexOptions.Multiline | RegexOptions.CultureInvariant))
        {
            var name = gpio.Groups[1].Value;
            functions.Add(name, new(name, "INOUT", name));
        }
        // -f 对独立功能不打印方向；在同一已纳入 SHA-256 的脚本中只读取数据字面量，绝不执行新代码。
        foreach (Match function in Regex.Matches(source,
            @"'([A-Z][A-Z0-9_]*)'\s*:\s*FuncPinInfo\('([^']*)',\s*'(INPUT|OUTPUT|INOUT)'\)", RegexOptions.CultureInvariant))
        {
            var name = function.Groups[1].Value;
            if (!Regex.IsMatch(result.StandardOutput, @"(?:^|,\s*)" + Regex.Escape(name) + @"(?:\((?:INPUT|OUTPUT|INOUT)\)|\s*$)",
                RegexOptions.Multiline | RegexOptions.CultureInvariant))
            {
                throw new StudioXException("AG32_PIN_PLAN_CATALOG", "功能数据与厂商查询结果不一致：" + name);
            }
            var gpio = function.Groups[2].Value;
            if (gpio.Length != 0 && !functions.ContainsKey(gpio))
            {
                throw new StudioXException("AG32_PIN_PLAN_CATALOG", "功能使用不存在的内部 GPIO：" + name);
            }
            functions.Add(name, new(name, function.Groups[3].Value, gpio.Length == 0 ? null : gpio));
        }
        if (functions.Count == 0)
        {
            throw new StudioXException("AG32_PIN_PLAN_CATALOG", "厂商功能目录为空。");
        }
        return new(Enumerable.Range(1, pinCount).Select(pin => new Ag32PackagePin(pin, pins.Contains(pin))).ToArray(), functions.Values.ToArray());
    }

    internal void Validate(IReadOnlyList<Ag32PinAssignment> assignments, Ag32PinClockSettings clocks)
    {
        if (assignments.Count > Pins.Count(pin => pin.CanAssign))
        {
            throw new StudioXException("AG32_PIN_PLAN_CONFLICT", "映射数量超过封装可用引脚数量。");
        }
        var known = Functions.ToDictionary(function => function.Name, StringComparer.Ordinal);
        foreach (var assignment in assignments)
        {
            if (assignment is null || assignment.Function is null || !known.TryGetValue(assignment.Function, out var function))
            {
                throw new StudioXException("AG32_PIN_PLAN_FUNCTION", "映射功能不在锁定的厂商目录中。");
            }
            if (!Pins.Any(pin => pin.Number == assignment.PinNumber && pin.CanAssign))
            {
                throw new StudioXException("AG32_PIN_PLAN_PIN", $"PIN_{assignment.PinNumber} 不是此封装的可映射引脚。");
            }
            if (assignment.Direction is { } direction && (direction is not ("INPUT" or "OUTPUT" or "INOUT") ||
                function.Direction != "INOUT" && direction != function.Direction))
            {
                throw new StudioXException("AG32_PIN_PLAN_FUNCTION", "方向与厂商功能定义不一致：" + assignment.Function);
            }
        }
        // 保存与草稿使用同一规则，完整报告涉及的功能和引脚，不自动覆盖或合并旧分配。
        var conflicts = Ag32PinPlanConflicts.Find(assignments, Functions);
        if (conflicts.Length != 0)
        {
            throw new StudioXException("AG32_PIN_PLAN_CONFLICT", string.Join("\n", conflicts.Select(conflict => conflict.Message)));
        }
        if (clocks.HseMhz is <= 0 || clocks.SysMhz is <= 0 || clocks.BusMhz is < 0)
        {
            throw new StudioXException("AG32_PIN_PLAN_CLOCK", "HSECLK、SYSCLK 必须为正数；BUSCLK 为零表示沿用 SYSCLK。");
        }
    }
}
