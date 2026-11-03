using System.Text.Json;
using StudioX.Application;
using StudioX.Application.Editing;
using StudioX.Application.Mcp;
using StudioX.Engine;
using StudioX.Engine.Arduino;
using StudioX.Foundation;

/// <summary>真实 MCP 会话重现根目录 Arduino 草稿读取；不加载 SDK、不编译、不连接硬件。</summary>
internal static class WorkspaceSourceChecks
{
    public static async Task RunAsync(string output)
    {
        if (Directory.Exists(output)) { throw new IOException("Use a new evidence directory."); }
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        void Check(bool value, string message)
        {
            if (!value) { throw new InvalidOperationException(message); }
            checks.Add(message);
            Console.WriteLine("PASS " + message);
        }
        var project = Path.Combine(output, "ForestFireAlarm");
        Directory.CreateDirectory(Path.Combine(project, ".studiox"));
        var board = ArduinoBoard.Require("uno");
        await JsonStore.WriteAsync(Path.Combine(project, ".studiox/project.json"), new ProjectManifest(
            1, "ForestFireAlarm", "arduino.avr", "0.1.0", "offline", board.DeviceId, "arduino-sketch",
            board.Platform.ToolsetId, board.Platform.CoreVersion, board.Platform.CompilerId,
            Kind: ProjectKind.Arduino, EntryFile: "ForestFireAlarm.ino", Arduino: new("uno", [])));
        const string diskText = "void setup() {}\nvoid loop() { int smoke = 70; }\n";
        const string draftText = "void setup() {}\nvoid loop() { int smoke = 50; }\n// UNSAVED_THRESHOLD_MARKER\n";
        var sketch = Path.Combine(project, "ForestFireAlarm.ino");
        await File.WriteAllTextAsync(sketch, diskText);
        await using var services = new WorkbenchService(Path.Combine(output, "runtime"), Path.Combine(output, "data"));
        var source = await services.Files.ReadAsync(project, "ForestFireAlarm.ino");
        var host = new SourceEditor(new(source, draftText));
        var authorizer = new SwitchingAuthorizer();
        var editor = new AgentEditorSession(services, project, host, authorizer);
        await using var tools = new StudioXMcpTools(services, project, authorizer, includePlugins: false, agentEditor: editor);
        await using var session = await StudioXMcpSession.CreateAsync(tools);
        async Task<JsonElement> Call(string name, object arguments)
        {
            var result = await session.CallToolDetailedAsync(name, JsonSerializer.Serialize(arguments));
            if (IsError(result)) { throw new InvalidOperationException(name + ": " + result.Text); }
            using var parsed = JsonDocument.Parse(result.Text);
            return parsed.RootElement.Clone();
        }

        var info = await Call("project_info", new { });
        Check(info.GetProperty("Name").GetString() == "ForestFireAlarm" &&
            info.GetProperty("kind").GetString() == "Arduino", "fixture is a valid Arduino UNO project");
        foreach (var directory in new[] { "", ".", "./", "././" })
        {
            var listed = await Call("project_list_files", new { directory });
            Check(listed.GetProperty("entries").EnumerateArray().Any(entry => entry.GetProperty("path").GetString() == "ForestFireAlarm.ino"),
                "root listing includes sketch for directory '" + directory + "'");
        }
        foreach (var path in new[] { "ForestFireAlarm.ino", "./ForestFireAlarm.ino", "././ForestFireAlarm.ino", "./FORESTFIREALARM.INO" })
        {
            var disk = await Call("project_read_file", new { path });
            Check(disk.GetProperty("text").GetString() == diskText &&
                disk.GetProperty("sha256").GetString() == source.DiskHash,
                "project_read_file reads saved sketch and disk hash: " + path);
            var draft = await Call("editor_read", new { path });
            Check(draft.GetProperty("text").GetString() == draftText && draft.GetProperty("dirty").GetBoolean() &&
                draft.GetProperty("path").GetString() == "ForestFireAlarm.ino" &&
                draft.GetProperty("contentHash").GetString() == AgentEditorSession.Hash(draftText),
                "editor_read resolves the same unsaved buffer: " + path);
        }
        var context = await Call("editor_context", new { });
        Check(context.GetProperty("documents").GetArrayLength() == 1 &&
            context.GetProperty("selectedText").GetString() == "setup" &&
            context.GetProperty("documents")[0].GetProperty("dirty").GetBoolean(),
            "editor context includes active .ino draft and selection");
        var search = await Call("editor_search", new { query = "UNSAVED_THRESHOLD_MARKER", include = "*.ino" });
        Check(search.GetProperty("results").GetArrayLength() == 1 &&
            search.GetProperty("results")[0].GetProperty("path").GetString() == "ForestFireAlarm.ino",
            "editor search includes unsaved sketch matches");
        var diskSearch = await Call("project_search", new { query = "smoke", directory = "./" });
        Check(diskSearch.GetProperty("results").GetArrayLength() == 1, "project search discovers root sketch");
        var qmd = await Call("project_qmd_search", new { query = "smoke", directory = "." });
        Check(qmd.ToString().Contains("ForestFireAlarm.ino", StringComparison.Ordinal), "BM25 search indexes sketch using root alias");

        var supported = new[] { ".c", ".h", ".cc", ".cpp", ".cxx", ".hpp", ".hh", ".hxx", ".ino", ".s", ".asm", ".cmake", ".ld", ".lds", ".dts", ".dtsi", ".overlay", ".py", ".pyw", ".pyi", ".v", ".vh", ".ve", ".sv", ".svh", ".mcuconfig" };
        Directory.CreateDirectory(Path.Combine(project, "src"));
        foreach (var extension in supported)
        {
            var path = "src/example" + extension;
            await File.WriteAllTextAsync(Path.Combine(project, path), "// SOURCE_TYPE_MARKER\n");
            var read = await Call("project_read_file", new { path = "./" + path });
            var closed = await Call("editor_read", new { path = "./" + path });
            Check(read.GetProperty("text").GetString() == "// SOURCE_TYPE_MARKER\n" &&
                closed.GetProperty("text").GetString() == "// SOURCE_TYPE_MARKER\n" && !closed.GetProperty("dirty").GetBoolean(),
                "source listing/read policy supports " + extension);
        }
        var nested = await Call("project_list_files", new { directory = "./src" });
        Check(nested.GetProperty("entries").GetArrayLength() == supported.Length, "nested listing uses the same source types as reading");

        host.Buffer = host.Buffer with { Text = new string('x', 18000) + "PAGED_DRAFT_END" };
        var page = await Call("editor_read", new { path = "./ForestFireAlarm.ino", offset = 12000, maxChars = 16000 });
        Check(page.GetProperty("text").GetString() == host.Buffer.Text[12000..] && page.GetProperty("endOfFile").GetBoolean(),
            "large unsaved sketch retains bounded pagination");
        host.Buffer = host.Buffer with { Text = draftText };
        var plan = await Call("editor_plan_changes", new { files = new[] { new { path = "./ForestFireAlarm.ino", contentHash = AgentEditorSession.Hash(draftText), hunks = new[] { new { oldText = "smoke = 50", newText = "smoke = 51" } } } }, reason = "offline preview" });
        Check(plan.GetProperty("files")[0].GetProperty("path").GetString() == "ForestFireAlarm.ino" &&
            host.Buffer.Text == draftText && await File.ReadAllTextAsync(sketch) == diskText && authorizer.Requests.Count == 0,
            "read/search/preview need no build, save or approval; draft and disk remain unchanged");
        var denied = await session.CallToolDetailedAsync("editor_apply_plan", JsonSerializer.Serialize(new { planId = plan.GetProperty("id").GetString() }));
        Check(IsError(denied) && host.Buffer.Text == draftText && await File.ReadAllTextAsync(sketch) == diskText && authorizer.Requests.Count == 1,
            "draft edit still requires authorization and denied apply changes nothing");
        try
        {
            await editor.PlanAsync([
                new("ForestFireAlarm.ino", AgentEditorSession.Hash(draftText), [new("smoke = 50", "smoke = 51")]),
                new("./ForestFireAlarm.ino", AgentEditorSession.Hash(draftText), [new("smoke = 50", "smoke = 52")])], "duplicate aliases");
            throw new InvalidOperationException("Duplicate aliases were accepted.");
        }
        catch (StudioXException error) when (error.Code == "AGENT_PLAN_SIZE") { Check(true, "canonical aliases cannot bypass duplicate-file plan guards"); }

        await File.WriteAllTextAsync(Path.Combine(output, "outside.ino"), "OUTSIDE_MARKER");
        Directory.CreateDirectory(Path.Combine(project, "secrets"));
        await File.WriteAllTextAsync(Path.Combine(project, "secrets/board.ino"), "SECRET_MARKER");
        await File.WriteAllTextAsync(Path.Combine(project, "binary.ino"), "binary\0payload");
        var unsafePaths = new[] { "../outside.ino", "./../outside.ino", "./src/../ForestFireAlarm.ino", "./src/../../outside.ino", "C:/outside.ino", "/outside.ino", "src\\example.ino", "./secrets/board.ino", "./.studiox/project.json", "./.env", "./private.ino", "./ForestFireAlarm.ino:stream", "./unknown.exe", "./firmware.hex", "./binary.ino" };
        foreach (var path in unsafePaths)
        {
            foreach (var tool in new[] { "project_read_file", "editor_read" })
            {
                var rejected = await session.CallToolDetailedAsync(tool, JsonSerializer.Serialize(new { path }));
                Check(IsError(rejected) && !rejected.Text.Contains("OUTSIDE_MARKER", StringComparison.Ordinal) &&
                    !rejected.Text.Contains("SECRET_MARKER", StringComparison.Ordinal), tool + " rejects unsafe path: " + path);
            }
        }
        Directory.CreateDirectory(Path.Combine(project, "device"));
        await File.WriteAllTextAsync(Path.Combine(project, "device/board.hxx"), "// managed source\n");
        var device = await Call("project_read_file", new { path = "./device/board.hxx" });
        var deviceWrite = await session.CallToolDetailedAsync("project_patch_file", JsonSerializer.Serialize(new
        {
            path = "./device/board.hxx", originalSha256 = device.GetProperty("sha256").GetString(),
            hunks = new[] { new { oldText = "managed", newText = "changed" } }
        }));
        Check(device.GetProperty("readOnly").GetBoolean() && IsError(deviceWrite) &&
            await File.ReadAllTextAsync(Path.Combine(project, "device/board.hxx")) == "// managed source\n",
            "new source types and aliases preserve device read-only protection");
        await JsonStore.WriteAsync(Path.Combine(output, "result.json"), new { passed = true, hardware = false, paidApi = false, compilerInvoked = false, checks });
        Console.WriteLine($"PASS {checks.Count} MCP source-access checks; no SDK, compiler, hardware or paid API used.");
    }

    private static bool IsError(StudioXMcpToolResult result)
    {
        using var parsed = JsonDocument.Parse(result.Text);
        return parsed.RootElement.TryGetProperty("error", out _);
    }

    private sealed class SourceEditor(WorkspaceBufferSnapshot buffer) : IAgentEditorAccess
    {
        public WorkspaceBufferSnapshot Buffer { get; set; } = buffer;
        public Task<AgentEditorSnapshot> SnapshotAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(new AgentEditorSnapshot("ForestFireAlarm.ino", 13, 5, 5, [Buffer]));
        }
        public Task PresentAsync(AgentEditPlan plan, CancellationToken token) => Task.CompletedTask;
        public Task<IReadOnlyList<WorkspaceFileChange>> SelectedAsync(AgentEditPlan plan, CancellationToken token) => Task.FromResult(plan.Changes);
        public Task ApplyAsync(IReadOnlyList<WorkspaceFileChange> changes, CancellationToken token) => throw new InvalidOperationException("This read regression must not apply edits.");
        public Task SaveAsync(IReadOnlyList<WorkspaceBufferSnapshot> snapshots, CancellationToken token) => throw new InvalidOperationException("This read regression must not save drafts.");
    }
}
