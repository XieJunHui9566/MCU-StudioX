using System.Text.Json;
using StudioX.KeilImporter;
using StudioX.KeilImporter.Validation;

if (args is ["--picker-smoke", var nativeScratch]) { return await PickerValidation.NativeSmokeAsync(nativeScratch); }
if (args is ["--offline", var offlineDirectory]) { return await OfflineWorkflowChecks.RunAsync(offlineDirectory); }
var publishedCancellationFixture = File.Exists(Path.Combine(AppContext.BaseDirectory, "cancel-published"));
if (args is ["project-info", _] && publishedCancellationFixture) { Console.WriteLine("{}"); return 0; }
if (args is ["build", var fixtureProject, _] && (publishedCancellationFixture || File.Exists(Path.Combine(fixtureProject, ".studiox/cli-fixture-mode"))))
{
    return await OfflineWorkflowChecks.CliFixtureAsync(fixtureProject, publishedCancellationFixture ? "cancel" : null);
}

if (args.Length != 7)
{
    Console.Error.WriteLine("Usage: validation <installed-cli> <packs> <archive> <scratch> <toolsets> <f407-uvprojx> <f103-uvprojx>");
    return 2;
}
var cli = Path.GetFullPath(args[0]);
var packs = Path.GetFullPath(args[1]);
var archive = Path.GetFullPath(args[2]);
var scratch = Path.GetFullPath(args[3]);
var tools = Path.GetFullPath(args[4]);
if (Directory.Exists(scratch))
{
    throw new InvalidOperationException("Validation scratch must be new.");
}
Directory.CreateDirectory(scratch);
var checks = new List<string>();
var buildReports = new List<object>();
using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(15));
var token = deadline.Token;
var runtime = Path.GetDirectoryName(Path.GetDirectoryName(cli))!;
try
{
    var f4 = (await DeviceCatalog.SearchAsync(packs, "STM32F407", token)).Single(device => device.Id == "STM32F407ZG" && device.PackId == "studiox.stm32f407" && device.PackVersion == "0.1.3");
    var f1 = (await DeviceCatalog.SearchAsync(packs, "STM32F103C8", token)).Single(device => device.Id == "STM32F103C8" && device.PackId == "studiox.stm32f103" && device.PackVersion == "0.1.3");
    Check(f4.Device.GetProperty("flashBytes").GetUInt32() == 1048576 && f1.Device.GetProperty("flashBytes").GetUInt32() == 65536, "exact STM32 model and memory metadata");
    await DeviceCatalog.VerifyAsync(f4, token);
    await DeviceCatalog.VerifyAsync(f1, token);
    Check(true, "full installed format-1 pack file hashes verified");

    await PickerValidation.RunAsync(scratch, Path.GetFullPath(args[5]), Path.GetDirectoryName(Path.GetDirectoryName(args[5]))!, cli, packs, f4, Check, token);

    var f4Root = Path.GetDirectoryName(Path.GetDirectoryName(args[5]))!;
    var f1Root = Path.GetDirectoryName(args[6])!;
    var f4Before = await SnapshotAsync(f4Root);
    var f1Before = await SnapshotAsync(f1Root);
    var f4Request = Request(args[5], f4Root, "real-f407-hal", f4, "hal");
    var f1Request = Request(args[6], f1Root, "real-f103-spl", f1, "spl");
    var f4Preview = await ImportPlanner.PlanAsync(f4Request, token);
    Check(f4Preview.CanCreate, "real STM32F407 Keil5 HAL target preview passes", f4Preview.Issues);
    Check(!Directory.Exists(f4Preview.Destination), "preview leaves output absent");
    Check(f4Preview.ApplicationSources.Any(source => source.EndsWith("system_stm32f4xx.c", StringComparison.Ordinal)) &&
        !f4Preview.PackSources.Any(source => source.EndsWith("system_stm32f4xx.c", StringComparison.Ordinal)), "original system source takes precedence without duplicate pack system");
    Check(f4Preview.Target.Defines.Contains("USE_HAL_DRIVER") && f4Preview.IncludeDirectories.Contains("Core/Inc"), "HAL macros and relative includes preserved");
    Check(!f4Preview.CMake.Contains("HSE_VALUE=8000000U", StringComparison.Ordinal) &&
        !f4Preview.CMake.Contains("USER_VECT_TAB_ADDRESS", StringComparison.Ordinal), "pack template clock and vector defaults do not override original HAL configuration");

    await RejectAsync(() => ProjectCreator.CreateAsync(f4Preview, "not-confirmed", token), "wrong preview confirmation rejected");
    var f4Result = await ProjectCreator.CreateAsync(f4Preview, f4Preview.PreviewId, token);
    Check(Directory.Exists(Path.Combine(f4Result.Directory, ".studiox")), "port adds valid StudioX metadata to existing project copy");
    var createdIdentity = await DeviceCatalog.ReadJsonAsync(Path.Combine(f4Result.Directory, ".studiox", "project.json"), token);
    Check(createdIdentity.GetProperty("entryFile").GetString() == "Core/Src/main.c" &&
        !Directory.Exists(Path.Combine(f4Result.Directory, "keil")) && !Directory.Exists(Path.Combine(f4Result.Directory, "template-reference")) &&
        !Directory.Exists(Path.Combine(f4Result.Directory, "device", "templates")), "original layout retained without wrapper or template application");
    Check(f4Preview.Files.All(file => File.Exists(Path.Combine(f4Result.Directory, file.RelativePath))) &&
        File.Exists(Path.Combine(f4Result.Directory, Path.GetRelativePath(f4Root, args[5]))), "all original project paths and Keil project file retained");
    await VerifyCopiesAsync(f4Preview, f4Result);
    await RejectAsync(() => ImportPlanner.PlanAsync(f4Request, token), "existing destination rejected");
    Check(!(await File.ReadAllTextAsync(Path.Combine(f4Result.Directory, ".studiox", "project.json"), token)).Contains("E:\\", StringComparison.Ordinal), "no developer absolute tool path in project identity");
    await VerifyBuildAsync(f4Result, "real STM32F407 HAL");

    var f1Preview = await ImportPlanner.PlanAsync(f1Request, token);
    Check(f1Preview.CanCreate, "real STM32F103 Keil5 SPL target preview passes", f1Preview.Issues);
    Check(f1Preview.AdditionalDefines.Contains("STM32F10X_MD") && f1Preview.Edits.Length == 3,
        "verified template supplies implicit SPL density macro and exact CMSIS GCC edits are previewed");
    var f1Result = await ProjectCreator.CreateAsync(f1Preview, f1Preview.PreviewId, token);
    await VerifyCopiesAsync(f1Preview, f1Result);
    var coreCopy = await File.ReadAllTextAsync(Path.Combine(f1Result.Directory, "Start", "core_cm3.c"), token);
    Check(coreCopy.Contains("\"=&r\"", StringComparison.Ordinal), "old CMSIS early-clobber fix applied only to destination copy");
    await VerifyBuildAsync(f1Result, "real STM32F103 SPL");
    Check(f4Before == await SnapshotAsync(f4Root) && f1Before == await SnapshotAsync(f1Root), "all files in both original real Keil project roots unchanged");
    Check(!Directory.EnumerateDirectories(scratch, ".studiox-keil-import-*").Any(), "successful creation leaves no staging directory");

    var fixture = Path.Combine(scratch, "fixture 中文 空格");
    Directory.CreateDirectory(Path.Combine(fixture, "src"));
    Directory.CreateDirectory(Path.Combine(fixture, "inc"));
    Directory.CreateDirectory(Path.Combine(fixture, "reference"));
    await File.WriteAllTextAsync(Path.Combine(fixture, "src", "main.c"), "int main(void) { for (;;) { } }\n", token);
    await File.WriteAllTextAsync(Path.Combine(fixture, "inc", "test.h"), "#define VALUE 1\n", token);
    await File.WriteAllTextAsync(Path.Combine(fixture, "reference", "extra.inl"), "// explicitly referenced resource\n", token);
    Directory.CreateDirectory(Path.Combine(fixture, "assets"));
    await File.WriteAllBytesAsync(Path.Combine(fixture, "assets", "logo.dat"), [0, 1, 255, 17], token);
    await File.WriteAllTextAsync(Path.Combine(fixture, "fixture.ioc"), "keep original board settings", token);
    var fixtureXml = Path.Combine(fixture, "fixture.uvprojx");
    var xml = """
        <Project><Targets><Target><TargetName>Target 1</TargetName><pCCUsed>ARMCC 5</pCCUsed><TargetOption>
        <TargetCommonOption><Device>STM32F407ZG</Device><CreateLib>0</CreateLib></TargetCommonOption>
        <TargetArmAds><Cads><VariousControls><Define>TEST=1</Define><IncludePath>inc</IncludePath></VariousControls></Cads><LDads><useFile>0</useFile></LDads></TargetArmAds>
        </TargetOption><Groups><Group><GroupName>Source</GroupName><Files>
        <File><FilePath>src/main.c</FilePath></File><File><FilePath>src/excluded.c</FilePath><FileOption><CommonProperty><IncludeInBuild>0</IncludeInBuild></CommonProperty></FileOption></File>
        <File><FilePath>reference/extra.inl</FilePath></File>
        </Files></Group></Groups></Target></Targets></Project>
        """;
    await File.WriteAllTextAsync(fixtureXml, xml, token);
    var fixtureRequest = Request(fixtureXml, fixture, "fixture-output", f4, "hal");
    var fixturePreview = await ImportPlanner.PlanAsync(fixtureRequest, token);
    Check(fixturePreview.Files.Any(file => file.RelativePath == "assets/logo.dat") && fixturePreview.Files.Any(file => file.RelativePath == "fixture.ioc"),
        "unreferenced binary resources and board configuration retained");
    await File.WriteAllTextAsync(Path.Combine(fixture, "CMakeLists.txt"), "original CMake must not be overwritten", token);
    Check((await ImportPlanner.PlanAsync(fixtureRequest, token)).Issues.Any(issue => issue.Code == "OUTPUT_COLLISION"), "existing root CMake collision explicitly blocked");
    File.Delete(Path.Combine(fixture, "CMakeLists.txt"));
    Check(fixturePreview.CanCreate && fixturePreview.Target.Excluded.Contains("src/excluded.c"), "excluded missing source is not required or compiled");
    Check(fixturePreview.Files.Any(file => file.RelativePath == "reference/extra.inl"), "explicit noncompiled support input outside IncludePath is copied");
    await File.AppendAllTextAsync(Path.Combine(fixture, "src", "main.c"), "// changed\n", token);
    await RejectAsync(() => ProjectCreator.CreateAsync(fixturePreview, fixturePreview.PreviewId, token), "source changed after preview prevents publication");
    Check(!Directory.Exists(fixturePreview.Destination), "changed input leaves destination absent");
    await RejectAsync(() => ImportPlanner.PlanAsync(fixtureRequest with { ParentDirectory = fixture }, token), "source/destination overlap rejected");
    await RejectAsync(() => ImportPlanner.PlanAsync(fixtureRequest with { ProjectName = "../escape" }, token), "project-name traversal rejected");
    await RejectAsync(() => ImportPlanner.PlanAsync(fixtureRequest with { ProjectName = "CON" }, token), "Windows reserved project-name rejected");
    await RejectAsync(() => ImportPlanner.PlanAsync(fixtureRequest with { ProjectName = "nul.config" }, token), "Windows reserved stem with extension rejected");
    await File.WriteAllTextAsync(fixtureXml, xml.Replace("<useFile>0</useFile>", "<useFile>1</useFile><ScatterFile>custom.sct</ScatterFile>", StringComparison.Ordinal), token);
    Check(!(await ImportPlanner.PlanAsync(fixtureRequest, token)).CanCreate, "custom scatter blocks misleading migration");
    await File.WriteAllTextAsync(fixtureXml, xml.Replace("<CreateLib>0</CreateLib>", "<CreateLib>0</CreateLib><BeforeMake><RunUserProg1>1</RunUserProg1><UserProg1Name>cmd /c echo unsafe</UserProg1Name></BeforeMake>", StringComparison.Ordinal), token);
    Check(!(await ImportPlanner.PlanAsync(fixtureRequest, token)).CanCreate, "active build event blocked and never executed");
    await File.WriteAllTextAsync(fixtureXml, xml.Replace("src/main.c", "../outside.c", StringComparison.Ordinal), token);
    await RejectAsync(() => ImportPlanner.PlanAsync(fixtureRequest, token), "source outside explicitly selected root rejected");
    await File.WriteAllTextAsync(fixtureXml, "<!DOCTYPE Project [<!ENTITY x SYSTEM 'file:///C:/Windows/win.ini'>]>" + xml, token);
    await RejectAsync(() => Task.FromResult(KeilProjectReader.Read(fixtureXml)), "XML DTD and external entity rejected");
    await File.WriteAllTextAsync(fixtureXml, xml.Replace("<FilePath>src/main.c</FilePath>", "<FilePath>src/main.c</FilePath><FileOption><FileArmAds><Cads><VariousControls><Define>LOCAL</Define></VariousControls></Cads></FileArmAds></FileOption>", StringComparison.Ordinal), token);
    Check(!(await ImportPlanner.PlanAsync(fixtureRequest, token)).CanCreate, "per-file compiler options rejected with actionable diagnostic");
    await File.WriteAllTextAsync(fixtureXml, xml.Replace("<TargetArmAds>", "<TargetArmAds><ArmAdsMisc><OnChipMemories><IROM><StartAddress>0x08004000</StartAddress><Size>0x100000</Size></IROM></OnChipMemories></ArmAdsMisc>", StringComparison.Ordinal), token);
    Check((await ImportPlanner.PlanAsync(fixtureRequest, token)).Issues.Any(issue => issue.Code == "MEMORY_LAYOUT" && issue.Severity == "error"), "bootloader memory offset cannot silently use default linker");
    await File.WriteAllTextAsync(fixtureXml, xml, token);
    var unicodeParent = Directory.CreateDirectory(Path.Combine(scratch, "输出 空间")).FullName;
    var unicodePreview = await ImportPlanner.PlanAsync(fixtureRequest with
    {
        ProjectName = "unicode-main",
        ParentDirectory = unicodeParent
    }, token);
    var unicodeResult = await ProjectCreator.CreateAsync(unicodePreview, unicodePreview.PreviewId, token);
    Check(await File.ReadAllTextAsync(Path.Combine(unicodeResult.Directory, "fixture.ioc"), token) == "keep original board settings" &&
        (await File.ReadAllBytesAsync(Path.Combine(unicodeResult.Directory, "assets", "logo.dat"), token)).SequenceEqual(new byte[] { 0, 1, 255, 17 }),
        "unreferenced resources retain their original bytes and paths");
    await VerifyBuildAsync(unicodeResult, "Chinese and spaced destination with explicit HAL system fallback");
    await File.WriteAllTextAsync(Path.Combine(fixture, "src", "main.c"), "int main(void) { return MISSING_IDENTIFIER; }", token);
    var compileErrorPreview = await ImportPlanner.PlanAsync(fixtureRequest with
    {
        ProjectName = "compile-error"
    }, token);
    var compileErrorResult = await ProjectCreator.CreateAsync(compileErrorPreview, compileErrorPreview.PreviewId, token);
    Check(!compileErrorResult.CompilationVerified && Directory.Exists(compileErrorResult.Directory) &&
        (await File.ReadAllTextAsync(Path.Combine(compileErrorResult.Directory, compileErrorResult.BuildLog), token)).Contains("MISSING_IDENTIFIER", StringComparison.Ordinal),
        "failed compilation retains ported project and actual compiler diagnostic without claiming success");
    await File.WriteAllTextAsync(Path.Combine(fixture, "src", "main.c"), "int main(void) { for (;;) { } }", token);
    var brokenCliDirectory = Directory.CreateDirectory(Path.Combine(scratch, "broken-cli")).FullName;
    var brokenCli = Path.Combine(brokenCliDirectory, "StudioX.Cli.exe");
    File.Copy(cli, brokenCli);
    var brokenPreview = await ImportPlanner.PlanAsync(fixtureRequest with
    {
        CliPath = brokenCli,
        ProjectName = "failed-cli"
    }, token);
    await RejectAsync(() => ProjectCreator.CreateAsync(brokenPreview, brokenPreview.PreviewId, token), "CLI startup failure aborts project creation");
    Check(!Directory.Exists(brokenPreview.Destination) && !Directory.EnumerateDirectories(scratch, ".studiox-keil-import-*").Any(), "failed CLI transaction leaves no project or staging directory");
    var mismatch = await ImportPlanner.PlanAsync(fixtureRequest with
    {
        Device = f1,
        TemplateId = "spl"
    }, token);
    Check(!mismatch.CanCreate && mismatch.Issues.Any(issue => issue.Code == "DEVICE_MISMATCH"), "explicit MCU mismatch rejected");
    Check(!(await ImportPlanner.PlanAsync(f4Request with
    {
        ProjectName = "wrong-framework",
        TemplateId = "spl"
    }, token)).CanCreate,
        "HAL source cannot silently select SPL framework template");
    using (var cancelled = new CancellationTokenSource())
    {
        cancelled.Cancel();
        await RejectAsync(() => ProjectCreator.CreateAsync(fixturePreview, fixturePreview.PreviewId, cancelled.Token), "cancelled create leaves inputs untouched");
    }

    var userData = Path.Combine(scratch, "plugin-data");
    using var imported = JsonDocument.Parse(await StudioXCli.RunAsync(cli, ["plugin", "import", runtime, userData, archive], token));
    Check(!imported.RootElement.GetProperty("enabled").GetBoolean(), "real plugin import remains disabled until explicit trust");
    await StudioXCli.RunAsync(cli, ["plugin", "enable", runtime, userData, "studiox.keil-importer"], token);
    var manifestFile = Directory.EnumerateFiles(Path.Combine(userData, "plugins"), "plugin.json", SearchOption.AllDirectories).Single();
    await using (var host = new HostProbe(Path.Combine(runtime, "plugin-host", "StudioX.PluginHost.exe"), manifestFile))
    {
        var contribution = await host.RequestAsync("describe", new
        {
        });
        Check(contribution.GetProperty("commands").GetArrayLength() == 11 && contribution.GetProperty("panels").GetArrayLength() == 1, "installed 0.2.6LTS host loads plugin SDK entry and declarations");
        await host.RequestAsync("activate", new
        {
        });
        Check(host.Panels.Count > 0, "real host publishes declarative initial panel");
        var form = new Dictionary<string, object?>
        {
            ["projectFile"] = Path.GetFullPath(args[5]),
            ["sourceRoot"] = f4Root,
            ["target"] = "",
            ["projectName"] = "host-f407",
            ["parent"] = scratch,
            ["packs"] = packs,
            ["cli"] = cli,
            ["query"] = "STM32F407",
            ["device"] = "",
            ["template"] = ""
        };
        Check((await host.InvokeAsync("load", form)).GetProperty("success").GetBoolean(), "real host loads Keil Target list");
        form["target"] = f4Request.TargetName;
        Check((await host.InvokeAsync("search", form)).GetProperty("success").GetBoolean(), "real host searches installed STM32 choices");
        form["device"] = f4.Key;
        Check((await host.InvokeAsync("templates", form)).GetProperty("success").GetBoolean(), "real host loads explicitly chosen device templates");
        form["template"] = "hal";
        Check((await host.InvokeAsync("preview", form)).GetProperty("canCreate").GetBoolean(), "real host command returns actionable migration preview");
        var confirmation = Checkbox(host.Panels[^1]);
        Check(!(await host.InvokeAsync("create", form)).GetProperty("success").GetBoolean(), "real host rejects create without explicit checkbox");
        Check(!Directory.Exists(Path.Combine(scratch, "host-f407")), "rejected UI create writes no project");
        await host.InvokeAsync("preview", form);
        var nextConfirmation = Checkbox(host.Panels[^1]);
        Check(confirmation != nextConfirmation, "fresh preview resets retained desktop confirmation checkbox identity");
        form[confirmation] = true;
        Check(!(await host.InvokeAsync("create", form)).GetProperty("success").GetBoolean(), "stale preview checkbox cannot authorize another preview");
        await host.InvokeAsync("preview", form);
        form[Checkbox(host.Panels[^1])] = true;
        var created = await host.InvokeAsync("create", form);
        Check(created.GetProperty("directory").GetString() == Path.Combine(scratch, "host-f407") && created.GetProperty("compilationVerified").GetBoolean(),
            "installed plugin host ports original target and automatically verifies actual compilation");
        var link = host.Panels[^1].GetProperty("widgets").EnumerateArray().Single(widget => widget.GetProperty("kind").GetString() == "projectLink");
        Check(link.GetProperty("value").GetProperty("directory").GetString() == created.GetProperty("directory").GetString() &&
            link.GetProperty("value").GetProperty("buildRecord").GetString() == ".studiox/keil-build.json",
            "completed host workflow publishes explicit project button and matching compilation record");
        await host.RequestAsync("deactivate", new
        {
        });
    }
    await PublishedCancellationChecks.RunAsync(f4Request, scratch, name => Check(true, name), token);
    Check(f4Before == await SnapshotAsync(f4Root) && f1Before == await SnapshotAsync(f1Root), "original real Keil projects unchanged after plugin-host UI workflow");
    await WriteReportAsync(true, null);
    Console.WriteLine("PASS: " + checks.Count + " checks. " + Path.Combine(scratch, "results.json"));
    return 0;
}
catch (Exception error)
{
    await WriteReportAsync(false, error.ToString());
    Console.Error.WriteLine(error);
    return 1;
}

