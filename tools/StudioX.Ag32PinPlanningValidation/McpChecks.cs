using System.Text.Json;
using StudioX.Application;
using StudioX.Application.Mcp;
using StudioX.Engine;
using StudioX.Foundation;
using StudioX.Packages;

/// <summary>通过真实 MCP 握手检查授权、编辑冲突和源文件同步契约，全程只运行 VE 转换器。</summary>
internal static class McpChecks
{
    internal static async Task RunAsync(string runtime, string project, string output, Action<bool, string> check)
    {
        var source = PathBoundary.Resolve(project, "logic/pins.ve");
        var data = Path.Combine(output, "mcp-data");
        var authorizer = new PlanAuthorizer();
        var dirty = false;
        await File.WriteAllTextAsync(source, "# MCP 隔离工程\nGPIO4_4 PIN_21\n");
        await using var services = new WorkbenchService(runtime, data);
        await using var session = await StudioXMcpSession.CreateAsync(new StudioXMcpTools(services, project, authorizer,
            () => Task.FromResult(dirty), includePlugins: false));
        var responses = new Dictionary<string, JsonElement>();
        async Task<JsonElement> Call(string tool, object arguments, string label)
        {
            using var parsed = JsonDocument.Parse(await session.CallToolAsync(tool, JsonSerializer.Serialize(arguments, JsonStore.Options)));
            var response = parsed.RootElement.Clone();
            responses[label] = response;
            await JsonStore.WriteAsync(Path.Combine(output, "mcp-responses.json"), responses);
            return response;
        }
        async Task CheckRejected(string code, object arguments, string label)
        {
            var before = await File.ReadAllBytesAsync(source);
            var response = await Call("ag32_pin_plan_apply", arguments, label);
            var after = await File.ReadAllBytesAsync(source);
            check(ErrorCode(response) == code && before.SequenceEqual(after), label + "; VE bytes unchanged");
        }
        var definitions = await session.ListToolsAsync();
        check(new[] { "ag32_pin_plan_read", "ag32_pin_plan_apply" }.All(name => definitions.Count(item => item.Name == name) == 1),
            "MCP handshake discovers both graphical planning tools exactly once");
        using var schema = JsonDocument.Parse(definitions.Single(item => item.Name == "ag32_pin_plan_apply").ParametersJson);
        var properties = schema.RootElement.GetProperty("properties");
        var required = schema.RootElement.GetProperty("required").EnumerateArray().Select(item => item.GetString()).ToArray();
        check(required.Contains("expected_source_sha256") && required.Contains("assignments_json") &&
            properties.GetProperty("assignments_json").GetProperty("type").GetString() == "string" &&
            new[] { "hse_mhz", "sys_mhz", "bus_mhz" }.All(name => properties.GetProperty(name).GetRawText().Contains("number")),
            "MCP schema requires source hash and mapping JSON, with nullable numeric MHz clocks");
        var read = await Call("ag32_pin_plan_read", new
        {
        }, "read");
        check(read.GetProperty("canEdit").GetBoolean() && read.GetProperty("pins").GetArrayLength() == 48 && authorizer.Requests.Count == 0,
            "MCP planning read returns exact package without approval");
        var hash = read.GetProperty("sourceSha256").GetString()!;
        var mappingJson = JsonSerializer.Serialize(new[] { new Ag32PinAssignment("GPIO4_4", 2) }, JsonStore.Options);
        var arguments = new
        {
            expected_source_sha256 = hash,
            assignments_json = mappingJson
        };
        await CheckRejected("MCP_APPROVAL_DENIED", arguments, "MCP denied write approval");
        check(authorizer.Requests.Count == 1 && authorizer.Requests[0].Permission == StudioXMcpPermission.FileWrite &&
            authorizer.Requests[0].Summary.Contains(hash, StringComparison.Ordinal) && authorizer.Requests[0].Summary.Contains("PIN_2", StringComparison.Ordinal),
            "MCP approval binds FileWrite, full VE hash and requested pin");
        authorizer.AllowApply = true;
        var applied = await Call("ag32_pin_plan_apply", arguments, "applied");
        check(applied.GetProperty("applied").GetBoolean() && applied.GetProperty("path").GetString() == "logic/pins.ve" &&
            !applied.GetProperty("hardwareConnected").GetBoolean() && File.ReadAllText(source).Contains("GPIO4_4 PIN_2", StringComparison.Ordinal),
            "MCP approved apply returns source path and applied flag for real time editor sync");
        check(File.Exists(PathBoundary.Resolve(project, applied.GetProperty("vexPath").GetString()!)) &&
            File.Exists(PathBoundary.Resolve(project, applied.GetProperty("sdcPath").GetString()!)) &&
            !Directory.Exists(PathBoundary.Resolve(project, ".build/ag32-mapping")) &&
            !Directory.GetFiles(project, "*.bin", SearchOption.AllDirectories).Any() && !services.Debugger.IsActive && !services.Ag32PinMapping.LicenseConfigured,
            "MCP planning produces real VEX and SDC without Supra license, download images or debug session");
        hash = applied.GetProperty("snapshot").GetProperty("sourceSha256").GetString()!;
        arguments = new
        {
            expected_source_sha256 = hash,
            assignments_json = mappingJson
        };
        foreach (var (conflictingMappings, label) in new (Ag32PinAssignment[], string)[]
        {
            ([new("GPIO4_4", 21), new("GPIO4_4", 2)], "MCP rejects duplicate GPIO on PIN_21 and PIN_2"),
            ([new("GPIO4_4", 2), new("GPIO4_5", 2)], "MCP rejects physical pin collision"),
            ([new("GPIO7_6", 2), new("UART0_UARTTXD", 21)], "MCP rejects shared internal GPIO resource")
        })
        {
            await CheckRejected("AG32_PIN_PLAN_CONFLICT", new
            {
                expected_source_sha256 = hash,
                assignments_json = JsonSerializer.Serialize(conflictingMappings, JsonStore.Options)
            }, label);
        }
        var requests = authorizer.Requests.Count;
        dirty = true;
        await CheckRejected("MCP_UNSAVED_FILES", arguments, "MCP dirty editor blocks apply before approval");
        check(authorizer.Requests.Count == requests, "MCP dirty source does not create an approval card");
        dirty = false;
        authorizer.OnApproval = _ => dirty = true;
        await CheckRejected("MCP_UNSAVED_FILES", arguments, "MCP dirty editor during approval blocks commit");
        dirty = false;
        authorizer.OnApproval = _ => File.AppendAllText(source, "# 审批期间用户编辑\n");
        var beforeRace = await File.ReadAllTextAsync(source);
        var race = await Call("ag32_pin_plan_apply", arguments, "approval-race");
        check(ErrorCode(race) == "AG32_PIN_PLAN_STALE" && await File.ReadAllTextAsync(source) == beforeRace + "# 审批期间用户编辑\n",
            "MCP approval-time VE change rejects old hash and preserves user edit");
        authorizer.OnApproval = null;
        read = await Call("ag32_pin_plan_read", new
        {
        }, "read-after-race");
        hash = read.GetProperty("sourceSha256").GetString()!;
        requests = authorizer.Requests.Count;
        await CheckRejected("AG32_PIN_PLAN_INPUT", new
        {
            expected_source_sha256 = hash,
            assignments_json = "[{"
        }, "MCP malformed mapping JSON rejected");
        check(authorizer.Requests.Count == requests, "MCP malformed JSON does not ask permission");
        var beforeMalformed = await File.ReadAllBytesAsync(source);
        using (var malformed = JsonDocument.Parse(await session.CallToolAsync("ag32_pin_plan_apply", "[")))
        {
            check(ErrorCode(malformed.RootElement) == "MCP_ARGUMENTS", "MCP malformed outer arguments produce protocol input error");
        }
        var afterMalformed = await File.ReadAllBytesAsync(source);
        check(beforeMalformed.SequenceEqual(afterMalformed), "MCP malformed outer arguments do not write files");
        await File.WriteAllTextAsync(source, "# 复杂映射保留\nCUSTOM_LOGIC signal\nGPIO4_4 PIN_2\n");
        read = await Call("ag32_pin_plan_read", new
        {
        }, "complex-read");
        check(!read.GetProperty("canEdit").GetBoolean() && read.GetProperty("diagnostics").GetArrayLength() > 0,
            "MCP exposes complex VE as read only with diagnostics");
        await CheckRejected("AG32_PIN_PLAN_READONLY", new
        {
            expected_source_sha256 = read.GetProperty("sourceSha256").GetString(),
            assignments_json = mappingJson
        }, "MCP complex VE apply rejected");
        await File.WriteAllTextAsync(source, "GPIO4_4 PIN_2\n");
        read = await Call("ag32_pin_plan_read", new
        {
        }, "read-for-null-entry");
        requests = authorizer.Requests.Count;
        await CheckRejected("AG32_PIN_PLAN_INPUT", new
        {
            expected_source_sha256 = read.GetProperty("sourceSha256").GetString(),
            assignments_json = "[null]"
        }, "MCP null assignment rejected as structured input error");
        check(authorizer.Requests.Count == requests, "MCP null assignment does not reach authorization");
        await CheckRejected("AG32_PIN_PLAN_INPUT", new
        {
            expected_source_sha256 = read.GetProperty("sourceSha256").GetString(),
            assignments_json = "[{\"pinNumber\":2}]"
        }, "MCP missing function rejected as structured input error");
        var manifestPath = PathBoundary.Resolve(project, ".studiox/project.json");
        var packPath = PathBoundary.Resolve(project, "device/manifest.json");
        var originalProject = await ProjectService.ReadAsync(project);
        var originalPack = await JsonStore.ReadAsync<PackManifest>(packPath);
        var otherProfile = Ag32DeviceCatalog.All.First(profile => profile.DeviceId != originalProject.DeviceId && profile.CanMap);
        var otherDevice = originalPack.Devices.FirstOrDefault(device => device.Id == otherProfile.DeviceId)
            ?? originalPack.Devices.Single(device => device.Id == originalProject.DeviceId) with
            {
                Id = otherProfile.DeviceId,
                DisplayName = otherProfile.DeviceId,
                FlashOrigin = otherProfile.FlashOrigin,
                FlashBytes = otherProfile.FlashBytes,
                RamOrigin = otherProfile.RamOrigin,
                RamBytes = otherProfile.RamBytes,
                OpenOcd = null
            };
        if (!originalPack.Devices.Any(device => device.Id == otherDevice.Id))
        {
            await JsonStore.WriteAsync(packPath, originalPack with
            {
                Devices = [.. originalPack.Devices, otherDevice]
            });
        }
        var otherProject = originalProject with
        {
            DeviceId = otherProfile.DeviceId,
            PinMapping = new(otherProfile.TargetDevice)
        };
        var expectedSnapshot = await services.Ag32PinPlanning.ReadAsync(project);
        var unchangedVe = await File.ReadAllBytesAsync(source);
        authorizer.OnApproval = _ => JsonStore.WriteAsync(manifestPath, otherProject).GetAwaiter().GetResult();
        var identityRace = await Call("ag32_pin_plan_apply", new
        {
            expected_source_sha256 = expectedSnapshot.SourceSha256,
            assignments_json = mappingJson
        }, "approval-target-race");
        var afterIdentityRace = await File.ReadAllBytesAsync(source);
        check(ErrorCode(identityRace) == "AG32_PIN_PLAN_STALE" && unchangedVe.SequenceEqual(afterIdentityRace) &&
            (await ProjectService.ReadAsync(project)).DeviceId == otherProfile.DeviceId,
            "MCP approval-time target change is rejected even with identical VE bytes");
        authorizer.OnApproval = null;
        try
        {
            await services.Ag32PinPlanning.ApplyAsync(project, expectedSnapshot, [new("GPIO4_4", 21)], expectedSnapshot.Clocks);
            throw new InvalidOperationException("Expected draft target identity rejection.");
        }
        catch (StudioXException ex) when (ex.Code == "AG32_PIN_PLAN_STALE")
        {
            var afterDraft = await File.ReadAllBytesAsync(source);
            check(unchangedVe.SequenceEqual(afterDraft), "Engine snapshot-bound graphical draft rejects changed model and package without writing VE");
        }
        await JsonStore.WriteAsync(manifestPath, originalProject);
        await JsonStore.WriteAsync(packPath, originalPack);
        await JsonStore.WriteAsync(Path.Combine(output, "mcp-responses.json"), responses);
    }

    private static string? ErrorCode(JsonElement value) => value.TryGetProperty("code", out var code) ? code.GetString() : null;

    private sealed class PlanAuthorizer : IStudioXMcpAuthorizer
    {
        internal List<StudioXMcpApprovalRequest> Requests { get; } = [];
        internal bool AllowApply
        {
            get; set;
        }
        internal Action<StudioXMcpApprovalRequest>? OnApproval
        {
            get; set;
        }
        public Task<bool> ApproveAsync(StudioXMcpApprovalRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Requests.Add(request);
            OnApproval?.Invoke(request);
            return Task.FromResult(AllowApply && request.Tool == "ag32_pin_plan_apply");
        }
    }
}
