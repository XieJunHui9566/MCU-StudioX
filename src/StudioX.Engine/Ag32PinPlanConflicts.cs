namespace StudioX.Engine;

/// <summary>按锁定的功能目录分析草稿冲突，不修改分配、不访问文件或硬件。</summary>
public static class Ag32PinPlanConflicts
{
    public static Ag32PinPlanConflict[] Find(IReadOnlyList<Ag32PinAssignment> assignments,
        IReadOnlyList<Ag32PinFunction> functions)
    {
        ArgumentNullException.ThrowIfNull(assignments);
        ArgumentNullException.ThrowIfNull(functions);

        var conflicts = new List<Ag32PinPlanConflict>();
        var entries = assignments.Where(item => item is not null && !string.IsNullOrEmpty(item.Function)).ToArray();
        var known = functions.ToDictionary(function => function.Name, StringComparer.Ordinal);
        foreach (var group in entries.GroupBy(item => item.Function, StringComparer.Ordinal).Where(group => group.Count() > 1))
        {
            var pins = PinNumbers(group);
            conflicts.Add(new("Function", $"功能 {group.Key} 被重复分配到 {PinLabels(pins)}；每个功能只能分配一次。",
                pins, [group.Key]));
        }

        foreach (var group in entries.GroupBy(item => item.PinNumber))
        {
            var names = FunctionNames(group);
            if (names.Length > 1)
            {
                conflicts.Add(new("Pin", $"PIN_{group.Key} 同时分配给 {string.Join("、", names)}；每个物理引脚只能分配一个功能。",
                    [group.Key], names));
            }
        }

        // 复用资源来自厂商目录；相同功能的重复分配已在上面报告，不生成重复的资源提示。
        var shared = entries.Where(item => known.TryGetValue(item.Function, out var function) && function.SharedGpio is not null)
            .GroupBy(item => known[item.Function].SharedGpio!, StringComparer.Ordinal);
        foreach (var group in shared)
        {
            var names = FunctionNames(group);
            if (names.Length > 1)
            {
                var pins = PinNumbers(group);
                conflicts.Add(new("SharedGpio", $"{string.Join("、", names)} 共用内部资源 {group.Key}，分配在 {PinLabels(pins)}，不能同时启用。",
                    pins, names));
            }
        }

        return conflicts.ToArray();
    }

    private static int[] PinNumbers(IEnumerable<Ag32PinAssignment> assignments)
        => assignments.Select(item => item.PinNumber).Distinct().Order().ToArray();

    private static string[] FunctionNames(IEnumerable<Ag32PinAssignment> assignments)
        => assignments.Select(item => item.Function).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    private static string PinLabels(IEnumerable<int> pins) => string.Join("、", pins.Select(pin => $"PIN_{pin}"));
}
