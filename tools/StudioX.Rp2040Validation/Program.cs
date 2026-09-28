using System.Buffers.Binary;
using StudioX.Application.CodeIntelligence;
using StudioX.Engine;
using StudioX.Engine.Debugging;
using StudioX.Foundation;
using StudioX.Packages;

// 只在新目录导入包并编译；不启动 OpenOCD，不连接设备或写入固件。
if (args is not [var archive, var runtime, var output])
{
    Console.Error.WriteLine("Usage: <RP2040.mcupack> <runtime> <new-output-directory>");
    return 2;
}
var root = Path.GetFullPath(output);
if (Directory.Exists(root))
{
    throw new InvalidOperationException("Use a new validation directory.");
}
var repository = new PackRepository(Path.Combine(root, "packs"));
var pack = await repository.ImportAsync(Path.GetFullPath(archive));
var device = pack.Manifest.Devices.Single();
Check(pack.Manifest.Id == "raspberrypi.rp2040" && device.Id == "RP2040-PICO", "独立 RP2040 包和板级身份");
Check(device.FlashOrigin == 0x10000000 && device.FlashBytes == 2 * 1024 * 1024 &&
    device.RamOrigin == 0x20000000 && device.RamBytes == 264 * 1024, "Pico 外部 Flash 和 SRAM 容量");
Check(device.CpuFlags.SequenceEqual(new[] { "-mcpu=cortex-m0plus", "-mthumb" }) &&
    device.Defines.Contains("PICO_RP2040=1"), "Cortex-M0+ 参数");
Check(device.OpenOcd is null && DebugTargetProfile.Find(device) is null, "未开放未验收硬件入口");
Check(device.Templates.Select(template => template.Id).SequenceEqual(new[] { "minimal", "blink", "multicore" }) &&
    device.Templates.All(template => template.EntryFile.EndsWith(".c", StringComparison.Ordinal)), "三个 C 模板");
Check(!Directory.EnumerateFiles(pack.RootDirectory, "*", SearchOption.AllDirectories).Any(path =>
    path.Contains("micropython", StringComparison.OrdinalIgnoreCase)), "未混入 MicroPython");
Check(File.Exists(Path.Combine(pack.RootDirectory, "sdk/LICENSE.TXT")) &&
    File.Exists(Path.Combine(pack.RootDirectory, "vendor/provenance.json")), "保留 SDK 许可与来源");

