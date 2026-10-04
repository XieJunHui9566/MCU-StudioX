using System.Security.Cryptography;
using System.Text.Json;
using StudioX.Engine;
using StudioX.Foundation;

if (args is not [var runtimeArgument, var projectsArgument, var outputArgument, var groupsArgument])
{
    Console.Error.WriteLine("Usage: StudioX.EspressifModuleValidation <runtime> <official-projects> <output> <offline,s3,c5,p4,classic,legacy,focused,config>");
    return 2;
}
var runtime = Path.GetFullPath(runtimeArgument);
var projects = Path.GetFullPath(projectsArgument);
var output = Path.GetFullPath(outputArgument);
Directory.CreateDirectory(output);
var catalog = new ToolsetCatalog(Path.Combine(runtime, "toolsets"));
var builds = new BuildService(catalog);
var assertions = new List<string>();
foreach (var group in groupsArgument.Split(','))
{
    Console.WriteLine("GROUP " + group);
    switch (group)
    {
        case "offline":
            await OfflineAsync();
            break;
        case "s3":
            await S3Async();
            break;
        case "c5":
            await C5Async();
            break;
        case "p4":
            await P4Async();
            break;
        case "classic":
            await ClassicAsync();
            break;
        case "legacy":
            await LegacyAsync();
            break;
        case "focused":
            await FocusedAsync();
            break;
        case "config":
            ConfigValues();
            break;
        default:
            throw new ArgumentException("Unknown validation group: " + group);
    }
    await File.WriteAllTextAsync(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new
    {
        count = assertions.Count,
        assertions
    }, JsonStore.Options));
}
Console.WriteLine("PASS " + assertions.Count + " module assertions; no device connection.");
return 0;

void ConfigValues()
{
    var parser = typeof(BuildService).Assembly.GetType("StudioX.Engine.EspressifModuleSdkConfig")!
        .GetMethod("ReadValues", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
    Dictionary<string, string> Parse(params string[] lines) => (Dictionary<string, string>)parser.Invoke(null, [lines])!;
    var values = Parse("CONFIG_ESP32_WIFI_AMPDU_RX_ENABLED=y", "CONFIG_ESP32_WIFI_AMPDU_RX_ENABLED=y",
        "CONFIG_ESP32_WIFI_DYNAMIC_RX_BUFFER_NUM=32", "CONFIG_ESP32_WIFI_DYNAMIC_RX_BUFFER_NUM=32",
        "CONFIG_ESPTOOLPY_FLASHSIZE=\"4MB\"", "CONFIG_ESPTOOLPY_FLASHSIZE=\"4MB\"", "# CONFIG_DISABLED is not set");
    Check(values.Count == 3 && values["CONFIG_ESP32_WIFI_AMPDU_RX_ENABLED"] == "y" &&
        values["CONFIG_ESP32_WIFI_DYNAMIC_RX_BUFFER_NUM"] == "32" && values["CONFIG_ESPTOOLPY_FLASHSIZE"] == "4MB",
        "Identical IDF deprecated aliases accepted without losing values");
    foreach (var pair in new[] { new[] { "CONFIG_FLASH=y", "CONFIG_FLASH=n" }, new[] { "CONFIG_SIZE=4", "CONFIG_SIZE=8" },
        new[] { "CONFIG_MODE=\"dio\"", "CONFIG_MODE=\"qio\"" } })
    {
        try
        {
            Parse(pair);
            throw new InvalidOperationException("Conflicting configuration accepted");
        }
        catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is StudioXException error &&
            error.Message.Contains("冲突的重复项", StringComparison.Ordinal))
        {
            Check(true, "Conflicting sdkconfig values rejected: " + pair[0]);
        }
    }
}

