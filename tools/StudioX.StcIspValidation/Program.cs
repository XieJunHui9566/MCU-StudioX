using System.Security.Cryptography;
using System.Text;
using StudioX.Engine;
using StudioX.Foundation;
using StudioX.Packages;

// 全程只创建隔离工程、编译和准备快照；绝不调用 Download/连接 COM 口。
// dotnet run --project tools/StudioX.StcIspValidation -c Release -- <tool-runtime> <stc.mcupack> <new-output-dir> <stcgal.exe>
Console.OutputEncoding = Encoding.UTF8;
if (args.Length != 4) throw new ArgumentException("需要 tool-runtime、STC 包、新输出目录与本机 stcgal.exe。");
var output = Path.GetFullPath(args[2]);
if (Directory.Exists(output) || File.Exists(output)) throw new InvalidOperationException("请选择不存在的验收输出目录。");
Directory.CreateDirectory(output);
var pack = await new PackRepository(Path.Combine(output, "repository")).ImportAsync(Path.GetFullPath(args[1]));
var device = pack.Manifest.Devices.Single(d => d.Id == "IAP15F2K61S2");
var project = Path.Combine(output, "iap15-project");
await new ProjectService().CreateAsync(pack, device.Id, device.Templates[0].Id, "isp_offline", project);
var catalog = new ToolsetCatalog(Path.Combine(Path.GetFullPath(args[0]), "toolsets"));
var service = new StcIspService(catalog, Path.Combine(output, "runtime"), Path.Combine(output, "user-data"));
var bundledData = Path.Combine(output, "bundled-user-data");
var bundled = new StcIspService(catalog, Path.GetFullPath(args[0]), bundledData);
Check((await bundled.GetToolStatusAsync()).Available, "内置 stcgal 运行时识别");
await JsonStore.WriteAsync(Path.Combine(bundledData, "stc-isp-tool.json"), new { ProgrammerExecutable = "Z:\\missing\\stcgal.exe" });
var fallback = await bundled.GetToolStatusAsync();
Check(fallback.Available && fallback.Message.Contains("回退", StringComparison.Ordinal), "过期用户配置回退到内置运行时");
var capabilities = await service.GetCapabilitiesAsync(project);
Check(capabilities.SupportsRcTrim && capabilities.MinRcFrequencyHz == 5000000 && capabilities.MaxRcFrequencyHz == 28000000 &&
    capabilities.SupportedClockModes.Contains(StcClockMode.ExternalCrystal), "IAP15 时钟能力");
await service.SaveProgrammerPathAsync(Path.GetFullPath(args[3]));
Check((await service.GetToolStatusAsync()).Available, "外部 stcgal 1.10 工具识别");
var settings = new StcIspSettings(Port: "COM5", ClockMode: StcClockMode.InternalRc, ClockFrequencyHz: 11059200);
await service.SaveSettingsAsync(project, settings);
try { await service.PrepareAsync(project, settings); throw new Exception("未编译固件被接受。"); }
catch (StudioXException ex) when (ex.Code == "STC_ISP_BUILD") { }
var build = await new BuildService(catalog).BuildAsync(project);
Check(build.Success, "SDCC 实际编译：" + build.Log);
var prepared = await service.PrepareAsync(project, settings);
Check(prepared.ExpectedModel == "IAP15F2K61S2" && prepared.ExpectedCodeBytes == 62464 && prepared.DataBytes > 0 &&
    prepared.HighestAddress < 62457 && prepared.Port == "COM5" &&
    prepared.ImageSha256 == Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(prepared.Image))), "实际包/构建产物离线准备");
Check(prepared.Image != prepared.SourceImage && File.Exists(prepared.GuardScript) && !File.Exists(prepared.LogPath), "固件快照与门禁脚本；未启动下载");
await service.VerifyPreparedAsync(prepared);
var originalSnapshot = await File.ReadAllBytesAsync(prepared.Image);
await File.AppendAllTextAsync(prepared.Image, "\n");
try { await service.VerifyPreparedAsync(prepared); throw new Exception("确认后修改的快照被接受。"); }
catch (StudioXException ex) when (ex.Code == "STC_ISP_PREPARE") { }
await File.WriteAllBytesAsync(prepared.Image, originalSnapshot);
await service.VerifyPreparedAsync(prepared);
await service.SaveSettingsAsync(project, settings with { Port = "COM6" });
try { await service.VerifyPreparedAsync(prepared); throw new Exception("确认后改变的串口被接受。"); }
catch (StudioXException ex) when (ex.Code == "STC_ISP_PREPARE") { }
await service.SaveSettingsAsync(project, settings);
await service.VerifyPreparedAsync(prepared);
Check((await service.LoadSettingsAsync(project)).ClockFrequencyHz == 11059200, "工程时钟持久化精度");
try { await service.PrepareAsync(project, settings with { ClockFrequencyHz = 12000000 }); throw new Exception("未保存频率被接受。"); }
catch (StudioXException ex) when (ex.Code == "STC_ISP_BUILD") { }
try { await service.SaveSettingsAsync(project, settings with { ClockFrequencyHz = 35000000 }); throw new Exception("超范围频率被接受。"); }
catch (StudioXException ex) when (ex.Code == "STC_ISP_CLOCK") { }
var scriptTest = await new ProcessRunner().RunAsync(new(prepared.PythonExecutable,
    [Path.GetFullPath("tools/StudioX.StcIspValidation/guard_offline.py"), prepared.GuardScript, prepared.Image],
    Path.GetFullPath("."), TimeSpan.FromSeconds(15), RemoveEnvironment: ["PYTHONHOME", "PYTHONPATH"]));
Check(scriptTest.Success && scriptTest.StandardOutput.Contains("STUDIOX_GUARD_OFFLINE_OK", StringComparison.Ordinal),
    "型号/容量/时钟门禁离线行为：" + scriptTest.StandardOutput + scriptTest.StandardError);
var all = pack.Manifest.Devices.Select(StcIspCapabilities.For).ToDictionary(c => c.DeviceId);
Check(all["STC89C52RC"].SupportedClockModes.SequenceEqual([StcClockMode.Preserve]) &&
    all["STC12C5A60S2"].SupportedClockModes.Contains(StcClockMode.ExternalCrystal) && !all["STC12C5A60S2"].SupportsRcTrim &&
    !all["STC8G1K08"].SupportedClockModes.Contains(StcClockMode.ExternalCrystal) && all["STC8G1K08"].SupportsRcTrim,
    "24 型号能力矩阵中的 STC89/STC12/STC8G 边界");
await File.WriteAllTextAsync(Path.Combine(output, "result.txt"),
    "PASS: IAP15F2K61S2 actual pack + SDCC build + HEX snapshot + exact-model guard offline checks. No COM access, erase, or write.\n");
Console.WriteLine("PASS STC ISP 离线验收：实际 IAP15 包与构建、快照、精确型号门禁；未连接串口或烧录。");
return;

static void Check(bool value, string message)
{
    if (!value) throw new InvalidOperationException(message);
}
