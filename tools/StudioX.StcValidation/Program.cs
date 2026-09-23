using System.Text;
using System.Text.Json;
using StudioX.Engine;
using StudioX.Foundation;
using StudioX.Packages;

// 离线验收：所有明确收录的 STC 型号都实际创建工程并使用发行工具集编译。
// dotnet run --project tools/StudioX.StcValidation -c Release -- <tool-runtime> <pack.mcupack> <new-output-directory> [device-id]
Console.OutputEncoding = Encoding.UTF8;
if (args.Length is not (3 or 4))
{
    Console.Error.WriteLine("用法：StudioX.StcValidation <tool-runtime> <STC8.mcupack> <新输出目录> [器件型号]");
    return 2;
}
var output = Path.GetFullPath(args[2]);
if (Directory.Exists(output) || File.Exists(output)) throw new InvalidOperationException("验收输出目录必须尚不存在。");
Directory.CreateDirectory(output);
var pack = await new PackRepository(Path.Combine(output, "repository")).ImportAsync(Path.GetFullPath(args[1]));
if (pack.Manifest.Id != "stc.stc8") throw new InvalidOperationException("传入的不是 STC8 单包。");
var selectedDevices = args.Length == 4
    ? pack.Manifest.Devices.Where(device => device.Id.Equals(args[3], StringComparison.OrdinalIgnoreCase)).ToArray()
    : pack.Manifest.Devices.ToArray();