async Task OfflineAsync()
{
    var all = new List<EspressifModuleProfile>();
    foreach (var target in new[] { "esp32", "esp32s3", "esp32p4", "esp32c3", "esp32c5", "esp32c6", "esp8266" })
    {
        var root = CopyProject(target, "offline-" + target);
        var original = await InputHashesAsync(root);
        Check(await builds.LoadEspressifModuleSettingsAsync(root) == new EspressifModuleSettings(), target + " defaults to native SDK");
        var capabilities = await builds.ReadEspressifModuleCapabilitiesAsync(root);
        Check(capabilities.FlashSizesMb.Max() == (target switch
        {
            "esp32s3" => 128,
            "esp32p4" => 64,
            "esp32c5" => 32,
            _ => 16
        }), target + " flash choices respect verified SoC limits");
        var profiles = await builds.ListEspressifModuleProfilesAsync(root);
        Check(profiles.Count > 0 && capabilities.Target == target, target + " exposes target-specific capabilities and official profiles");
        foreach (var profile in profiles)
        {
            Check(profile.Target == target && profile.Id == profile.Settings.ProfileId && Uri.TryCreate(profile.SourceUrl, UriKind.Absolute, out var source) &&
                source.Scheme == "https" && source.Host.EndsWith("espressif.com", StringComparison.Ordinal), profile.Id + " has exact identity and official source");
            await builds.SaveEspressifModuleSettingsAsync(root, profile.Settings);
            Check(await builds.LoadEspressifModuleSettingsAsync(root) == profile.Settings, profile.Id + " settings round-trip");
            all.Add(profile);
        }
        await builds.SaveEspressifModuleSettingsAsync(root, new());
        Check(await InputHashesAsync(root) == original, target + " sidecar saves preserve original native files");
    }
    Check(all.Select(profile => profile.Id).Distinct(StringComparer.Ordinal).Count() == all.Count, "Official profile IDs are globally unique");
    var s3 = CopyProject("esp32s3", "offline-rejections");
    var preset = Profile("esp32s3", "ESP32-S3-WROOM-1-N8R8").Settings;
    await RejectAsync(() => builds.SaveEspressifModuleSettingsAsync(s3, preset with { FlashSizeMb = 4 }), "ESP_MODULE_SETTINGS", "Preset capacity tampering rejected");
    await RejectAsync(() => builds.SaveEspressifModuleSettingsAsync(s3, Profile("esp32c3", "ESP32-C3-WROOM-02-N4").Settings), "ESP_MODULE_SETTINGS", "Cross-target profile rejected");
    await RejectAsync(() => builds.SaveEspressifModuleSettingsAsync(s3, new(SingleCore: true)), "ESP_MODULE_SETTINGS", "Unsupported core selection rejected");
    await RejectAsync(() => builds.SaveEspressifModuleSettingsAsync(s3, new(PsramMode: "quad", PsramSizeMb: 64)), "ESP_MODULE_SETTINGS", "Quad PSRAM density outside SDK driver rejected");
    var classic = CopyProject("esp32", "offline-capacity");
    await RejectAsync(() => builds.SaveEspressifModuleSettingsAsync(classic, new(FlashSizeMb: 16)), "ESP_MODULE_SETTINGS", "Unverified custom expansion cannot bypass WROOM32 4 MiB");
    await builds.SaveEspressifModuleSettingsAsync(classic, Profile("esp32", "ESP32-WROOM-32E-N16").Settings);
    Check((await builds.LoadEspressifModuleSettingsAsync(classic)).FlashSizeMb == 16, "Official accurate module can override generic pack capacity");
    await RejectAsync(() => builds.SaveEspressifModuleSettingsAsync(classic, new(FlashFrequencyMhz: 20, PsramMode: "quad")), "ESP_MODULE_SETTINGS", "Classic unsupported Flash/PSRAM speed combination rejected");
    var receipt = PathBoundary.Resolve(s3, ".build/studiox-build-receipt.json");
    Directory.CreateDirectory(Path.GetDirectoryName(receipt)!);
    await File.WriteAllTextAsync(receipt, "{}");
    await builds.SaveEspressifModuleSettingsAsync(s3, preset);
    Check(!File.Exists(receipt), "Changing module settings invalidates the old receipt");
}

