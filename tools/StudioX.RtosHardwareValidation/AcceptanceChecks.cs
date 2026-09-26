namespace StudioX.RtosHardwareValidation;

using StudioX.Engine.Debugging;

/// <summary>只用内存中的结果与日志行验证，不能到达应用服务或硬件入口。</summary>
internal static class AcceptanceChecks
{
    public static void Run()
    {
        var checks = 0;
        void Check(bool condition, string description)
        {
            if (!condition) throw new Exception(description);
            Console.WriteLine("PASS " + description);
            checks++;
        }
        const ulong ramBytes = 64 * 1024;
        var tasks = Enumerable.Range(0, 5).Select(index => new FreeRtosTask(
            (ulong)(0x20000100 + index * 128), "task-" + index, index == 0 ? "Running" : "Blocked",
            (ulong)index, (ulong)index, null, null, null, null, null)).ToArray();
        var good = new FreeRtosSnapshot(true, "fixture", true, 0, 42, tasks[0].Address, (ulong)tasks.Length,
            tasks, new("heap_4", 32 * 1024, 16 * 1024, 8 * 1024, 11, 3, 8 * 1024, 2), [], []);
        var accepted = SnapshotAcceptance.Validate(good, ramBytes);
        Check(accepted.Accepted && accepted.PartialFields.Length > 0 &&
            SnapshotAcceptance.Validate(good with { TickCount = good.TickCount }, ramBytes).Accepted,
            "valid non-fixed task/heap values and unchanged Tick remain accepted with unknown optional fields");
        Check(!SnapshotAcceptance.Validate(good with { ReportedTaskCount = 1552988982, Tasks = [] }, ramBytes).Accepted &&
            !SnapshotAcceptance.Validate(good with { CurrentTaskAddress = 0x20000103 }, ramBytes).Accepted &&
            !SnapshotAcceptance.Validate(good with { SchedulerRunning = null }, ramBytes).Accepted,
            "bogus task count, unaligned current TCB and unreadable scheduler are refused");
        Check(!SnapshotAcceptance.Validate(good with { Heap = good.Heap! with { FreeBytes = 2792760398 } }, ramBytes).Accepted &&
            !SnapshotAcceptance.Validate(good with { Heap = good.Heap! with { MinimumEverFreeBytes = 20000 } }, ramBytes).Accepted &&
            !SnapshotAcceptance.Validate(good with { Heap = good.Heap! with { LargestFreeBlockBytes = 20000 } }, ramBytes).Accepted &&
            !SnapshotAcceptance.Validate(good with { Heap = good.Heap! with { TotalBytes = ramBytes + 1 } }, ramBytes).Accepted,
            "heap values cannot exceed capacity or contradict free/minimum/largest relationships");
        Check(!SnapshotAcceptance.Validate(good with { Heap = good.Heap! with { FreeBlockCount = 0 } }, ramBytes).Accepted &&
            !SnapshotAcceptance.Validate(good with { Heap = good.Heap! with { FreeBlockCount = 1 } }, ramBytes).Accepted &&
            !SnapshotAcceptance.Validate(good with { Tasks = [tasks[0], tasks[0]], ReportedTaskCount = 2 }, ramBytes).Accepted,
            "inconsistent free-block statistics and duplicate task addresses are refused");

        static ImageVerificationEvidence Evidence(params string[] lines)
        {
            var result = new ImageVerificationEvidence();
            foreach (var line in lines) result.AddLogLine(line);
            return result;
        }
        Check(Evidence("[t] GDB < @\"verified 355780 bytes in 3s\\n\"").Accepted &&
            Evidence("[t] OpenOCD < verified 4096 bytes in 1s").Accepted &&
            Evidence("[t] GDB < @\"checksum mismatch - attempting binary compare\\n\"", "[t] GDB < @\"verified 4096 bytes\\n\"").Accepted,
            "positive byte verification is accepted, including successful CRC fallback without actual diff");
        Check(!Evidence("[t] GDB < @\"checksum mismatch - attempting binary compare\\n\"",
            "[t] GDB < @\"diff 127 address 0x08000080. Was 0xff instead of 0x00\\n\"", "[t] GDB < 12^done").Accepted,
            "WCH mismatch with MI done cannot pass image verification");
        Check(!Evidence("[t] GDB < @\"verified 4096 bytes\\n\"", "[t] OpenOCD < diff 0 address 0x08000000").Accepted &&
            !Evidence("[t] GDB < 12^done").Accepted &&
            !Evidence("[t] OpenOCD < verified 4096 bytes", "[t] OpenOCD < Error: error reading USB data").Accepted,
            "actual diff or USB failure overrides positive output, while done without verified bytes is refused");
        Check(!Evidence("[t] GDB > 1-interpreter-exec console \"monitor echo verified 4096 bytes\"").Accepted &&
            !Evidence("[t] GDB < @\"verified 0 bytes\\n\"").Accepted,
            "command text and zero-byte verification cannot fabricate matching evidence");
        Console.WriteLine($"PASS {checks} acceptance checks; pure fixtures only, no hardware.");
    }
}
