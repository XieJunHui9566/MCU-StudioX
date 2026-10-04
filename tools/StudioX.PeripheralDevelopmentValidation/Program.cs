using System.Text;
using StudioX.Application.PeripheralDevelopment;
using StudioX.Engine;
using StudioX.Foundation;
using StudioX.Packages;

if (args is ["--diagnostics", var diagnosticsOutput, var diagnosticsRuntime, var nativeProjects])
{
    await PeripheralDiagnosticChecks.RunAsync(Path.GetFullPath(diagnosticsOutput), Path.GetFullPath(diagnosticsRuntime), Path.GetFullPath(nativeProjects));
    return 0;
}
if (args.Length is not (1 or 3)) { Console.Error.WriteLine("Usage: <output> [<runtime> <pack-inputs>]"); return 2; }
var output = Path.GetFullPath(args[0]);
if (Directory.Exists(output) || File.Exists(output)) { throw new IOException("Use a new validation output directory."); }
Directory.CreateDirectory(output);
var checks = new List<string>();
void Check(bool condition, string label) { if (!condition) { throw new InvalidOperationException(label); } checks.Add(label); Console.WriteLine("PASS " + label); }
async Task Reject(Func<Task> operation, string code, string label)
{
    try
    {
        await operation();
        throw new InvalidOperationException("Expected rejection: " + label);
    }
    catch (StudioXException ex) when (ex.Code == code) { Check(true, label); }
}
var fixture = Path.Combine(output, "fixtures");
var projectRoot = Path.Combine(fixture, "project");
var toolsRoot = Path.Combine(fixture, "toolsets");
var sdk = Path.Combine(toolsRoot, "espressif.idf", "5.5.4", "sdk");
var toolRoot = Path.GetDirectoryName(sdk)!;
var settings = new EspressifProjectSettings("esp-idf", "esp32c3", "5.5.4");
var project = new ProjectManifest(1, "fixture", "test.esp", "1.0.0", "hash", "ESP32-C3", "hello_world", "espressif.idf", "5.5.4", "esp-idf", Espressif: settings);
var device = new DeviceDefinition(project.DeviceId, "fixture", "riscv32", 0, 1, 0, 1, project.ToolsetId, project.ToolsetVersion, project.CompilerId,
    [], [], [], [], "link.ld", [], [], [new ProjectTemplate("hello_world", "Hello", "templates/hello", "main.c")], Espressif: new(settings.Framework, settings.Target, settings.SdkVersion));
