using System.Text.Json;
using StudioX.Application;
using StudioX.Application.Editing;
using StudioX.Application.Mcp;
using StudioX.Foundation;

if (args is not [var runtimeArg, var fixtureArg, var outputArg]) { throw new ArgumentException("runtime, fixture, new output required"); }
var runtime = Path.GetFullPath(runtimeArg);
var fixture = Path.GetFullPath(fixtureArg);
var root = Path.GetFullPath(outputArg);
if (Directory.Exists(root)) { throw new IOException("Use a new directory"); }
Directory.CreateDirectory(root);
var checks = new List<string>();
void Check(bool condition, string name) { if (!condition) { throw new InvalidOperationException(name); } checks.Add(name); Console.WriteLine("PASS " + name); }
async Task Reject(Func<Task> action, string name)
{
    try
    {
        await action();
    }
    catch (Exception e) when (e is StudioXException or IOException or OperationCanceledException) { Check(true, name); return; }
    throw new InvalidOperationException("Expected rejection: " + name);
}
foreach (var permission in Enum.GetValues<StudioXMcpPermission>())
{
    Check(!AgentAccessService.Allows(AgentAccessMode.Review, permission), "review asks for " + permission);
    Check(AgentAccessService.Allows(AgentAccessMode.FullAccess, permission), "full access automatically allows " + permission);
    Check(AgentAccessService.Allows(AgentAccessMode.FullAuthorization, permission) == (permission is StudioXMcpPermission.FileWrite or StudioXMcpPermission.Build or StudioXMcpPermission.GitWrite), "project authorization classification: " + permission);
}
Check(!AgentAccessService.Allows(AgentAccessMode.FullAccess, (StudioXMcpPermission)999), "unknown permissions are not silently authorized");
var project = Path.Combine(root, "project");
foreach (var name in new[] { ".studiox/project.json", "device/manifest.json", "src/main.c", "src/other.c", "src/meter.cpp", "include/shared.h" })
{
    var target = Path.Combine(project, name);
    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
    File.Copy(Path.Combine(fixture, name), target);
}
var access = new AgentAccessService(Path.Combine(root, "data"));
Check(await access.LoadAsync(project) == AgentAccessMode.Review, "unselected project starts in review mode");
await access.SaveAsync(project, AgentAccessMode.FullAccess);
Check(await new AgentAccessService(Path.Combine(root, "data")).LoadAsync(project.ToUpperInvariant()) == AgentAccessMode.FullAccess, "access selection persists across restart and path casing");
Check(await access.LoadAsync(project + "-other") == AgentAccessMode.Review, "another project does not inherit full access");
await using var services = new WorkbenchService(runtime, Path.Combine(root, "data"));
var editor = new MemoryEditor(services, project);
var source = await services.Files.ReadAsync(project, "src/main.c");
var baseline = source.Text;
editor.Documents[source.RelativePath] = new(source, baseline + "// pre-existing user draft\n");
var auth = new ModeAuthorizer();
var session = new AgentEditorSession(services, project, editor, auth);
session.BeginTask("test");
Check((await session.ReadAsync("src/main.c")).Text.EndsWith("// pre-existing user draft\n", StringComparison.Ordinal), "read prefers unsaved user buffer");
var first = await session.PlanAsync([new("src/main.c", AgentEditorSession.Hash(editor.Documents[source.RelativePath].Text), [new("= 1;", "= 2;"), new("helper();", "helper() + 1;")])], "two hunks");
Check(editor.Documents[source.RelativePath].Text.Contains("= 1;"), "planning does not apply edits");
editor.Selection = p => p.Changes.Select(c => c with { Matches = c.Matches.Take(1).ToArray(), After = WorkspaceEditService.ApplyText(c.Before, c.Matches.Take(1).ToArray()) }).ToArray();
await session.ApplyAsync(first.Id);
Check(first.Status == "applied" && editor.Documents[source.RelativePath].Text.Contains("= 2;") && !editor.Documents[source.RelativePath].Text.Contains("helper() + 1"), "only user-selected hunks apply");
Check(await File.ReadAllTextAsync(Path.Combine(project, source.RelativePath)) == baseline, "applied plan leaves disk unchanged");
editor.Selection = null;
editor.Documents[source.RelativePath] = editor.Documents[source.RelativePath] with { Text = editor.Documents[source.RelativePath].Text + "// later user text\n" };
await session.UndoAsync(first.Id);
Check(editor.Documents[source.RelativePath].Text.Contains("= 1;") && editor.Documents[source.RelativePath].Text.EndsWith("// later user text\n"), "undo preserves pre-existing draft and later independent user edits");
var stale = await session.PlanAsync([new("src/main.c", AgentEditorSession.Hash(editor.Documents[source.RelativePath].Text), [new("= 1;", "= 3;")])], "stale plan");
editor.Documents[source.RelativePath] = editor.Documents[source.RelativePath] with { Text = editor.Documents[source.RelativePath].Text + "// raced\n" };
await Reject(() => session.ApplyAsync(stale.Id), "racing user edit rejects stale plan without overwriting");
var denied = await session.PlanAsync([new("src/main.c", AgentEditorSession.Hash(editor.Documents[source.RelativePath].Text), [new("= 1;", "= 4;")])], "deny");
auth.Mode = AgentAccessMode.Review;
await Reject(() => session.ApplyAsync(denied.Id), "review denial does not mutate buffer");
auth.Mode = AgentAccessMode.FullAuthorization;
var conflict = await session.PlanAsync([new("src/main.c", AgentEditorSession.Hash(editor.Documents[source.RelativePath].Text), [new("= 1;", "= 5;")])], "undo conflict");
await session.ApplyAsync(conflict.Id);
editor.Documents[source.RelativePath] = editor.Documents[source.RelativePath] with { Text = editor.Documents[source.RelativePath].Text.Replace("= 5;", "= 99;") };
await Reject(() => session.UndoAsync(conflict.Id), "undo refuses to replace user edits inside the changed region");
await Reject(() => session.ReadAsync("../outside.c"), "full mode cannot escape the bound project through editor paths");
using (var cancelled = new CancellationTokenSource()) { cancelled.Cancel(); await Reject(() => session.SaveForBuildAsync(cancelled.Token), "cancelled task cannot save before build"); }
editor.BeforeSave = () => editor.Documents[source.RelativePath] = editor.Documents[source.RelativePath] with { Text = editor.Documents[source.RelativePath].Text + "// saving race\n" };
await Reject(() => session.SaveForBuildAsync(), "saving stale approval snapshot is rejected");
editor.BeforeSave = null;
editor.Documents[source.RelativePath] = new(source, baseline);
session.BeginTask("multi-plan undo");
foreach (var replacement in new[] { ("= 1;", "= 2;"), ("= 2;", "= 3;") })
{
    var plan = await session.PlanAsync([new("src/main.c", AgentEditorSession.Hash(editor.Documents[source.RelativePath].Text), [new(replacement.Item1, replacement.Item2)])], "sequential changes");
    await session.ApplyAsync(plan.Id);
}
editor.Documents[source.RelativePath] = editor.Documents[source.RelativePath] with { Text = editor.Documents[source.RelativePath].Text + "// retained after whole task undo\n" };
await session.UndoTaskAsync(session.TaskId);
Check(editor.Documents[source.RelativePath].Text == baseline + "// retained after whole task undo\n", "whole task undo reverses overlapping sequential plans and retains later independent edits");
editor.Documents[source.RelativePath] = new(source, baseline);
await services.Intelligence.StartAsync(project);
await using var tools = new StudioXMcpTools(services, project, auth, () => Task.FromResult(editor.Documents.Values.Any(d => d.Text != d.Source.Text)), includePlugins: false, agentEditor: session);
await using var mcp = await StudioXMcpSession.CreateAsync(tools);
async Task<JsonElement> Call(string name, object arguments)
{
    var result = await mcp.CallToolDetailedAsync(name, JsonSerializer.Serialize(arguments, JsonStore.Options));
    await File.WriteAllTextAsync(Path.Combine(root, name + ".json"), result.Text);
    return JsonDocument.Parse(result.Text).RootElement.Clone();
}
Check((await mcp.ListToolsAsync()).Any(t => t.Name == "editor_plan_refactor"), "real MCP exposes editor tools only when editor access is attached");
var context = await Call("editor_context", new { });
Check(context.GetProperty("activePath").GetString() == "src/main.c", "MCP returns active document and selection metadata");
await File.WriteAllTextAsync(Path.Combine(project, "main.py"), "print('draft')\n");
var python = await services.Files.ReadAsync(project, "main.py");
editor.Documents[python.RelativePath] = new(python, "print('unsaved')\n");
Check((await Call("editor_read", new { path = "main.py" })).GetProperty("text").GetString() == "print('unsaved')\n", "Python editor reads include unsaved text");
await File.WriteAllTextAsync(Path.Combine(project, "private.h"), "int secret_test_marker;\n");
var privateSource = await services.Files.ReadAsync(project, "private.h");
editor.Documents[privateSource.RelativePath] = new(privateSource, privateSource.Text);
Check(!(await Call("editor_context", new { })).GetProperty("documents").EnumerateArray().Any(d => d.GetProperty("path").GetString() == "private.h"), "editor context preserves protected-path filtering");
Check((await Call("editor_search", new { query = "secret_test_marker" })).GetProperty("results").GetArrayLength() == 0, "buffer search does not expose protected paths");
editor.Documents.Remove(privateSource.RelativePath);
editor.Documents.Remove(python.RelativePath);
var references = await Call("editor_inspect", new { path = "src/main.c", action = "references", offset = baseline.IndexOf("shared_value", StringComparison.Ordinal) });
Check(references.GetArrayLength() >= 3, "MCP uses real clangd references across files");
var rename = await Call("editor_plan_refactor", new { path = "src/main.c", action = "rename", offset = baseline.IndexOf("shared_value", StringComparison.Ordinal), oldName = "shared_value", newName = "shared_counter" });
Check(rename.GetProperty("files").GetArrayLength() == 3, "semantic rename stages a three-file plan");
editor.Selection = p => p.Changes.Take(1).ToArray();
await Reject(() => session.ApplyAsync(rename.GetProperty("id").GetString()!), "semantic rename rejects partial application in the core service");
editor.Selection = null;
var otherPath = Path.Combine(project, "src/other.c");
var otherOriginal = await File.ReadAllTextAsync(otherPath);
await File.WriteAllTextAsync(otherPath, otherOriginal + "// external race\n");
await Reject(() => session.ApplyAsync(rename.GetProperty("id").GetString()!), "external change in second file rejects all files atomically");
Check(editor.Documents["src/main.c"].Text == baseline, "first file remains untouched after later-file conflict");
await File.WriteAllTextAsync(otherPath, otherOriginal);
await Call("editor_apply_plan", new { planId = rename.GetProperty("id").GetString() });
Check(editor.Documents["src/other.c"].Text.Contains("int shared_value = 7;") && editor.Documents["src/main.c"].Text.Contains("shared_counter"), "semantic rename preserves unrelated local shadow");
await session.UndoAsync(rename.GetProperty("id").GetString()!);
Check(editor.Documents["src/main.c"].Text == baseline, "whole semantic plan can be undone");
await File.WriteAllTextAsync(Path.Combine(project, "CMakeLists.txt"), """
cmake_minimum_required(VERSION 3.20)
set(CMAKE_SYSTEM_NAME Generic)
set(CMAKE_TRY_COMPILE_TARGET_TYPE STATIC_LIBRARY)
project(agent_fixture C CXX ASM)
add_executable(firmware src/main.c src/other.c)
target_include_directories(firmware PRIVATE include)
target_compile_options(firmware PRIVATE -mcpu=cortex-m3 -mthumb -ffreestanding)
target_link_options(firmware PRIVATE -mcpu=cortex-m3 -mthumb -nostdlib -Wl,-e,main -Wl,-Map=${CMAKE_BINARY_DIR}/firmware.map)
set_target_properties(firmware PROPERTIES SUFFIX ".elf")
add_custom_command(TARGET firmware POST_BUILD COMMAND ${CMAKE_OBJCOPY} -O binary $<TARGET_FILE:firmware> ${CMAKE_BINARY_DIR}/firmware.bin)
add_custom_command(TARGET firmware POST_BUILD COMMAND ${CMAKE_OBJCOPY} -O ihex $<TARGET_FILE:firmware> ${CMAKE_BINARY_DIR}/firmware.hex)
""");
session.BeginTask("actual MCP edit build repair workflow");
var transport = new WorkflowTransport();
var promptsBefore = auth.Prompts;
var reply = await new AiAgentService(transport, new(), mcp).SendAsync(project, "修复并编译验证当前工程");
await File.WriteAllTextAsync(Path.Combine(root, "workflow-answer.txt"), reply.Text);
Check(transport.SawEditorInstructions, "agent receives editor-aware workflow instructions");
Check(transport.SawBuildFailure && transport.SawBuildSuccess, "actual GCC build fails, clangd repair applies, second build succeeds");
Check(auth.Prompts == promptsBefore, "full authorization workflow does not ask for each edit/save/build");
Check(session.ValidationStatus == "编译通过 · 尚未实板验证", "task report separates compiled result from hardware evidence");
Check(File.Exists(Path.Combine(project, ".build", "firmware.bin")), "actual isolated compiled artifact exists");
session.InvalidateValidation();
Check(session.ValidationStatus.Contains("过期"), "later user changes invalidate previous build evidence");
await File.WriteAllTextAsync(Path.Combine(root, "result.txt"), "PASS " + checks.Count + "\n" + string.Join('\n', checks));
Console.WriteLine("PASS " + checks.Count + "; scripted model, real MCP/clangd/GCC; no network or hardware");
