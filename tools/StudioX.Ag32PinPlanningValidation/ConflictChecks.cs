using StudioX.Engine;

/// <summary>用实际功能目录核对草稿全部冲突及解除冲突后的状态，避免只标记后加入的引脚。</summary>
internal static class ConflictChecks
{
    internal static void Run(Ag32PinPlanSnapshot snapshot, Action<bool, string> check)
    {
        Ag32PinAssignment[] duplicateFunction = [new("GPIO4_4", 21), new("GPIO4_4", 2)];
        var conflicts = Ag32PinPlanConflicts.Find(duplicateFunction, snapshot.Functions);
        check(conflicts is [{ Kind: "Function" }] && conflicts[0].PinNumbers.SequenceEqual([2, 21]) &&
            conflicts[0].Functions.SequenceEqual(["GPIO4_4"]) && conflicts[0].Message.Contains("PIN_2", StringComparison.Ordinal) &&
            conflicts[0].Message.Contains("PIN_21", StringComparison.Ordinal),
            "Duplicate GPIO identifies both physical pins and a concrete diagnostic");
        check(duplicateFunction.SequenceEqual(new Ag32PinAssignment[] { new("GPIO4_4", 21), new("GPIO4_4", 2) }),
            "Conflict inspection preserves both draft assignments for correction");

        conflicts = Ag32PinPlanConflicts.Find([new("GPIO4_4", 2), new("GPIO4_5", 2)], snapshot.Functions);
        check(conflicts is [{ Kind: "Pin" }] && conflicts[0].PinNumbers.SequenceEqual([2]) &&
            conflicts[0].Functions.SequenceEqual(["GPIO4_4", "GPIO4_5"]),
            "Physical pin collision identifies both competing functions");

        conflicts = Ag32PinPlanConflicts.Find([new("GPIO7_6", 2), new("UART0_UARTTXD", 21)], snapshot.Functions);
        check(conflicts is [{ Kind: "SharedGpio" }] && conflicts[0].PinNumbers.SequenceEqual([2, 21]) &&
            conflicts[0].Message.Contains("GPIO7_6", StringComparison.Ordinal),
            "Vendor peripheral multiplexing reports every affected pin");

        conflicts = Ag32PinPlanConflicts.Find([new("GPIO4_4", 2), new("GPIO4_4", 2)], snapshot.Functions);
        check(conflicts is [{ Kind: "Function" }] && conflicts[0].PinNumbers.SequenceEqual([2]),
            "Repeated identical declaration remains a conflict without duplicate diagnostics");

        check(Ag32PinPlanConflicts.Find([new("GPIO4_4", 21)], snapshot.Functions).Length == 0 &&
            Ag32PinPlanConflicts.Find([new("GPIO4_4", 21), new("GPIO4_5", 2)], snapshot.Functions).Length == 0,
            "Removing or reassigning a conflicting row clears draft conflicts");
    }
}