ImportRequest Request(string projectFile, string root, string name, DeviceChoice device, string template) =>
    new(Path.GetFullPath(projectFile), Path.GetFullPath(root), KeilProjectReader.Read(projectFile).Single().Name, name, scratch, cli, packs, device, template);

void Check(bool success, string name, object? detail = null)
{
    if (!success)
    {
        throw new InvalidOperationException(name + ": " + JsonSerializer.Serialize(detail, DeviceCatalog.Json));
    }
    checks.Add(name);
    Console.WriteLine("PASS " + name);
}

async Task RejectAsync(Func<Task> operation, string name)
{
    try
    {
        await operation();
    }
    catch (Exception error) when (error is InvalidOperationException or IOException or System.Xml.XmlException or OperationCanceledException)
    {
        Check(true, name);
        return;
    }
    throw new InvalidOperationException("Expected rejection: " + name);
}

async Task<string> SnapshotAsync(string root)
{
    var hashes = new List<string>();
    foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
    {
        hashes.Add(Path.GetRelativePath(root, file) + "\t" + await ImportPaths.HashAsync(file, token));
    }
    return ImportPaths.HashText(string.Join('\n', hashes));
}

async Task VerifyBuildAsync(CreationResult result, string name)
{
    var project = result.Directory;
    Check(result.CompilationVerified, name + " automatic build passes", result.BuildError);
    var log = await File.ReadAllTextAsync(Path.Combine(project, result.BuildLog), token);
    await File.WriteAllTextAsync(Path.Combine(scratch, Path.GetFileName(project) + "-build.txt"), log, token);
    var firmware = Directory.EnumerateFiles(Path.Combine(project, ".build"), "firmware.elf", SearchOption.AllDirectories).Single();
    buildReports.Add(new
    {
        name,
        sha256 = await ImportPaths.HashAsync(firmware, token),
        bytes = new FileInfo(firmware).Length
    });
    Check(true, name + " actual managed GNU build produces ELF");
}

async Task VerifyCopiesAsync(ImportPreview plan, CreationResult result)
{
    foreach (var file in plan.Files.Where(file => !plan.Edits.Any(edit => edit.RelativePath == file.RelativePath)))
    {
        if (await ImportPaths.HashAsync(Path.Combine(result.Directory, file.RelativePath), token) != file.Sha256)
        {
            throw new InvalidOperationException("Original project bytes changed in copy: " + file.RelativePath);
        }
    }
    Check(true, result.DeviceId + " all unedited source, startup and resource copies preserve exact original bytes");
}

static string Checkbox(JsonElement panel) => panel.GetProperty("widgets").EnumerateArray().Single(widget => widget.GetProperty("kind").GetString() == "form")
    .GetProperty("children").EnumerateArray().Single(widget => widget.GetProperty("kind").GetString() == "checkbox").GetProperty("id").GetString()!;

Task WriteReportAsync(bool success, string? error) => File.WriteAllTextAsync(Path.Combine(scratch, "results.json"),
    JsonSerializer.Serialize(new
    {
        success,
        checks,
        buildReports,
        error,
        hardware = "not-connected-or-tested",
        ideSource = "unchanged"
    }, DeviceCatalog.Json));