var pack = new PackManifest(1, project.PackId, project.PackVersion, "fixture", "Espressif", [device]);
async Task Write(string root, string relative, string value) { var path = PathBoundary.Resolve(root, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); await File.WriteAllTextAsync(path, value); }
await JsonStore.WriteAsync(Path.Combine(projectRoot, ".studiox", "project.json"), project);
await JsonStore.WriteAsync(Path.Combine(projectRoot, "device", "manifest.json"), pack);
var metadata = new ToolsetManifest(1, project.ToolsetId, project.ToolsetVersion, "win-x64", project.CompilerId, [], [], ResourceDirectories: new() { ["idf"] = "sdk" }, Purpose: "esp-idf");
await JsonStore.WriteAsync(Path.Combine(toolRoot, "toolset.json"), metadata);
await Write(sdk, "version.txt", "v5.5.4");
const string capsPath = "components/soc/esp32c3/include/soc/soc_caps.h";
const string caps = "#define SOC_GPIO_PIN_COUNT 22\n#define SOC_UART_NUM (2)\n#define SOC_HP_I2C_NUM (1U)\n#define SOC_I2C_SUPPORTED 1\n#define SOC_SPI_PERIPH_NUM 2\n#define SOC_ADC_SUPPORTED 1\n#define SOC_LEDC_SUPPORTED 1\n#define SOC_RMT_SUPPORTED 1\n#define SOC_RMT_MEM_WORDS_PER_CHANNEL 48\n";
await Write(sdk, capsPath, caps);
var components = new Dictionary<string, string> { ["esp_driver_gpio"] = "driver/gpio.h", ["esp_driver_uart"] = "driver/uart.h", ["esp_driver_i2c"] = "driver/i2c_master.h", ["esp_driver_spi"] = "driver/spi_master.h", ["esp_adc"] = "esp_adc/adc_oneshot.h", ["esp_driver_ledc"] = "driver/ledc.h", ["esp_timer"] = "esp_timer.h", ["esp_driver_rmt"] = "driver/rmt_tx.h", ["esp_common"] = "esp_err.h", ["log"] = "esp_log.h" };
const string apiDeclarations = "void gpio_config(void); void uart_driver_install(void); void i2c_new_master_bus(void); void spi_bus_add_device(void); void adc_oneshot_new_unit(void); void ledc_timer_config(void); void esp_timer_create(void); void rmt_new_tx_channel(void);\n";
foreach (var component in components) { await Write(sdk, $"components/{component.Key}/include/{component.Value}", apiDeclarations); await Write(sdk, $"components/{component.Key}/CMakeLists.txt", "# fixture\n"); }
await Write(sdk, "components/esp_driver_spi/include/driver/spi_common.h", "void spi_bus_initialize(void);\n");
var catalog = new ToolsetCatalog(toolsRoot, Path.Combine(fixture, "user-data"));
var service = new PeripheralDevelopmentService(catalog);
var context = await service.ReadAsync(projectRoot);
Check(context.Options.Count == 8 && context.Options.All(o => o.Available), "all eight recipes require matching SDK evidence");
foreach (var option in context.Options)
{
    var values = Values(option);
    var preview = service.Generate(context, option.Id, values);
    Check(preview.Code.Contains("esp_err_t sx_" + values["name"] + "_init(void)", StringComparison.Ordinal) && preview.Dependencies.Contains(option.Component, StringComparison.Ordinal), option.Id + " has initialization and declared dependency");
}
var gpio = context.Options.Single(o => o.Id == "gpio");
var parameters = Values(gpio);
await Reject(() => Task.FromResult(service.Generate(context, "gpio", new Dictionary<string, string> { ["name"] = "test" })), "PERIPHERAL_PARAMETER", "no board GPIO is guessed");
parameters["pin"] = "22";
await Reject(() => Task.FromResult(service.Generate(context, "gpio", parameters)), "PERIPHERAL_PARAMETER", "GPIO beyond target count rejected");
parameters["pin"] = "-1";
await Reject(() => Task.FromResult(service.Generate(context, "gpio", parameters)), "PERIPHERAL_PARAMETER", "negative GPIO rejected before unsigned shift");
parameters["pin"] = "1";
parameters["name"] = "x); injected()";
await Reject(() => Task.FromResult(service.Generate(context, "gpio", parameters)), "PERIPHERAL_PARAMETER", "identifier injection rejected");
parameters = Values(gpio);
parameters["level"] = "2";
await Reject(() => Task.FromResult(service.Generate(context, "gpio", parameters)), "PERIPHERAL_PARAMETER", "invalid output level rejected");
var i2c = context.Options.Single(o => o.Id == "i2c");
var i2cValues = Values(i2c);
i2cValues["scl"] = i2cValues["sda"];
await Reject(() => Task.FromResult(service.Generate(context, "i2c", i2cValues)), "PERIPHERAL_PIN_CONFLICT", "duplicate peripheral pins rejected");
i2cValues = Values(i2c);
i2cValues["port"] = "1";
await Reject(() => Task.FromResult(service.Generate(context, "i2c", i2cValues)), "PERIPHERAL_PARAMETER", "controller count comes from target SDK");
var previewGpio = service.Generate(context, "gpio", Values(gpio));
await PeripheralAdditionChecks.RunAsync(service, context, previewGpio, Check);
Check(!service.PrepareInsertion(previewGpio, "void app_main(void)\r\n{}\r\n").Replace("\r\n", "", StringComparison.Ordinal).Contains('\n'), "insertion preserves CRLF");
await Reject(() => Task.FromResult(service.PrepareInsertion(previewGpio, previewGpio.Code)), "PERIPHERAL_NAME_CONFLICT", "existing generated instance prevents duplicate definitions");
await service.ValidateAsync(context);
Check(true, "unchanged snapshot revalidates");
var uartHeader = Path.Combine(sdk, "components/esp_driver_uart/include/driver/uart.h");
await File.WriteAllTextAsync(uartHeader, "// changed\n");
await Reject(() => service.ValidateAsync(context), "PERIPHERAL_STALE", "SDK header change invalidates preview");
File.Delete(uartHeader);
var missing = await service.ReadAsync(projectRoot);
Check(!missing.Options.Single(o => o.Id == "uart").Available && missing.Options.Single(o => o.Id == "uart").Availability.Contains("driver/uart.h", StringComparison.Ordinal), "missing nested header remains visible with exact path");
await Reject(() => Task.FromResult(service.Generate(missing, "uart", Values(missing.Options.Single(o => o.Id == "uart")))), "PERIPHERAL_UNAVAILABLE", "missing-header recipe cannot generate");
await Write(sdk, "components/esp_driver_uart/include/driver/uart.h", "// incomplete header\n");
var incomplete = await service.ReadAsync(projectRoot);
Check(!incomplete.Options.Single(o => o.Id == "uart").Available && incomplete.Options.Single(o => o.Id == "uart").Availability.Contains("预期 API", StringComparison.Ordinal), "existing but incomplete SDK header is not treated as usable");
await Write(sdk, "components/esp_driver_uart/include/driver/uart.h", apiDeclarations);
await Write(sdk, capsPath, caps.Replace("SOC_RMT_SUPPORTED 1", "SOC_RMT_SUPPORTED 0", StringComparison.Ordinal));
Check(!(await service.ReadAsync(projectRoot)).Options.Single(o => o.Id == "rmt").Available, "target lacking RMT does not expose a usable recipe");
await Write(sdk, capsPath, caps);
await JsonStore.WriteAsync(Path.Combine(projectRoot, ".studiox", "project.json"), project with { Name = "changed" });
await Reject(() => service.ValidateAsync(context), "PERIPHERAL_STALE", "project configuration changes invalidate preview");
await JsonStore.WriteAsync(Path.Combine(projectRoot, ".studiox", "project.json"), project);
await JsonStore.WriteAsync(Path.Combine(projectRoot, "device", "manifest.json"), pack with { Id = "wrong.id" });
await Reject(() => service.ReadAsync(projectRoot), "PERIPHERAL_DEVICE_IDENTITY", "pack identity mismatch rejected");
await JsonStore.WriteAsync(Path.Combine(projectRoot, "device", "manifest.json"), pack);
await Write(sdk, "version.txt", "6.1.0");
await Reject(() => service.ReadAsync(projectRoot), "PERIPHERAL_SDK_IDENTITY", "actual SDK version mismatch rejected");
await Write(sdk, "version.txt", "5.5.4");
await catalog.SetEnabledAsync(project.ToolsetId, project.ToolsetVersion, false);
await Reject(() => service.ReadAsync(projectRoot), "TOOLSET_DISABLED", "disabled component does not silently use another SDK");
await catalog.SetEnabledAsync(project.ToolsetId, project.ToolsetVersion, true);
await JsonStore.WriteAsync(Path.Combine(projectRoot, ".studiox", "project.json"), project with { Espressif = null, ToolsetId = "test.tools", CompilerId = "test.compiler" });
await Reject(() => service.ReadAsync(projectRoot), "PERIPHERAL_FRAMEWORK", "generic project does not receive guessed ESP APIs");
await JsonStore.WriteAsync(Path.Combine(projectRoot, ".studiox", "project.json"), project);
var canceled = new CancellationToken(true);
try { await service.ReadAsync(projectRoot, canceled); throw new Exception("Expected cancellation"); } catch (OperationCanceledException) { Check(true, "canceled catalog read stops"); }
var newerToolRoot = Path.Combine(toolsRoot, "espressif.idf", "6.1.0");
var newerSdk = Path.Combine(newerToolRoot, "sdk");
foreach (var source in Directory.GetFiles(sdk, "*", SearchOption.AllDirectories))
{
    await Write(newerSdk, Path.GetRelativePath(sdk, source).Replace('\\', '/'), await File.ReadAllTextAsync(source));
}
await Write(newerSdk, "version.txt", "6.1.0");
await JsonStore.WriteAsync(Path.Combine(newerToolRoot, "toolset.json"), metadata with { Version = "6.1.0" });
var newerSettings = settings with { SdkVersion = "6.1.0" };
var newerProject = project with { ToolsetVersion = "6.1.0", Espressif = newerSettings };
await JsonStore.WriteAsync(Path.Combine(projectRoot, ".studiox", "project.json"), newerProject);
await JsonStore.WriteAsync(Path.Combine(projectRoot, "device", "manifest.json"), pack with { Devices = [device with { ToolsetVersion = "6.1.0", Espressif = new(newerSettings.Framework, newerSettings.Target, newerSettings.SdkVersion) }] });
var newerContext = await service.ReadAsync(projectRoot);
Check(newerContext.Options.All(o => o.Available && o.DocumentationUrl.Contains("/en/v6.1/esp32c3/", StringComparison.Ordinal)), "SDK 6.1.0 uses its published 6.1 documentation branch");