async Task S3Async()
{
    var root = CopyProject("esp32s3", "s3-n8r8-n16r8");
    var original = await InputHashesAsync(root);
    foreach (var label in new[] { "ESP32-S3-WROOM-1-N8R8", "ESP32-S3-WROOM-1-N16R8" })
    {
        var settings = Profile("esp32s3", label).Settings;
        await builds.SaveEspressifModuleSettingsAsync(root, settings);
        await BuildAsync(root, label);
        var preview = await new EspressifFlashService(catalog).PreviewAsync(root, new(Port: "COM999"));
        Check(preview.Layout.FlashSize == settings.FlashSizeMb + "MB", label + " complete layout uses selected capacity");
        Check(await InputHashesAsync(root) == original, label + " build preserves existing user sdkconfig/defaults/CMake");
    }
    var config = PathBoundary.Resolve(root, ".build/studiox-module-sdkconfig");
    var actual = await File.ReadAllTextAsync(config);
    await File.AppendAllTextAsync(config, "\n# deliberate external module-config edit\n");
    await RejectAsync(() => new EspressifFlashService(catalog).PreviewAsync(root, new(Port: "COM999")), "ESP_FLASH_BUILD", "External actual config edit invalidates old images");
    await File.WriteAllTextAsync(config, actual);
    var sidecar = PathBoundary.Resolve(root, EspressifModuleSettings.RelativePath);
    var saved = await File.ReadAllTextAsync(sidecar);
    await JsonStore.WriteAsync(sidecar, new EspressifModuleSettings());
    await RejectAsync(() => new EspressifFlashService(catalog).PreviewAsync(root, new(Port: "COM999")), "ESP_FLASH_BUILD", "External module JSON edit invalidates old images");
    await File.WriteAllTextAsync(sidecar, saved);
    _ = await new EspressifFlashService(catalog).PreviewAsync(root, new(Port: "COM999"));
    Check(true, "Stable final N16R8 receipt remains valid without hardware access");
    var disabled = CopyProject("esp32s3", "s3-disabled-restore");
    var disabledOriginal = await InputHashesAsync(disabled);
    await builds.SaveEspressifModuleSettingsAsync(disabled, Profile("esp32s3", "ESP32-S3-WROOM-1-N8").Settings);
    await ConfigureAsync(disabled, "S3 explicit no PSRAM");
    var disabledConfig = await File.ReadAllTextAsync(PathBoundary.Resolve(disabled, ".build/studiox-module-sdkconfig"));
    Check(!disabledConfig.Split('\n').Contains("CONFIG_SPIRAM=y"), "No PSRAM preset disables runtime initialization");
    await builds.SaveEspressifModuleSettingsAsync(disabled, new());
    await ConfigureAsync(disabled, "S3 restore native configuration");
    using var description = JsonDocument.Parse(await File.ReadAllTextAsync(PathBoundary.Resolve(disabled, ".build/project_description.json")));
    Check(EspressifPathIdentity.NormalizePath(description.RootElement.GetProperty("config_file").GetString()!) == EspressifPathIdentity.NormalizePath(PathBoundary.Resolve(disabled, "sdkconfig")), "Restore releases managed overlay and uses native sdkconfig");
    Check(await InputHashesAsync(disabled) == disabledOriginal, "Disable/restore preserves original sdkconfig/defaults/CMake");
    var nativeConfig = PathBoundary.Resolve(disabled, "config/user-sdkconfig");
    Directory.CreateDirectory(Path.GetDirectoryName(nativeConfig)!);
    File.Copy(PathBoundary.Resolve(disabled, "sdkconfig"), nativeConfig, overwrite: true);
    var customConfig = await File.ReadAllTextAsync(nativeConfig);
    customConfig = customConfig.Replace("CONFIG_LOG_DEFAULT_LEVEL_INFO=y", "# CONFIG_LOG_DEFAULT_LEVEL_INFO is not set", StringComparison.Ordinal)
        .Replace("# CONFIG_LOG_DEFAULT_LEVEL_DEBUG is not set", "CONFIG_LOG_DEFAULT_LEVEL_DEBUG=y", StringComparison.Ordinal)
        .Replace("CONFIG_LOG_DEFAULT_LEVEL=3", "CONFIG_LOG_DEFAULT_LEVEL=4", StringComparison.Ordinal);
    await File.WriteAllTextAsync(nativeConfig, customConfig);
    var nativeCMake = PathBoundary.Resolve(disabled, "CMakeLists.txt");
    await File.WriteAllTextAsync(nativeCMake, "set(SDKCONFIG \"${CMAKE_CURRENT_LIST_DIR}/config/user-sdkconfig\")\n" + await File.ReadAllTextAsync(nativeCMake));
    await ConfigureAsync(disabled, "S3 default preserves custom native configuration path");
    using var nativeDescription = JsonDocument.Parse(await File.ReadAllTextAsync(PathBoundary.Resolve(disabled, ".build/project_description.json")));
    Check(EspressifPathIdentity.NormalizePath(nativeDescription.RootElement.GetProperty("config_file").GetString()!) == EspressifPathIdentity.NormalizePath(nativeConfig), "Default does not force SDKCONFIG over user CMake");
    var nativeOriginal = await InputHashesAsync(disabled);
    await builds.SaveEspressifModuleSettingsAsync(disabled, Profile("esp32s3", "ESP32-S3-WROOM-1-N8").Settings);
    await ConfigureAsync(disabled, "S3 explicit selection overrides custom SDKCONFIG without editing CMake");
    Check((await File.ReadAllTextAsync(PathBoundary.Resolve(disabled, ".build/studiox-module-sdkconfig"))).Contains("CONFIG_LOG_DEFAULT_LEVEL_DEBUG=y", StringComparison.Ordinal), "Managed overlay preserves distinct settings from the actual custom SDKCONFIG");
    Check(await InputHashesAsync(disabled) == nativeOriginal, "Custom SDKCONFIG and CMake remain unchanged by module selection");
    var opi = CopyProject("esp32s3", "s3-octal-flash");
    await builds.SaveEspressifModuleSettingsAsync(opi, Profile("esp32s3", "ESP32-S3-WROOM-2-N32R16V").Settings);
    await ConfigureAsync(opi, "S3 official Octal Flash and Octal PSRAM");
}

