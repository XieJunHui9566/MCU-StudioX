using StudioX.Engine;
using StudioX.Foundation;
using StudioX.Packages;

if (args.Length != 3)
{
    Console.Error.WriteLine("StudioX.Rt51Validation <package-source> <new-output> <tool-runtime>");
    return 2;
}
var source = Path.GetFullPath(args[0]);
var root = Path.GetFullPath(args[1]);
try
{
    if (Directory.Exists(root))
    {
        throw new InvalidOperationException("Choose a new evidence directory.");
    }
    Directory.CreateDirectory(root);
    var checks = new List<string>();
    void Check(bool condition, string name)
    {
        if (!condition)
        {
            throw new InvalidOperationException(name);
        }
        checks.Add(name);
        Console.WriteLine("PASS " + name);
    }
    var archive = Path.Combine(root, "rt51-candidate.mcupack");
    await PackArchiveWriter.WriteAsync(source, archive);
    var pack = await new PackRepository(Path.Combine(root, "repository")).ImportAsync(archive);
    var device = pack.Manifest.Devices.Single(d => d.Id == "IAP15F2K61S2");
    Check(device.Templates.Any(t => t.Id == "bare") && device.Templates.Any(t => t.Id == "rt51"), "bare and RT51 templates coexist");
    Check(pack.Manifest.Devices.Where(d => d.Id != device.Id).All(d => d.Templates.All(t => t.Id != "rt51")), "RT51 template restricted to verified memory layout");
    Check(device.MonitorFirmware?.ImageSha256 == "ABC080915DDDCA9589F184E9BE76FBCC757EDF001BE02AF7A70ECB57451106F9", "existing Mon51 firmware identity preserved");
    var resolved = TemplateResolver.Resolve(device, "rt51");
    var expected = new[] { ("--xram-loc", "1"), ("--xram-size", "1023"), ("--code-size", "56317"), ("--iram-size", "256"), ("--idata-loc", "48"), ("--stack-size", "128") };
    foreach (var (option, value) in expected)
    {
        var index = resolved.LinkOptions.ToList().IndexOf(option);
        Check(index >= 0 && resolved.LinkOptions[index + 1] == value && resolved.LinkOptions.Count(s => s == option) == 1, "paired option " + option + " " + value);
    }
    Check(TemplateResolver.Resolve(device, "bare").LinkOptions.SequenceEqual(device.LinkOptions), "ordinary template preserves original memory limits");
    // 数值相同属于不同开关，不能按独立字符串去重。
    var sameValues = device with
    {
        LinkOptions = ["--iram-size", "256", "--xram-size", "256"]
    };
    var template = sameValues.Templates.Single(t => t.Id == "rt51");
    sameValues = sameValues with
    {
        Templates = [template with { Build = template.Build! with { LinkOptions = ["--stack-size", "256"] } }]
    };
    Check(TemplateResolver.Resolve(sameValues, "rt51").LinkOptions.Count(s => s == "256") == 3, "identical numeric operands retained for three independent options");
    var project = Path.Combine(root, "project");
    await new ProjectService().CreateAsync(pack, device.Id, "rt51", "rt51_demo", project);
    Check(File.Exists(Path.Combine(project, "src", "rt51.c")) && File.Exists(Path.Combine(project, "include", "rt51.h")), "first-party kernel copied as editable project sources");
    Check(File.Exists(Path.Combine(project, "src", "stc15_xinit_sdcc.c")), "IAP15 XINIT startup adapter copied and included in the project");
    Check(File.Exists(Path.Combine(project, "src", "rt51_timers.c")), "static software timer service copied as an editable source");
    var romLimit = await StcCodeRomLimit.ReadAsync(project, await ProjectService.ReadAsync(project));
    Check(romLimit?.MaximumBytes == 56317, "build and download ROM checks adopt the selected template reservation");
    await JsonStore.WriteAsync(Path.Combine(project, StcIspSettings.RelativePath), new StcIspSettings(ClockMode: StcClockMode.InternalRc, ClockFrequencyHz: 12000000));
    var builder = new BuildService(new ToolsetCatalog(Path.Combine(Path.GetFullPath(args[2]), "toolsets")));
    await builder.SaveSettingsAsync(project, new ProjectBuildSettings(DebugInfo: CompilerDebugInfo.Standard));
    var ordinary = await builder.BuildAsync(project, new Progress<string>(Console.WriteLine));
    Check(ordinary.Success, "actual managed SDCC build of RT51 application");
    await builder.SaveSettingsAsync(project, new ProjectBuildSettings(DebugInfo: CompilerDebugInfo.Standard, Mon51Profile: true));
    var monitor = await builder.BuildAsync(project, new Progress<string>(Console.WriteLine));
    Check(monitor.Success, "actual managed SDCC build with Mon51 profile");
    var memory = await File.ReadAllTextAsync(Path.Combine(project, ".build", "firmware.mem"));
    Check(memory.Contains("Stack starts at: 0x80", StringComparison.Ordinal) && memory.Contains("128 bytes available", StringComparison.Ordinal), "actual link places hardware stack at 80..FF");
    Check(System.Text.RegularExpressions.Regex.IsMatch(memory, @"EXTERNAL RAM\s+0x0001\s+0x[0-9a-f]+\s+\d+\s+1023"), "Mon51 retains address-zero reservation and never extends XRAM into 0400");
    Check((await File.ReadAllTextAsync(Path.Combine(project, ".build", "firmware.map"))).Contains("__mcs51_genXINIT", StringComparison.Ordinal), "actual link resolves the XINIT initialization entry");
    var map = await File.ReadAllTextAsync(Path.Combine(project, ".build", "firmware.map"));
    Check(map.Contains("_rt51_delay_until", StringComparison.Ordinal) && map.Contains("_rt51_timer_dispatch", StringComparison.Ordinal), "example links fixed-period delay and task-context timer dispatch");
    await JsonStore.WriteAsync(Path.Combine(root, "result.json"), new
    {
        passed = true,
        hardware = false,
        checks,
        archive,
        memory,
        ordinary.LogPath
    });
    Console.WriteLine($"PASS {checks.Count} package and build checks");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    return 1;
}