var nativeResults = new List<object>();
if (args.Length == 3)
{
    var runtime = Path.GetFullPath(args[1]);
    var inputs = Path.GetFullPath(args[2]);
    var nativeCatalog = new ToolsetCatalog(Path.Combine(runtime, "toolsets"));
    var nativeService = new PeripheralDevelopmentService(nativeCatalog);
    var builds = new BuildService(nativeCatalog);
    var repository = new PackRepository(Path.Combine(output, "native-packs"));
    foreach (var version in new[] { "0.1.1", "0.4.0" })
    {
        var installed = await repository.ImportAsync(Path.Combine(inputs, "espressif.esp32c3-" + version + ".mcupack"));
        var nativeProject = Path.Combine(output, "native-projects", "idf-" + version);
        var manifest = await new ProjectService().CreateAsync(installed, "ESP32-C3", "hello-world", "peripheral_check", nativeProject);
        var nativeContext = await nativeService.ReadAsync(nativeProject);
        Check(nativeContext.Options.All(o => o.Available), "native " + manifest.Espressif!.SdkVersion + " SDK contains every recipe header and component");
        var sourceNames = new List<string>();
        var additions = new List<(string Path, PeripheralCodePreview Code)>();
        var main = new StringBuilder("#include \"esp_err.h\"\n");
        foreach (var option in nativeContext.Options)
        {
            var code = nativeService.Generate(nativeContext, option.Id, Values(option));
            var file = option.Id + "_assist.c";
            sourceNames.Add("\"" + file + "\"");
            additions.Add(("main/" + file, code));
            await File.WriteAllTextAsync(Path.Combine(nativeProject, "main", file), "// original source\n");
            main.Append("esp_err_t ").Append(code.InstanceName).Append("_init(void);\n");
        }
        main.Append("void app_main(void)\n{\n");
        foreach (var option in nativeContext.Options)
        {
            main.Append("    ESP_ERROR_CHECK(sx_").Append(option.Id).Append("_init());\n");
        }
        main.Append("}\n");
        await File.WriteAllTextAsync(Path.Combine(nativeProject, "main", "assist_main.c"), main.ToString());
        sourceNames.Add("\"assist_main.c\"");
        await File.WriteAllTextAsync(Path.Combine(nativeProject, "main", "CMakeLists.txt"), "# retain native fixture comment\nidf_component_register(SRCS " + string.Join(' ', sourceNames) + " INCLUDE_DIRS \".\" PRIV_REQUIRES freertos esp_system)\n");
        var nativeFiles = new StudioX.Application.ProjectFileService();
        foreach (var (path, code) in additions)
        {
            var component = await nativeService.ReadComponentAsync(nativeContext, path, []);
            var addition = nativeService.PrepareAddition(nativeContext, component, code);
            await nativeService.ValidateAdditionAsync(addition, []);
            foreach (var change in addition.Changes)
            {
                await nativeFiles.SaveAsync(nativeProject, change.Source, change.After);
            }
        }
        Check((await File.ReadAllTextAsync(Path.Combine(nativeProject, "main", "CMakeLists.txt"))).Contains("PRIV_REQUIRES freertos esp_system", StringComparison.Ordinal),
            "native " + manifest.Espressif.SdkVersion + " automatic dependency additions preserve original dependencies");
        Console.WriteLine("BUILD native ESP-IDF " + manifest.Espressif.SdkVersion);
        var report = await builds.BuildAsync(nativeProject, new ProgressText());
        await File.WriteAllTextAsync(Path.Combine(output, "native-" + manifest.Espressif.SdkVersion + ".log"), report.Log);
        Check(report.Success, "native ESP-IDF " + manifest.Espressif.SdkVersion + " compiles and links all eight assisted peripherals");
        nativeResults.Add(new
        {
            sdkVersion = manifest.Espressif.SdkVersion,
            target = manifest.Espressif.Target,
            report.Success,
            report.ExitCode,
            recipeCount = nativeContext.Options.Count,
            hardware = false
        });
    }
}
await JsonStore.WriteAsync(Path.Combine(output, "result.json"), new { success = true, hardware = false, checks, nativeResults });
return 0;

static Dictionary<string, string> Values(PeripheralOption option)
{
    var values = option.Parameters.ToDictionary(p => p.Id, p => p.DefaultValue, StringComparer.Ordinal);
    values["name"] = option.Id;
    var pin = 0;
    foreach (var parameter in option.Parameters.Where(p => p.Id is "pin" or "tx" or "rx" or "sda" or "scl" or "mosi" or "miso" or "sclk" or "cs"))
    {
        values[parameter.Id] = (pin++).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
    return values;
}
sealed class ProgressText : IProgress<string>
{
    public void Report(string value) => Console.WriteLine(value);
}