async Task C5Async()
{
    var root = CopyProject("esp32c5", "c5-quad-psram");
    var cmake = PathBoundary.Resolve(root, "CMakeLists.txt");
    var originalCMake = await File.ReadAllTextAsync(cmake);
    if (!originalCMake.Contains("set(COMPONENTS main esp_driver_gpio)", StringComparison.Ordinal))
    {
        await File.WriteAllTextAsync(cmake, "set(COMPONENTS main esp_driver_gpio)\n" + originalCMake);
    }
    var original = await InputHashesAsync(root);
    await builds.SaveEspressifModuleSettingsAsync(root, Profile("esp32c5", "ESP32-C5-WROOM-1-N8R8").Settings);
    await BuildAsync(root, "C5 Quad PSRAM");
    _ = await new EspressifFlashService(catalog).PreviewAsync(root, new(Port: "COM999"));
    using var description = JsonDocument.Parse(await File.ReadAllTextAsync(PathBoundary.Resolve(root, ".build/project_description.json")));
    var components = description.RootElement.GetProperty("build_components").EnumerateArray().Select(item => item.GetString()).ToArray();
    Check(new[] { "main", "esp_driver_gpio", "esp_psram" }.All(components.Contains), "Managed PSRAM hook preserves explicitly selected user component roots");
    Check(await InputHashesAsync(root) == original, "C5 selected Quad PSRAM build preserves native inputs");
}