if (selectedDevices.Length == 0) throw new InvalidOperationException("包内没有请求验证的器件型号。");
var toolsets = new ToolsetCatalog(Path.Combine(Path.GetFullPath(args[0]), "toolsets"));
var builder = new BuildService(toolsets);
var downloader = new OpenOcdService(toolsets);
var rows = new List<object>();
var errors = 0;
foreach (var device in selectedDevices)
{
    var path = Path.Combine(output, "projects", device.Id);
    try
    {
        if (device.Architecture != "mcs51" || device.ToolsetId != "stc.sdcc" || device.OpenOcd is not null)
            throw new InvalidOperationException("器件架构、工具链或调试状态不符。 ");
        var manifest = await new ProjectService().CreateAsync(pack, device.Id, device.Templates[0].Id, "stc_check", path);
        if (manifest.CompilerId != "sdcc-4.5.0-15242" || await downloader.ConfigurationAsync(path) is not null)
            throw new InvalidOperationException("工具集或下载入口不符。");
        if (!File.ReadAllText(Path.Combine(path, "CMakeLists.txt")).Contains("project(firmware LANGUAGES C)", StringComparison.Ordinal))
            throw new InvalidOperationException("工程未配置为 SDCC C-only 构建。");
        var levels = device.Id == "IAP15F2K61S2"
            ? new[] { CompilerOptimization.ProjectDefault, CompilerOptimization.O0, CompilerOptimization.Os, CompilerOptimization.O2 }
            : new[] { CompilerOptimization.ProjectDefault };
        foreach (var level in levels)
        {
            await builder.SaveSettingsAsync(path, new ProjectBuildSettings(Optimization: level));
            var report = await builder.BuildAsync(path);
            if (!report.Success) throw new InvalidOperationException("实际编译失败：" + report.LogPath + "\n" + report.Log);
            var build = Path.Combine(path, ".build");
            var ihx = Path.Combine(build, "firmware.ihx");
            var hex = Path.Combine(build, "firmware.hex");
            if (!File.Exists(ihx) || !File.Exists(hex) || !File.Exists(Path.Combine(build, "firmware.map")) ||
                !File.Exists(Path.Combine(build, "firmware.mem")))
                throw new InvalidOperationException("缺少 SDCC IHX/HEX/MAP/MEM 产物。");
            if (!File.ReadAllBytes(ihx).AsSpan().SequenceEqual(File.ReadAllBytes(hex)))
                throw new InvalidOperationException("HEX 副本与 SDCC IHX 不一致。");
            var highest = ValidateHex(await File.ReadAllLinesAsync(ihx), checked(device.FlashBytes - 7));
            var memory = await new BuildMemoryService().ReadAsync(path);
            if (memory.Targets.Count != 1 || !memory.Targets[0].Regions.Any(region => region.Name == "程序 Flash" && region.Used > 0))
                throw new InvalidOperationException("SDCC .mem 未呈现程序 Flash 占用。");
            rows.Add(new { device = device.Id, level = level.ToString(), status = "PASS", highestCodeAddress = highest,
                flashBytes = device.FlashBytes, ramBytes = device.RamBytes });
            Console.WriteLine($"PASS {device.Id} {level}：最高程序地址 0x{highest:X4}");
        }
        if (device.Id == "IAP15F2K61S2")
        {
            var limit = await builder.ReadStcCodeRomLimitAsync(path)
                ?? throw new InvalidOperationException("STC 工程未读取到器件包代码容量。");
            if (limit.PhysicalBytes != 61 * 1024 || limit.MaximumBytes != 61 * 1024 - 7 || limit.ReservedBytes != 7)
                throw new InvalidOperationException("IAP15F2K61S2 保留区计算有误。");
            try
            {
                await builder.SaveSettingsAsync(path, new ProjectBuildSettings(CodeRomSizeBytes: limit.MaximumBytes + 1));
                throw new InvalidOperationException("超出器件有效容量的设置被接受。");
            }
            catch (StudioXException ex) when (ex.Code == "BUILD_SETTINGS") { }
            await builder.SaveSettingsAsync(path, new ProjectBuildSettings(CodeRomSizeBytes: 8192));
            var limited = await builder.BuildAsync(path);
            if (!limited.Success) throw new InvalidOperationException("8 KiB 代码上限实际编译失败：" + limited.Log);
            var limitedMemory = await new BuildMemoryService().ReadAsync(path);
            if (limitedMemory.Targets.Single().Regions.Single(region => region.Name == "程序 Flash").Capacity != 8192)
                throw new InvalidOperationException("SDCC 实际链接容量未设为 8 KiB。");
            rows.Add(new { device = device.Id, level = "CodeRom8192", status = "PASS", flashBytes = device.FlashBytes,
                effectiveCodeBytes = limit.MaximumBytes, reservedBytes = limit.ReservedBytes });
            Console.WriteLine("PASS IAP15F2K61S2 代码 ROM 上限 8192 字节；超过有效容量已拒绝");

            var receiptPath = Path.Combine(path, ".build", "studiox-build-receipt.json");
            using var beforeClock = JsonDocument.Parse(await File.ReadAllTextAsync(receiptPath));
            var earlierStamp = beforeClock.RootElement.GetProperty("sourceStamp").GetString();
            await JsonStore.WriteAsync(Path.Combine(path, StcIspSettings.RelativePath),
                new StcIspSettings(Port: "COM5", TransferBaud: 19200));
            var serialOnlyBuild = await builder.BuildAsync(path);
            if (!serialOnlyBuild.Success) throw new InvalidOperationException("仅串口设置变化后的构建失败：" + serialOnlyBuild.Log);
            using var afterSerialOnly = JsonDocument.Parse(await File.ReadAllTextAsync(receiptPath));
            if (afterSerialOnly.RootElement.GetProperty("sourceStamp").GetString() != earlierStamp)
                throw new InvalidOperationException("仅串口号/速度变化不应改变固件源码戳。");
            rows.Add(new { device = device.Id, level = "SerialOnly", status = "PASS" });
            Console.WriteLine("PASS IAP15F2K61S2 仅串口号/速度变化不使固件失效");
            await JsonStore.WriteAsync(Path.Combine(path, StcIspSettings.RelativePath),
                new StcIspSettings(ClockMode: StcClockMode.InternalRc, ClockFrequencyHz: 11059200));
            var clockBuild = await builder.BuildAsync(path);
            if (!clockBuild.Success) throw new InvalidOperationException("显式 11.0592 MHz RC 编译失败：" + clockBuild.Log);
            if (!File.ReadAllText(Path.Combine(path, ".build", "compile_commands.json"))
                .Contains("-DSTUDIOX_CLOCK_HZ=11059200UL", StringComparison.Ordinal))
                throw new InvalidOperationException("编译命令缺少 STUDIOX_CLOCK_HZ 时钟宏。");
            using var afterClock = JsonDocument.Parse(await File.ReadAllTextAsync(receiptPath));
            if (afterClock.RootElement.GetProperty("sourceStamp").GetString() == earlierStamp)
                throw new InvalidOperationException("时钟配置变化未失效旧构建凭据。");
            rows.Add(new { device = device.Id, level = "Clock11059200Hz", status = "PASS", clockHz = 11059200 });
            Console.WriteLine("PASS IAP15F2K61S2 11.0592 MHz 编译时钟宏；旧构建凭据已失效");
        }
    }
    catch (Exception ex)
    {
        errors++;
        rows.Add(new { device = device.Id, level = "", status = "FAIL", error = ex.ToString() });
        Console.Error.WriteLine($"FAIL {device.Id}：{ex.Message}");
    }
    await File.WriteAllTextAsync(Path.Combine(output, "matrix.json"), JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
}
Console.WriteLine($"STC8 离线验收：{selectedDevices.Length} 个型号，{rows.Count - errors} 次实际构建，失败 {errors}。未连接硬件。");
return errors == 0 ? 0 : 1;

static uint ValidateHex(string[] lines, uint usableFlashBytes)
{
    uint offset = 0, highest = 0;
    var codeRecords = 0;
    var ended = false;
    foreach (var line in lines.Where(line => line.Length > 0))
    {
        if (ended || line[0] != ':') throw new InvalidOperationException("Intel HEX 格式无效。");
        var record = Convert.FromHexString(line[1..].Trim());
        if (record.Length < 5 || record.Length != record[0] + 5 || record.Aggregate(0, (sum, value) => sum + value) % 256 != 0)
            throw new InvalidOperationException("Intel HEX 长度或校验和无效。");
        var address = (uint)(record[1] << 8 | record[2]);
        if (record[3] == 0)
        {
            var end = checked(offset + address + record[0]);
            if (end > usableFlashBytes) throw new InvalidOperationException("程序数据进入 Flash 保留区或超出容量。");
            highest = Math.Max(highest, end - 1);
            codeRecords++;
        }
        else if (record[3] == 1) ended = true;
        else if (record[3] == 4 && record[0] == 2) offset = (uint)(record[4] << 8 | record[5]) << 16;
        else throw new InvalidOperationException("不支持的 Intel HEX 记录类型。");
    }
    if (!ended || codeRecords == 0) throw new InvalidOperationException("Intel HEX 没有程序数据或 EOF。");
    return highest;
}
