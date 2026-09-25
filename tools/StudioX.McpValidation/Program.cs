using System.Net;
using System.Text;
using System.Text.Json;
using StudioX.Application;
using StudioX.Application.Mcp;
using StudioX.Application.Serial;
using StudioX.Application.Skills;
using StudioX.Devices;
using StudioX.Engine;
using StudioX.Foundation;

var root = Path.Combine(Path.GetTempPath(), "studiox-mcp-validation-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var checks = 0;
void Check(bool condition, string description)
{
    if (!condition) throw new Exception(description);
    Console.WriteLine("PASS " + description);
    checks++;
}
bool RejectsSkillPath(Action action)
{
    try { action(); return false; }
    catch (StudioXException) { return true; }
}

try
{
    var project = Path.Combine(root, "project");
    Directory.CreateDirectory(Path.Combine(project, ".studiox"));
    Directory.CreateDirectory(Path.Combine(project, "src"));
    Directory.CreateDirectory(Path.Combine(project, ".build"));
    await JsonStore.WriteAsync(Path.Combine(project, ".studiox", "project.json"),
        new ProjectManifest(1, "mcp-test", "demo.pack", "1.0.0", "offline", "demo", "blank", "gcc", "1.0.0", "gcc"));
    await File.WriteAllTextAsync(Path.Combine(project, "src", "main.c"), "int main(void) { return 0; }\n");
    await File.WriteAllTextAsync(Path.Combine(project, ".build", "studiox-build.log"), "compiler diagnostic: test\n");
    var userSkillsRoot = Path.Combine(root, "user-skills");
    var bundledSkillsRoot = Path.Combine(root, "runtime", "skills");
    var projectSkillsRoot = Path.Combine(project, ".agents", "skills");
    var bundledSkill = Path.Combine(bundledSkillsRoot, "studiox-validation");
    var bundledOnlySkill = Path.Combine(bundledSkillsRoot, "studiox-bundled-validation");
    var userSkill = Path.Combine(userSkillsRoot, "studiox-validation");
    var projectSkill = Path.Combine(projectSkillsRoot, "studiox-validation");
    var projectOnlySkill = Path.Combine(projectSkillsRoot, "project-validation");
    Directory.CreateDirectory(bundledSkill);
    Directory.CreateDirectory(bundledOnlySkill);
    Directory.CreateDirectory(Path.Combine(userSkill, "references"));
    Directory.CreateDirectory(Path.Combine(projectSkill, "references"));
    Directory.CreateDirectory(Path.Combine(projectOnlySkill, "references"));
    await File.WriteAllTextAsync(Path.Combine(bundledSkill, "SKILL.md"),
        "---\nname: studiox-validation\ndescription: Bundled scope guidance\n---\n# Bundled skill\n");
    await File.WriteAllTextAsync(Path.Combine(bundledOnlySkill, "SKILL.md"),
        "---\nname: studiox-bundled-validation\ndescription: Bundled-only guidance\n---\n# Bundled-only skill\n");
    await File.WriteAllTextAsync(Path.Combine(userSkill, "SKILL.md"),
        "---\nname: studiox-validation\ndescription: User scope guidance\n---\n# User skill\n");
    await File.WriteAllTextAsync(Path.Combine(userSkill, "references", "guide.md"), "user reference\n");
    await File.WriteAllTextAsync(Path.Combine(projectSkill, "SKILL.md"),
        "---\nname: studiox-validation\ndescription: Project scope guidance\n---\n# Project skill\n");
    await File.WriteAllTextAsync(Path.Combine(projectOnlySkill, "SKILL.md"),
        "---\nname: project-validation\ndescription: Project only test skill\n---\n# Project-only body\n");
    await File.WriteAllTextAsync(Path.Combine(projectOnlySkill, "references", "guide.md"),
        "Project-only reference\n");
    Directory.CreateDirectory(Path.Combine(projectSkillsRoot, "bad-skill"));
    await File.WriteAllTextAsync(Path.Combine(projectSkillsRoot, "bad-skill", "SKILL.md"),
        "---\nname: mismatch\ndescription: Invalid folder/name pair\n---\n");
    var userCatalog = new AgentSkillCatalog(project, userSkillsRoot, projectSkillsRoot);
    var userDiscovery = userCatalog.Discover();
    Check(userDiscovery.Skills.Count == 1 && userDiscovery.Skills[0].Scope == "user" &&
        userCatalog.ReadSkill("studiox-validation").Content.Contains("# User skill", StringComparison.Ordinal),
        "user Skill is discovered by metadata and its body loads on demand");
    Check(userCatalog.ReadReference("studiox-validation", "references/guide.md").Content == "user reference\n" &&
        RejectsSkillPath(() => userCatalog.ReadReference("studiox-validation", "references/../SKILL.md")) &&
        RejectsSkillPath(() => userCatalog.ReadReference("studiox-validation", "scripts/run.ps1")),
        "Skill references are scoped to references/ without traversal or script access");
    var bundledCatalog = new AgentSkillCatalog(project, userSkillsRoot, projectSkillsRoot,
        bundledSkillsRoot: bundledSkillsRoot);
    var bundledDiscovery = bundledCatalog.Discover();
    Check(bundledDiscovery.Skills.Count == 2 &&
        bundledDiscovery.Skills.Single(skill => skill.Name == "studiox-bundled-validation").Scope == "bundled" &&
        bundledDiscovery.Skills.Single(skill => skill.Name == "studiox-validation").Scope == "user" &&
        bundledCatalog.ReadSkill("studiox-validation").Content.Contains("# User skill", StringComparison.Ordinal) &&
        bundledCatalog.ReadSkill("studiox-bundled-validation").Content.Contains("# Bundled-only skill", StringComparison.Ordinal),
        "bundled MCU Skills are defaults and user Skills override the same name");
    var bundledProjectCatalog = new AgentSkillCatalog(project, userSkillsRoot, projectSkillsRoot,
        includeProjectSkills: true, bundledSkillsRoot: bundledSkillsRoot);
    Check(bundledProjectCatalog.ReadSkill("studiox-validation").Content.Contains("# Project skill", StringComparison.Ordinal),
        "enabled project Skills override user and bundled defaults");
    var bundledService = new AiSkillService(Path.Combine(root, "skill-service-data"), Path.Combine(root, "runtime"));
    Check(bundledService.Discover(project).Skills.Any(skill =>
            skill.Name == "studiox-bundled-validation" && skill.Scope == "bundled"),
        "application Skill service reads bundled runtime/skills without installation in the user profile");
    var trustedCatalog = new AgentSkillCatalog(project, userSkillsRoot, projectSkillsRoot,
        includeProjectSkills: true);
    var trustedDiscovery = trustedCatalog.Discover();
    Check(trustedDiscovery.Skills.Count == 2 &&
        trustedDiscovery.Skills.Single(skill => skill.Name == "studiox-validation").Scope == "project" &&
        trustedCatalog.ReadSkill("studiox-validation").Content.Contains("# Project skill", StringComparison.Ordinal) &&
        trustedDiscovery.Diagnostics.Any(item => item.Contains("bad-skill", StringComparison.Ordinal)),
        "enabled project Skill takes precedence and invalid manifests remain diagnostic");
    var sampleSkillsRoot = Path.Combine(Directory.GetCurrentDirectory(), "examples", "skills");
    var sampleCatalog = new AgentSkillCatalog(project, sampleSkillsRoot, projectSkillsRoot);
    var sampleDiscovery = sampleCatalog.Discover();
    var expectedSamples = new[] { "mcu-build-repair", "mcu-code-style", "mcu-debug-serial", "mcu-device-evidence" };
    Check(sampleDiscovery.Diagnostics.Count == 0 &&
        sampleDiscovery.Skills.Select(skill => skill.Name).SequenceEqual(expectedSamples) &&
        expectedSamples.All(name => sampleCatalog.ReadSkill(name).Content.Contains("\n---\n", StringComparison.Ordinal)),
        "four MCU sample Skills have valid metadata and readable instructions");
    var outside = Path.Combine(root, "outside.c");
    await File.WriteAllTextAsync(outside, "untouched");
    var authorizer = new SwitchingAuthorizer();
    var preparedGitRuntime = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "git-runtime");
    var hasBundledGit = File.Exists(Path.Combine(preparedGitRuntime, "git", "cmd", "git.exe"));
    await using var services = new WorkbenchService(hasBundledGit ? preparedGitRuntime : Path.Combine(root, "runtime"),
        Path.Combine(root, "data"));
    Check(!services.AiSkills.IsProjectEnabled(project) &&
        !services.AiSkills.Discover(project).Skills.Any(skill => skill.Name == "project-validation"),
        "project Skill instructions are excluded before explicit enablement");
    if (hasBundledGit) await services.Git.InitializeAsync(project);
    await using var session = await StudioXMcpSession.CreateAsync(new StudioXMcpTools(services, project, authorizer));

    var definitions = await session.ListToolsAsync();
    var names = definitions.Select(tool => tool.Name).ToArray();
    Check(names.SequenceEqual(names.Order(StringComparer.Ordinal)) &&
        names.Contains("project_build") && names.Contains("project_build_log") && names.Contains("git_status") &&
        names.Contains("debug_status") && names.Contains("firmware_download_plan") &&
        names.Contains("firmware_download") && names.Contains("serial_send") &&
        names.Contains("serial_read_raw") &&
        names.Contains("plot_snapshot") && names.Contains("device_info") &&
        names.Contains("microchip_search_products") &&
        names.Contains("web_search") && names.Contains("web_fetch") &&
        names.Contains("skill_list") && names.Contains("skill_read") &&
        names.Contains("skill_read_reference"),
        "real MCP handshake exposes deterministic programming, build, download, Git, debug, serial, plot and device tools");

    var unsupportedDownload = await session.CallToolAsync("firmware_download_plan", "{}");
    Check(unsupportedDownload.Contains("error", StringComparison.OrdinalIgnoreCase) &&
        !authorizer.Requests.Any(request => request.Tool == "firmware_download_plan") &&
        !Directory.EnumerateDirectories(Path.Combine(project, ".build"), "download-*").Any(),
        "download plan refuses unsupported project without opening hardware");
    var malformedDownload = await session.CallToolAsync("firmware_download",
        "{\"deviceId\":\"STM32F407ZG\",\"imageSha256\":\"bad\",\"probeId\":\"stlink\",\"speedKhz\":1000}");
    Check(malformedDownload.Contains("error", StringComparison.OrdinalIgnoreCase) &&
        !authorizer.Requests.Any(request => request.Tool == "firmware_download"),
        "download rejects unpinned firmware before requesting hardware approval");
    await StcIspMcpChecks.RunUnsupportedProjectAsync(session, services, authorizer, project, Check);

    var untrustedSkillList = await session.CallToolAsync("skill_list", "{}");
    Check(!untrustedSkillList.Contains("project-validation", StringComparison.Ordinal) &&
        untrustedSkillList.Contains("\"projectSkillsEnabled\":false", StringComparison.Ordinal),
        "MCP Skill catalog excludes untrusted project instructions by default");
    services.AiSkills.SetProjectEnabled(project, true);
    var trustedSkillList = await session.CallToolAsync("skill_list", "{}");
    Check(services.AiSkills.IsProjectEnabled(project) &&
        trustedSkillList.Contains("project-validation", StringComparison.Ordinal) &&
        trustedSkillList.Contains("\"projectSkillsEnabled\":true", StringComparison.Ordinal) &&
        !trustedSkillList.Contains("# Project-only body", StringComparison.Ordinal),
        "MCP Skill catalog reports enabled project metadata without eager body loading");
    var skillBody = await session.CallToolAsync("skill_read", "{\"name\":\"project-validation\"}");
    var skillReference = await session.CallToolAsync("skill_read_reference",
        "{\"name\":\"project-validation\",\"path\":\"references/guide.md\"}");
    var rejectedReference = await session.CallToolAsync("skill_read_reference",
        "{\"name\":\"project-validation\",\"path\":\"references/../SKILL.md\"}");
    Check(skillBody.Contains("# Project-only body", StringComparison.Ordinal) &&
        skillReference.Contains("Project-only reference", StringComparison.Ordinal) &&
        rejectedReference.Contains("error", StringComparison.OrdinalIgnoreCase),
        "MCP Skill tools read requested body/reference and reject traversal");

    var serializedToolCount = 0;
    using (var http = new HttpClient(new StubHandler(async request =>
    {
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
        serializedToolCount = body.RootElement.GetProperty("tools").GetArrayLength();
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"ok\"}}]}",
                Encoding.UTF8, "application/json")
        };
    })))
    using (var chat = new AiChatClient(_ => "offline-test-key", http))
        _ = await chat.CompleteAsync(new AiSettings(BaseUrl: "https://example.test/v1"),
            new AiChatRequest([new AiChatMessage("user", "offline")], definitions));
    Check(serializedToolCount == names.Length,
        "AI provider request serializes the complete MCP tool catalog within limits");

    var info = await session.CallToolAsync("project_info", "{}");
    Check(info.Contains("mcp-test", StringComparison.Ordinal), "MCP client calls project tool through server");
    var log = await session.CallToolAsync("project_build_log", "{\"offsetBytes\":0,\"maxBytes\":100}");
    Check(log.Contains("compiler diagnostic: test", StringComparison.Ordinal),
        "build diagnostics can be read in bounded chunks");
    var read = await session.CallToolAsync("project_read_file", "{\"path\":\"src/main.c\"}");
    using var readJson = JsonDocument.Parse(read);
    var sha = readJson.RootElement.GetProperty("sha256").GetString()!;
    Check(readJson.RootElement.GetProperty("text").GetString()!.Contains("return 0"),
        "MCP read returns source and concurrency hash");

    var editArgs = JsonSerializer.Serialize(new
    {
        path = "src/main.c", originalSha256 = sha, content = "int main(void) { return 1; }\n"
    });
    var denied = await session.CallToolAsync("project_edit_file", editArgs);
    Check(denied.Contains("MCP_APPROVAL_DENIED", StringComparison.OrdinalIgnoreCase) ||
        denied.Contains("没有授权", StringComparison.Ordinal) || denied.Contains("denied", StringComparison.OrdinalIgnoreCase),
        "write tool is denied by default");
    Check((await File.ReadAllTextAsync(Path.Combine(project, "src", "main.c"))).Contains("return 0"),
        "denied edit leaves source unchanged");

    authorizer.Allow = true;
    var saved = await session.CallToolAsync("project_edit_file", editArgs);
    Check(saved.Contains("\"saved\":true", StringComparison.Ordinal) &&
        (await File.ReadAllTextAsync(Path.Combine(project, "src", "main.c"))).Contains("return 1"),
        "approved edit saves through project service");
    Check(authorizer.Requests.Any(request => request.Tool == "project_edit_file"),
        "authorization names the concrete action");

    if (hasBundledGit)
    {
        var status = await session.CallToolAsync("git_status", "{}");
        Check(status.Contains("src/main.c", StringComparison.Ordinal),
            "Git MCP status reads only the temporary project repository");
        var stage = await session.CallToolAsync("git_stage", "{\"action\":\"stage\",\"paths\":[\"src/main.c\"]}");
        Check(stage.Contains("\"completed\":true", StringComparison.Ordinal),
            "approved Git MCP stage changes the temporary repository");
        var diff = await session.CallToolAsync("git_diff", "{\"path\":\"src/main.c\",\"target\":\"staged\"}");
        Check(diff.Contains("return 1", StringComparison.Ordinal),
            "Git MCP reads staged source diff");
    }

    var escaped = await session.CallToolAsync("project_read_file", "{\"path\":\"../outside.c\"}");
    Check(escaped.Contains("error", StringComparison.OrdinalIgnoreCase) &&
        await File.ReadAllTextAsync(outside) == "untouched", "project path boundary rejects traversal");

    var started = await session.CallToolAsync("plot_start", "{\"demo\":true}");
    Check(started.Contains("\"simulated\":true", StringComparison.Ordinal),
        "plot demo uses an offline MCP session");
    var plot = await session.CallToolAsync("plot_snapshot", "{\"maxSamples\":5}");
    Check(plot.Contains("\"Simulated\":true", StringComparison.Ordinal),
        "MCP plot exposes bounded offline snapshot");
    _ = await session.CallToolAsync("plot_stop", "{}");

    await using (var hub = new DeviceHub())
    await using (var serial = new SerialTerminalService(hub, Path.Combine(root, "data"),
        _ => new SimulationTransport(TimeSpan.FromMilliseconds(5))))
    {
        await serial.ConnectAsync(new SerialSettings("COM1"));
        await Task.Delay(60);
        var raw = serial.ReadRaw(0, 128);
        Check(raw.Chunks.Count > 0 && raw.NextReceivedByteOffset > 0 &&
            Convert.FromBase64String(raw.Chunks[0].Base64).Length > 0,
            "serial raw RX preserves simulated binary frames and byte cursor");
        await serial.DisconnectAsync();
    }

    var fakeTransport = new McpAgentTransport();
    var agent = new AiAgentService(fakeTransport, new AiSettings(), mcpSession: session);
    var reply = await agent.SendAsync(project, "读取工程信息");
    Check(reply.Text == "工程已读取" && fakeTransport.Requests.Count == 2 &&
        fakeTransport.Requests[0].Tools!.Any(tool => tool.Name == "project_info") &&
        fakeTransport.Requests[0].Tools!.Any(tool => tool.Name == "project_edit_file") &&
        fakeTransport.Requests[0].Tools!.All(tool => tool.Name != "propose_file_edit") &&
        fakeTransport.Requests[0].Messages.Any(message => message.Content?.Contains(
            "project-validation", StringComparison.Ordinal) == true) &&
        !fakeTransport.Requests[0].Messages[0].Content!.Contains("project-validation", StringComparison.Ordinal) &&
        !fakeTransport.Requests[0].Messages.Any(message => message.Content?.Contains(
            "# Project-only body", StringComparison.Ordinal) == true) &&
        fakeTransport.Requests[1].Messages.Any(message => message.Role == "tool" &&
            message.Content!.Contains("mcp-test", StringComparison.Ordinal)),
        "built-in Agent injects Skill metadata and calls the same MCP tools");
    Check(reply.Usage == new AiTokenUsage(150, 20, 170) &&
        reply.History.Single().ProtocolMessages is { } protocol &&
        protocol.Any(message => message.Role == "assistant" && message.ReasoningContent == "先调用工具") &&
        protocol.Any(message => message.Role == "tool") &&
        fakeTransport.Requests[1].Messages.Any(message => message.Role == "assistant" &&
            message.ReasoningContent == "先调用工具" && message.ToolCalls?.Single().Id == "mcp-1"),
        "MCP Agent retains tool protocol, model reasoning, and the latest prompt usage");
    var continued = await agent.SendAsync(project, "继续", reply.History);
    Check(continued.Text == "继续完成" && fakeTransport.Requests[2].Messages.Any(message =>
            message.Role == "assistant" && message.ReasoningContent == "先调用工具" &&
            message.ToolCalls?.Single().Id == "mcp-1"),
        "later MCP Agent turns replay reasoning and tool protocol");

    var skillTransport = new SkillAgentTransport();
    var skillAgent = new AiAgentService(skillTransport, new AiSettings(), mcpSession: session);
    var skillReply = await skillAgent.SendAsync(project, "使用 project-validation 技能");
    Check(skillReply.Text == "技能已读取" && skillTransport.Requests.Count == 2 &&
        skillTransport.Requests[1].Messages.Any(message => message.Role == "tool" &&
            message.Content!.Contains("# Project-only body", StringComparison.Ordinal)),
        "built-in Agent reads selected Skill through the shared MCP path");

    services.AiSkills.SetProjectEnabled(project, false);
    var revokedList = await session.CallToolAsync("skill_list", "{}");
    var revokedRead = await session.CallToolAsync("skill_read", "{\"name\":\"project-validation\"}");
    Check(!revokedList.Contains("project-validation", StringComparison.Ordinal) &&
        revokedRead.Contains("error", StringComparison.OrdinalIgnoreCase),
        "revoking project Skill access takes effect in an existing MCP session");

    await ExternalProjectMcpChecks.RunAsync(session, services, authorizer, project, root, Check);
    await AgentExternalWorkloadChecks.RunAsync(session, authorizer, project, root, Check);
    await ExternalProjectPaginationChecks.RunAsync(services, authorizer, project, root, Check);
    await ProjectMcpPagingChecks.RunAsync(session, authorizer, project, Check);
    await DebugMcpAssistChecks.RunAsync(services, session, project, Check);
    await DebugMemoryPagingChecks.RunAsync(root, Check);
    await QmdMcpChecks.RunAsync(session, services, project, Check);
    await WebMcpChecks.RunAsync(session, services, project, Check);
    await PdfMcpChecks.RunAsync(session, authorizer, project, root, Check);
    await McpErrorPropagationChecks.RunAsync(services, project, Check);
    var cliExecutable = Path.GetFullPath("src/StudioX.Cli/bin/Debug/net10.0/StudioX.Cli.exe");
    if (File.Exists(cliExecutable))
        await McpErrorPropagationChecks.RunExternalAsync(cliExecutable, project,
            Path.Combine(root, "runtime"), Check);

    Console.WriteLine($"PASS {checks} offline MCP checks; no live device, network database or Git remote was called.");
}
finally
{
    // 清理本次运行独有的少量临时文件；绝不触碰已有用户目录。
    var full = Path.GetFullPath(root);
    var temp = Path.GetFullPath(Path.GetTempPath());
    var relativeToTemp = Path.GetRelativePath(temp, full);
    if (!relativeToTemp.Contains(Path.DirectorySeparatorChar) &&
        !relativeToTemp.Contains(Path.AltDirectorySeparatorChar) &&
        relativeToTemp.StartsWith("studiox-mcp-validation-", StringComparison.Ordinal) &&
        Directory.Exists(full))
    {
        foreach (var file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(full, recursive: true);
    }
}

sealed class SwitchingAuthorizer : IStudioXMcpAuthorizer
{
    public bool Allow { get; set; }
    public List<StudioXMcpApprovalRequest> Requests { get; } = [];
    public Task<bool> ApproveAsync(StudioXMcpApprovalRequest request, CancellationToken token)
    {
        Requests.Add(request);
        return Task.FromResult(Allow);
    }
}

sealed class McpAgentTransport : IAiAgentTransport
{
    public List<AiChatRequest> Requests { get; } = [];
    public Task<AiChatResponse> CompleteAsync(AiSettings settings, AiChatRequest request,
        CancellationToken token = default)
    {
        Requests.Add(request);
        return Task.FromResult(Requests.Count switch
        {
            1 => new AiChatResponse(null, [new AiToolCall("mcp-1", "project_info", "{}")],
                "tool_calls", null, new AiTokenUsage(100, 12, 112), "先调用工具"),
            2 => new AiChatResponse("工程已读取", [], "stop", null,
                new AiTokenUsage(150, 20, 170), "读取结果完成"),
            3 => new AiChatResponse("继续完成", [], "stop", null,
                new AiTokenUsage(180, 15, 195), "接着回答"),
            _ => throw new Exception("Unexpected MCP Agent request")
        });
    }
}

sealed class SkillAgentTransport : IAiAgentTransport
{
    public List<AiChatRequest> Requests { get; } = [];
    public Task<AiChatResponse> CompleteAsync(AiSettings settings, AiChatRequest request,
        CancellationToken token = default)
    {
        Requests.Add(request);
        return Task.FromResult(Requests.Count == 1
            ? new AiChatResponse(null, [new AiToolCall("skill-1", "skill_read", "{\"name\":\"project-validation\"}")],
                "tool_calls", null)
            : new AiChatResponse("技能已读取", [], "stop", null));
    }
}

sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        respond(request);
}