async Task FocusedAsync()
{
    var root = CopyProject("esp32s3", "custom-base-focused");
    var nativeConfig = PathBoundary.Resolve(root, ".studiox/user-sdkconfig");
    var custom = (await File.ReadAllTextAsync(PathBoundary.Resolve(root, "sdkconfig")))
        .Replace("CONFIG_LOG_DEFAULT_LEVEL_INFO=y", "# CONFIG_LOG_DEFAULT_LEVEL_INFO is not set", StringComparison.Ordinal)
        .Replace("# CONFIG_LOG_DEFAULT_LEVEL_DEBUG is not set", "CONFIG_LOG_DEFAULT_LEVEL_DEBUG=y", StringComparison.Ordinal)
        .Replace("CONFIG_LOG_DEFAULT_LEVEL=3", "CONFIG_LOG_DEFAULT_LEVEL=4", StringComparison.Ordinal);
    await File.WriteAllTextAsync(nativeConfig, custom);
    var cmake = PathBoundary.Resolve(root, "CMakeLists.txt");
    await File.WriteAllTextAsync(cmake, "set(SDKCONFIG \"${CMAKE_CURRENT_LIST_DIR}/.studiox/user-sdkconfig\")\n" + await File.ReadAllTextAsync(cmake));
    await ConfigureAsync(root, "Baseline actual custom native SDKCONFIG");
    var original = await InputHashesAsync(root);
    var configOriginal = await File.ReadAllTextAsync(nativeConfig);
    foreach (var label in new[] { "ESP32-S3-WROOM-1-N8", "ESP32-S3-WROOM-1-N16R8" })
    {
        await builds.SaveEspressifModuleSettingsAsync(root, Profile("esp32s3", label).Settings);
        await ConfigureAsync(root, label + " preserves established custom base");
        Check((await File.ReadAllTextAsync(PathBoundary.Resolve(root, ".build/studiox-module-sdkconfig"))).Contains("CONFIG_LOG_DEFAULT_LEVEL_DEBUG=y", StringComparison.Ordinal), label + " retains nonhardware DEBUG option from actual custom base");
    }
    Check(await File.ReadAllTextAsync(nativeConfig) == configOriginal && await InputHashesAsync(root) == original, "Module changes preserve native CMake/sdkconfig/defaults/custom file bytes");
    var stamp = await SourceStampAsync(root);
    await File.AppendAllTextAsync(nativeConfig, "\n# deliberate external hidden-base edit\n");
    Check(await SourceStampAsync(root) != stamp, "Hidden native base change invalidates source stamp used by download receipts");
    await File.WriteAllTextAsync(nativeConfig, configOriginal);
    var journal = PathBoundary.Resolve(root, ".build/studiox-module-base.json");
    var journalOriginal = await File.ReadAllTextAsync(journal);
    await File.AppendAllTextAsync(journal, "\n");
    Check(await SourceStampAsync(root) != stamp, "Native-base journal change invalidates source stamp");
    await File.WriteAllTextAsync(journal, journalOriginal);
    Check(await SourceStampAsync(root) == stamp, "Exact restoration restores complete source stamp");
    await builds.SaveEspressifModuleSettingsAsync(root, new());
    await ConfigureAsync(root, "Restore custom native configuration after overlays");
    using var description = JsonDocument.Parse(await File.ReadAllTextAsync(PathBoundary.Resolve(root, ".build/project_description.json")));
    Check(EspressifPathIdentity.NormalizePath(description.RootElement.GetProperty("config_file").GetString()!) == EspressifPathIdentity.NormalizePath(nativeConfig), "Restore releases SDKCONFIG override and returns to actual custom path");

    var first = CopyProject("esp32s3", "first-custom-rejection");
    var firstConfig = PathBoundary.Resolve(first, "config/custom-sdkconfig");
    Directory.CreateDirectory(Path.GetDirectoryName(firstConfig)!);
    File.Copy(PathBoundary.Resolve(first, "sdkconfig"), firstConfig);
    var firstCMake = PathBoundary.Resolve(first, "CMakeLists.txt");
    await File.WriteAllTextAsync(firstCMake, "set(SDKCONFIG \"${CMAKE_CURRENT_LIST_DIR}/config/custom-sdkconfig\")\n" + await File.ReadAllTextAsync(firstCMake));
    var before = await InputHashesAsync(first);
    await builds.SaveEspressifModuleSettingsAsync(first, Profile("esp32s3", "ESP32-S3-WROOM-1-N8").Settings);
    var rejected = await builds.ConfigureAsync(first, new ProgressText());
    Check(!rejected.Success && rejected.Log.Contains("custom SDKCONFIG has not been configured", StringComparison.Ordinal), "First unconfigured custom SDKCONFIG explicitly rejected without silently losing options");
    Check(await InputHashesAsync(first) == before, "Rejected first custom configuration preserves original native file bytes");
    foreach (var target in new[] { "esp32s3", "esp32c5", "esp32p4" })
    {
        var menu = CopyProject(target, "stable-frequency-" + target);
        Check(!(await builds.ReadEspressifModuleCapabilitiesAsync(menu)).FlashFrequenciesMhz.Contains(120), target + " menu exposes stable frequencies only");
        await RejectAsync(() => builds.SaveEspressifModuleSettingsAsync(menu, new(FlashFrequencyMhz: 120)), "ESP_MODULE_SETTINGS", target + " explicit unsupported dependency combination rejected");
    }
}

