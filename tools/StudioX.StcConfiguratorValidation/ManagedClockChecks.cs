namespace StudioX.StcConfiguratorValidation;

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using StudioX.Application;
using StudioX.Application.Plugins;
using StudioX.Engine;
using StudioX.Foundation;
using StudioX.Packages;
using StudioX.StcConfigurator;

internal static class ManagedClockChecks
{
    internal static async Task RunAsync(InstalledPack pack, string archive, string runtime, string hostRuntime,
        string output, Action<bool, string> check)
    {
        var project = Path.Combine(output, "managed-project");
        var manifest = await new ProjectService().CreateAsync(pack, "IAP15L2K61S2", "bare", "managed_clock", project);
        var path = Path.Combine(project, "device.mcuconfig");
        var originalSource = await File.ReadAllTextAsync(Path.Combine(project, "src/main.c"));
        await using var services = new WorkbenchService(runtime, Path.Combine(output, "managed-data"));
        // 验收宿主沿用测试编译输出；工具运行时仍是调用方明确传入的锁定版本。
        var pluginManager = new PluginManagerService(hostRuntime, Path.Combine(output, "managed-data"));
        await using var managerLifetime = pluginManager;
        var ownership = new PluginProjectClockOwnership(pluginManager);
        services.ProjectClocks.Ownership = ownership;
        var entry = await pluginManager.ImportAsync(archive);
        var descriptor = entry.Manifest!;
        check(!await PluginProjectMatcher.MatchesAsync(descriptor, null), "project-scoped configurator never matches no-project context");
        check(await PluginProjectMatcher.MatchesAsync(descriptor, project), "exact pack/device/template/package/profile combination matches");
        var ordinary = Path.Combine(output, "ordinary-project");
        await new ProjectService().CreateAsync(pack, "IAP15L2K61S2", "bare", "ordinary_clock", ordinary);
        var ordinaryManifest = await ProjectService.ReadAsync(ordinary);
        await JsonStore.WriteAsync(Path.Combine(ordinary, ".studiox/project.json"), ordinaryManifest with
        {
            GraphicalConfiguration = null
        });
        check(!await PluginProjectMatcher.MatchesAsync(descriptor, ordinary), "same MCU without explicit configuration association stays hidden");
        var config = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        var bad = config.DeepClone();
        bad["target"]!["packageId"] = "LQFP48";
        await File.WriteAllTextAsync(path, bad.ToJsonString());
        check(!await PluginProjectMatcher.MatchesAsync(descriptor, project), "package mismatch hides configuration entry");
        await File.WriteAllTextAsync(path, config.ToJsonString());

        await services.StcIsp.SaveSettingsAsync(project, new(Port: "COM77", ClockMode: StcClockMode.InternalRc, ClockFrequencyHz: 12000000));
        await pluginManager.SetEnabledAsync(entry.Id, true);
        await using (var nonmatching = await pluginManager.OpenWorkspaceAsync(ordinary, (_, _, _, _) => throw new InvalidOperationException("nonmatching plugin must not execute")))
        {
            check(nonmatching.Contributions.Count == 0 && !nonmatching.IsPluginRunning(entry.Id), "nonmatching workspace starts no configurator process or contributions");
        }
        var pending = await services.ProjectClocks.ReadAsync(project);
        check(pending.Managed && !pending.Ready, "enabled matching provider locks clock even before initial setup");
        var pendingSettings = await services.StcIsp.LoadSettingsAsync(project);
        check(pendingSettings.ClockMode == StcClockMode.Preserve && pendingSettings.ClockFrequencyHz is null, "unset graphical clock never revives old 12 MHz manual value");
        await RejectAsync(() => services.StcIsp.SaveSettingsAsync(project, pendingSettings with { ClockMode = StcClockMode.InternalRc, ClockFrequencyHz = 16000000 }), "STC_ISP_CLOCK_MANAGED");
        check(true, "application API rejects manual frequency override while clock is owned");

        var model = new StcConfigurationModel(await File.ReadAllTextAsync(path));
        void Configure(int divider)
        {
            var values = model.View("", true).Fields.ToDictionary(field => field.Id, field => field.Value);
            values["clockSource"] = "IRC";
            values["clockMHz"] = "24";
            values["systemDivider"] = divider.ToString(System.Globalization.CultureInfo.InvariantCulture);
            values["reserve"] = "none";
            model.Apply(JsonSerializer.SerializeToElement(new
            {
                action = "parameters",
                values
            }));
            model.PrepareClockValidation();
        }
        Configure(2);
        check(model.Document["configuration"]!["profileData"]!["projectClock"]!["inputSha256"]!.GetValue<string>() == ProjectClockService.InputHash(model.Document), "plugin integer JCS clock receipt matches host projection");
        await File.WriteAllTextAsync(path, model.Document.ToJsonString(), new UTF8Encoding(false));
        var state = await services.ProjectClocks.ReadAsync(project);
        if (!state.Ready)
        {
            throw new InvalidOperationException(state.Message);
        }
        check(state.Managed && state.Ready && state.SourceFrequencyHz == 24000000 && state.SystemFrequencyHz == 12000000 && state.SystemDivider == 2, "24 MHz source divided by two gives distinct 12 MHz system frequency");
        var settings = await services.StcIsp.LoadSettingsAsync(project);
        check(settings.ClockMode == StcClockMode.InternalRc && settings.ClockFrequencyHz == 24000000, "ISP receives source frequency instead of divided system frequency");
        await services.StcIsp.SaveSettingsAsync(project, settings with
        {
            TransferBaud = 57600
        });
        var transport = await StcIspSettings.ReadAsync(project);
        check(transport.TransferBaud == 57600 && transport.ClockMode == StcClockMode.Preserve && transport.ClockFrequencyHz is null, "download file retains transport only while clock facts stay in mcuconfig");
        services.ProjectClocks.HasPendingChangesAsync = (_, _) => Task.FromResult(true);
        check(!(await services.ProjectClocks.ReadAsync(project)).Ready, "unsaved graphical or editor changes block managed clock use");
        await RejectAsync(() => services.StcIsp.PreviewAsync(project, settings), "PROJECT_CLOCK");
        services.ProjectClocks.HasPendingChangesAsync = null;

        var build = await services.Builds.BuildAsync(project);
        if (!build.Success)
        {
            throw new InvalidOperationException(build.Log);
        }
        File.Copy(build.LogPath!, Path.Combine(output, "managed-clock-compile.log"));
        var database = await File.ReadAllTextAsync(Path.Combine(project, ".build/compile_commands.json"));
        check(database.Contains("STUDIOX_CLOCK_HZ=12000000UL", StringComparison.Ordinal) && database.Contains("STUDIOX_CLOCK_DIVIDER=2", StringComparison.Ordinal) && database.Contains("stc15_clock_startup.c", StringComparison.Ordinal), "actual SDCC build applies 12 MHz macro and 2-divider startup source");
        var map = await File.ReadAllTextAsync(Path.Combine(project, ".build/firmware.map"));
        var hook = await File.ReadAllTextAsync(Path.Combine(project, ".build/CMakeFiles/firmware.dir/device/sdk/support/stc15_clock_startup.c.asm"));
        check(System.Text.RegularExpressions.Regex.IsMatch(map, @"___sdcc_external_startup\s+stc15_clock_startup") &&
            hook.Contains("anl\ta,#0xf8", StringComparison.OrdinalIgnoreCase) && hook.Contains("orl\ta,#0x01", StringComparison.OrdinalIgnoreCase) &&
            !build.Log.Contains("warning 294", StringComparison.Ordinal), "linked reset startup uses project divider hook and preserves upper clock register bits");
        check(await File.ReadAllTextAsync(Path.Combine(project, "src/main.c")) == originalSource, "clock management never overwrites user main source");
        var savedClockHash = state.ConfigurationSha256;
        var externallyChanged = (JsonObject)model.Document.DeepClone();
        externallyChanged["configuration"]!["clocks"]![0]!["parameters"]!["divider"] = 4;
        await File.WriteAllTextAsync(path, externallyChanged.ToJsonString());
        check(!(await services.ProjectClocks.ReadAsync(project)).Ready, "external clock edit invalidates saved validation receipt");
        Configure(4);
        await File.WriteAllTextAsync(path, model.Document.ToJsonString());
        await RejectAsync(() => services.StcIsp.PreviewAsync(project, settings with { TransferBaud = 57600 }), "STC_ISP_BUILD");
        check(true, "new valid divider still rejects previous firmware receipt before any hardware access");
        var stale = new StcIspPreparation(project, manifest.DeviceId, 62464, "COM77", "unused", "unused", "unused", 0, 0, "unused", "unused", "unused", settings with
        {
            TransferBaud = 57600
        }, ClockConfigurationSha256: savedClockHash);
        await RejectAsync(() => services.StcIsp.VerifyPreparedAsync(stale), "STC_ISP_PREPARE");
        check(true, "prepared download rejects clock changes even when ISP source frequency is unchanged");
        await RejectAsync(() => services.GraphicalClockControl.ReleaseAsync(project), "PROJECT_CLOCK_OWNER");
        await pluginManager.SetEnabledAsync(entry.Id, false);
        check((await services.ProjectClocks.ReadAsync(project)).Managed, "disabling provider preserves saved clock ownership");
        var preserved = await File.ReadAllTextAsync(path);
        await File.WriteAllTextAsync(path, "{");
        var malformed = await services.ProjectClocks.ReadAsync(project);
        check(malformed.Managed && !malformed.Ready, "malformed associated configuration stays locked after provider disabled");
        await File.WriteAllTextAsync(path, preserved);
        var indexPath = Path.Combine(project, ".studiox/pack-files.sha256.json");
        File.Move(indexPath, indexPath + ".fixture");
        try
        {
            var missingIndex = await services.ProjectClocks.ReadAsync(project);
            check(missingIndex.Managed && !missingIndex.Ready, "missing template provenance never falls back to manual frequency");
        }
        finally { File.Move(indexPath + ".fixture", indexPath); }
        await services.GraphicalClockControl.ReleaseAsync(project);
        check(!(await services.ProjectClocks.ReadAsync(project)).Managed && (await services.StcIsp.LoadSettingsAsync(project)).ClockMode == StcClockMode.Preserve, "explicit detach unlocks manual mode without restoring obsolete frequency");
        await pluginManager.SetEnabledAsync(entry.Id, true);
        check(!(await services.ProjectClocks.ReadAsync(project)).Ready, "re-enabled provider requires renewed configuration save after detach");
        await JsonStore.WriteAsync(Path.Combine(project, ".studiox/project.json"), manifest with
        {
            DeviceId = "IAP15F2K61S2"
        });
        check(!await PluginProjectMatcher.MatchesAsync(descriptor, project), "exact L/F target substitution does not activate plugin");
    }

    private static async Task RejectAsync(Func<Task> action, string code)
    {
        try
        {
            await action();
        }
        catch (StudioXException error) when (error.Code == code) { return; }
        throw new InvalidOperationException("Expected rejection " + code);
    }
}