var catalog = new ToolsetCatalog(Path.Combine(Path.GetFullPath(runtime), "toolsets"));
var builds = new BuildService(catalog);
var downloads = new OpenOcdService(catalog);
var tools = await catalog.ResolveAsync(device.ToolsetId, device.ToolsetVersion, device.CompilerId);
var passed = new List<string>();
foreach (var template in device.Templates)
{
    var project = Path.Combine(root, "中文 Pico 工程", template.Id);
    await new ProjectService().CreateAsync(pack, device.Id, template.Id, "RP2040_" + template.Id, project);
    var report = await builds.BuildAsync(project);
    Check(report.Success, report.Log);
    Check(await downloads.ConfigurationAsync(project) is null, "工程未继承 RP2350 下载配置");
    var image = await File.ReadAllBytesAsync(Path.Combine(project, ".build/firmware.bin"));
    Check(image.Length > 264 && image.Length <= device.FlashBytes, "固件位于 2 MiB Flash 范围内");
    // RP2040 ROM 要求最前面的 boot2 为 256 字节，最后四字节是非反射 CRC-32。
    uint crc = 0xffffffff;
    foreach (var value in image.AsSpan(0, 252))
    {
        crc ^= (uint)value << 24;
        for (var bit = 0; bit < 8; bit++)
        {
            crc = (crc << 1) ^ ((crc & 0x80000000) != 0 ? 0x04c11db7u : 0u);
        }
    }
    Check(BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(252)) == crc, "boot2 CRC 正确");
    var stack = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(256));
    var reset = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(260));
    Check(stack == 0x20042000 && (reset & 1) == 1 &&
        (reset & ~1u) >= 0x10000100 && (reset & ~1u) < 0x10000000 + image.Length, "向量表、栈顶与 Thumb 复位入口");
    var memory = await new BuildMemoryService().ReadAsync(project);
    var target = memory.Targets.Single();
    Check(target.Diagnostic is null &&
        target.Regions.Single(region => region.Origin == 0x10000000).Capacity == 2 * 1024 * 1024 &&
        target.Regions.Where(region => region.Origin >= 0x20000000 && region.Origin < 0x20042000)
            .Sum(region => (long)region.Capacity) == 264 * 1024, "实际 ELF/MAP 内存布局");
    await JsonStore.WriteAsync(Path.Combine(project, "memory.json"), memory);
    foreach (var extension in new[] { "elf", "hex", "map" })
    {
        Check(File.Exists(Path.Combine(project, ".build/firmware." + extension)), "生成 " + extension);
    }
    var attributes = await new ProcessRunner().RunAsync(new(tools.Tool("readelf"),
        ["-A", Path.Combine(project, ".build/firmware.elf")], project, TimeSpan.FromSeconds(10),
        ToolsetEnvironment.Create(tools), RemoveEnvironment: ToolsetEnvironment.AmbientVariables));
    await File.WriteAllTextAsync(Path.Combine(project, "elf-attributes.log"), attributes.StandardOutput + attributes.StandardError);
    Check(attributes.Success && attributes.StandardOutput.Contains("v6S-M", StringComparison.Ordinal) &&
        !attributes.StandardOutput.Contains("VFP registers", StringComparison.Ordinal), "实际 Cortex-M0+ ABI，无硬浮点调用");
    Pass(template.Id + $"：真实编译 {image.Length} B、boot2 CRC、向量表、内存、ABI、中文空格路径");

    if (template.Id == "minimal")
    {
        var original = await File.ReadAllTextAsync(Path.Combine(project, "src/main.c"));
        await File.WriteAllTextAsync(Path.Combine(project, "src/main.c"), """
            #include "pico/stdlib.h"
            #include "pico/rand.h"
            #include "pico/unique_id.h"
            #include "hardware/clocks.h"
            #include "hardware/adc.h"
            #include "hardware/dma.h"
            #include "hardware/i2c.h"
            #include "hardware/spi.h"
            #include "hardware/pio.h"
            #include "hardware/pwm.h"
            #include "hardware/rtc.h"
            _Static_assert(PICO_RP2040 == 1, "RP2040");
            _Static_assert(PICO_FLASH_SIZE_BYTES == 2097152, "Pico flash");
            _Static_assert(XOSC_HZ == 12000000, "Pico crystal");
            _Static_assert(SYS_CLK_HZ == 125000000, "SDK system clock");
            _Static_assert(PICO_DEFAULT_LED_PIN == 25, "Pico LED");
            int main(void) {
                pico_unique_board_id_t id;
                pico_get_unique_board_id(&id);
                adc_init(); rtc_init();
                i2c_init(i2c0, 100000); spi_init(spi0, 1000000);
                pio_sm_set_enabled(pio0, 0, false); pwm_set_enabled(0, false);
                dma_channel_abort(0);
                return (int)(get_rand_32() ^ clock_get_hz(clk_sys) ^ id.id[0]);
            }
            """);
        await builds.SaveSettingsAsync(project, new(Optimization: CompilerOptimization.O2, DebugInfo: CompilerDebugInfo.Full));
        var apiBuild = await builds.BuildAsync(project);
        Check(apiBuild.Success, apiBuild.Log);
        await File.WriteAllTextAsync(Path.Combine(project, "src/main.c"), original);
        await builds.SaveSettingsAsync(project, new());
        Check((await builds.BuildAsync(project)).Success, "恢复模板默认编译设置");
        Pass("SDK 时钟/Flash 静态断言、外设 API 链接、O2 覆盖与恢复");

        await using var intelligence = new CodeIntelligenceService(Path.GetFullPath(runtime), Path.Combine(root, "language-cache"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await intelligence.StartAsync(project, timeout.Token);
        var text = "#include \"pico/stdlib.h\"\nvoid check(void) { gpio_set_d\n}\n";
        var completions = await intelligence.CompleteAsync("src/main.c", text,
            text.IndexOf("gpio_set_d", StringComparison.Ordinal) + "gpio_set_d".Length, timeout.Token);
        Check(completions.Any(item => item.InsertText.Contains("gpio_set_dir", StringComparison.Ordinal)), "SDK GPIO 补全");
        text = "#include \"pico/stdlib.h\"\nvoid check(void) { sleep_ms(10); }\n";
        var position = text.IndexOf("sleep_ms", StringComparison.Ordinal) + 2;
        Check((await intelligence.NavigateAsync("src/main.c", text, position, true, timeout.Token)).Count > 0 &&
            await intelligence.HoverAsync("src/main.c", text, position, timeout.Token) is not null, "SDK 声明跳转与悬停");
        Pass("clangd GPIO 补全、SDK 声明跳转与悬停");
    }
}
await File.WriteAllLinesAsync(Path.Combine(root, "result.txt"), passed.Prepend("PASS — RP2040 offline C SDK validation; no hardware access."));
return 0;

void Pass(string message)
{
    passed.Add(message);
    Console.WriteLine("PASS " + message);
}

static void Check(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
