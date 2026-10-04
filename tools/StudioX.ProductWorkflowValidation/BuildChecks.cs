namespace StudioX.ProductWorkflowValidation;

using StudioX.Application;
using StudioX.Engine;
using StudioX.Foundation;

internal static class BuildChecks
{
    public static void Run(Action<bool, string> check)
    {
        const string map = "Discarded input sections\n .text.dead 0x00000000 0x100 dead.o\nLinker script and memory map\n .text.main 0x08000000 0x20 main.o\n .text.wrapped\n                0x08000020 0x10 lib.a(helper.o)\n .bss.buffer 0x20000000 0x40 main.o\n .debug_info 0x00000000 0x200 main.o\n";
        var rows = BuildDetailAnalyzer.ParseMap(map);
        check(rows.Count == 3 && !rows.Any(r => r.Name == "dead") && rows.Single(r => r.Name == "main").Bytes == 32, "MAP contribution analysis excludes discarded/debug sections and reads wrapped symbol lines");
        var timings = BuildDetailAnalyzer.ParseNinjaLog("# ninja log v5\n0\t12\t1\tmain.o\tabcd\n0\t15\t1\tmain.o\tabcd\n12\t18\t1\tfirmware.elf\tabcd\n5\t4\t1\tbad.o\tabcd\n");
        check(timings.Count == 1 && timings[0].Milliseconds == 15, "Ninja compile timing uses latest object record and excludes links/invalid intervals");
        var project = new ProjectManifest(1, "fixture", "", "", "", "STM32F407ZGT6", "", "arm.gnu", "1.0.0", "arm-gnu-15.2.rel1");
        var before = new BuildHistorySnapshot("before", DateTimeOffset.UtcNow, new(project, "before", "before", rows, timings), new([new("firmware", "Debug", DateTime.UtcNow, [new("FLASH", 0x08000000, 1024, 32)])], "fixture"), 1.2);
        var after = before with
        {
            Id = "after",
            Details = before.Details with
            {
                Contributions = rows.Select(r => r.Name == "main" ? r with { Bytes = 48 } : r).ToArray()
            },
            Memory = new([new("firmware", "Debug", DateTime.UtcNow, [new("FLASH", 0x08000000, 1024, 48)])], "fixture")
        };
        var comparison = BuildHistoryService.Compare(before, after);
        check(comparison.Any(r => r.Category == "存储区/字节" && r.Difference == 16) && comparison.Any(r => r.Category == "符号/字节" && r.Difference == 16), "comparison relates region growth to file and symbol changes");
        try
        {
            _ = BuildHistoryService.Compare(before, after with
            {
                Details = after.Details with
                {
                    Project = project with
                    {
                        DeviceId = "other"
                    }
                }
            });
            check(false, "incompatible compare");
        }
        catch (StudioXException error) { check(error.Code == "BUILD_COMPARE_TARGET", "cross-device build comparison rejected"); }
    }
}