async Task<string> SourceStampAsync(string root)
{
    var type = typeof(BuildService).Assembly.GetType("StudioX.Engine.Debugging.DebugSourceStamp")!;
    var method = type.GetMethod("ComputeAsync", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
    return await (Task<string>)method.Invoke(null, [root, CancellationToken.None])!;
}

async Task P4Async()
{
    var root = CopyProject("esp32p4", "p4-revision-families");
    var original = await InputHashesAsync(root);
    foreach (var label in new[] { "ESP32-P4NRW16", "ESP32-P4NRW16X" })
    {
        await builds.SaveEspressifModuleSettingsAsync(root, Profile("esp32p4", label).Settings);
        await ConfigureAsync(root, label + " HEX PSRAM/revision family");
    }
    Check(await InputHashesAsync(root) == original, "P4 revision and HEX selections preserve native inputs");
}

async Task ClassicAsync()
{
    var root = CopyProject("esp32", "classic-single-dual-core");
    var original = await InputHashesAsync(root);
    foreach (var label in new[] { "ESP32-SOLO-1-N4", "ESP32-WROOM-32E-N16" })
    {
        await builds.SaveEspressifModuleSettingsAsync(root, Profile("esp32", label).Settings);
        await ConfigureAsync(root, label + " core/Flash selection");
    }
    Check(await InputHashesAsync(root) == original, "Classic single/dual-core selection preserves native inputs");
}

async Task LegacyAsync()
{
    var root = CopyProject("esp8266", "legacy-capacity");
    var original = await InputHashesAsync(root);
    await builds.SaveEspressifModuleSettingsAsync(root, new(FlashSizeMb: 4, FlashMode: "dio", FlashFrequencyMhz: 40));
    await ConfigureAsync(root, "ESP8266 dedicated legacy Flash configuration");
    Check(await InputHashesAsync(root) == original, "Legacy selected capacity preserves native inputs");
}

EspressifModuleProfile Profile(string target, string label) => EspressifModuleCatalog.ForTarget(target).Single(profile => profile.Label == label);
string CopyProject(string target, string name)
{
    var source = target == "esp8266" ? Path.GetFullPath(Path.Combine(projects, "../../espressif-projects-current/projects/esp8266_hello_world")) : Path.Combine(projects, target + "_hello_world");
    var root = Path.Combine(output, name);
    if (File.Exists(PathBoundary.Resolve(root, ".studiox/project.json")))
    {
        return root;
    }
    Directory.CreateDirectory(root);
    foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories).Where(Included))
    {
        Directory.CreateDirectory(PathBoundary.Resolve(root, Path.GetRelativePath(source, directory).Replace('\\', '/')));
    }
    foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Where(Included))
    {
        var destination = PathBoundary.Resolve(root, Path.GetRelativePath(source, file).Replace('\\', '/'));
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(file, destination, overwrite: false);
    }
    return root;
    bool Included(string path)
    {
        var relative = Path.GetRelativePath(source, path).Replace('\\', '/');
        return relative != ".build" && !relative.StartsWith(".build/", StringComparison.Ordinal) && relative != ".git" && !relative.StartsWith(".git/", StringComparison.Ordinal);
    }
}
async Task<string> InputHashesAsync(string root)
{
    var paths = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Where(path =>
    {
        var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        return !relative.StartsWith(".build/", StringComparison.Ordinal) && !relative.StartsWith(".studiox/", StringComparison.Ordinal);
    }).Order(StringComparer.Ordinal).ToArray();
    return string.Join("\n", await Task.WhenAll(paths.Select(async path => Path.GetRelativePath(root, path) + ":" + Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path))))));
}
async Task ConfigureAsync(string root, string name)
{
    var report = await builds.ConfigureAsync(root, new ProgressText());
    await File.WriteAllTextAsync(Path.Combine(root, ".build/module-validation-configure.log"), report.Log);
    Check(report.Success, name + " actual SDK configure: " + (report.Success ? "PASS" : report.Log));
}
async Task BuildAsync(string root, string name)
{
    var report = await builds.BuildAsync(root, new ProgressText());
    await File.WriteAllTextAsync(Path.Combine(root, ".build/module-validation-build.log"), report.Log);
    Check(report.Success, name + " actual SDK native build: " + (report.Success ? "PASS" : report.Log));
}
async Task RejectAsync(Func<Task> action, string code, string message)
{
    try
    {
        await action();
        throw new InvalidOperationException("Expected rejection: " + message);
    }
    catch (StudioXException exception) when (exception.Code == code) { Check(true, message); }
}
void Check(bool passed, string message)
{
    if (!passed)
    {
        throw new InvalidOperationException(message);
    }
    assertions.Add(message);
    Console.WriteLine("PASS " + message);
}
sealed class ProgressText : IProgress<string>
{
    public void Report(string value) => Console.WriteLine("  " + value);
}
