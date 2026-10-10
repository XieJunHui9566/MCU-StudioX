using StudioX.Application;
using StudioX.Application.BuildConfiguration;
using StudioX.Application.Editing;
using StudioX.Engine;
using StudioX.Foundation;

if (args.Length is < 1 or > 2) { throw new ArgumentException("Usage: <new-output> [real-compilation-inputs.json]"); }
var output = Path.GetFullPath(args[0]);
if (Directory.Exists(output)) { throw new IOException("Use a new output directory."); }
Directory.CreateDirectory(output);
var checks = new List<string>();
void Check(bool value, string message) { if (!value) { throw new InvalidOperationException(message); } checks.Add(message); Console.WriteLine("PASS " + message); }
async Task Reject(Func<Task> action, string code, string message)
{
    try
    {
        await action();
        throw new InvalidOperationException("Expected rejection: " + message);
    }
    catch (StudioXException error) when (error.Code == code) { Check(true, message); }
}
var files = new ProjectFileService();
var service = new SourceRegistrationService(files);
var manifest = new ProjectManifest(1, "source-fixture", "offline.pack", "1.0.0", "hash", "offline", "plain", "offline.tools", "1.0.0", "gcc");
var project = Path.Combine(output, "中文 工程");
async Task Write(string relative, string value)
{
    var path = PathBoundary.Resolve(project, relative);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    await File.WriteAllTextAsync(path, value);
}
await JsonStore.WriteAsync(Path.Combine(project, ".studiox/project.json"), manifest);
await Write("src/main.c", "int main(void) { return 0; }\n");
await Write("src/中文 空格.c", "int added;\n");
await Write("src/helper.cpp", "int helper;\n");
await Write("src/start.S", "/* assembly */\n");
const string native = "# retain target_sources(fake PRIVATE evil.c)\r\n#[=[ target_sources(fake PRIVATE evil.c) ]=]\r\nadd_executable(app src/main.c)\r\nadd_library(other STATIC src/helper.cpp)\r\ntarget_sources(app PUBLIC [=[src/start.S]=]) # public sources\r\n";
await Write("CMakeLists.txt", native);
var inventory = await service.DiscoverAsync(project);
Check(inventory.Configurations.SequenceEqual(["CMakeLists.txt"]) && inventory.Sources.Contains("src/start.S"), "fresh discovery finds user CMake and uppercase assembly with Unicode paths");
var context = await service.ReadAsync(project, "CMakeLists.txt", []);
Check(context.Targets.Select(target => target.Name).SequenceEqual(["app", "other"]) && context.Targets[0].Sources.SequenceEqual(["src/main.c", "src/start.S"]), "comments and bracket arguments preserve explicit target and PUBLIC source ownership");
var plan = service.Prepare(context, "app", [new(SourceRegistrationKind.Add, "src/中文 空格.c"), new(SourceRegistrationKind.Add, "src/helper.cpp")]);
Check(plan.Change.After.Contains("PRIVATE \"src/中文 空格.c\" \"src/helper.cpp\"", StringComparison.Ordinal) && plan.Change.After.Contains("add_library(other STATIC src/helper.cpp)", StringComparison.Ordinal) &&
    !plan.Change.After.Replace("\r\n", "", StringComparison.Ordinal).Contains('\n') && await File.ReadAllTextAsync(Path.Combine(project, "CMakeLists.txt")) == native, "preview preserves comments, CRLF and another target without disk writes");
await service.ValidateAsync(plan, []);
Check(true, "unchanged closed configuration and source paths revalidate");
await Reject(() => Task.FromResult(service.Prepare(context, "app", [new(SourceRegistrationKind.Add, "src/main.c"), new(SourceRegistrationKind.Remove, "src/main.c")])), "SOURCE_CMAKE_UNSUPPORTED", "contradictory operations on one path are rejected");
Check(!service.Prepare(context, "app", [new(SourceRegistrationKind.Add, "src/main.c")]).Change.CanApply, "already registered source is idempotent");
await Reject(() => Task.FromResult(service.Prepare(context, "missing", [new(SourceRegistrationKind.Add, "src/helper.cpp")])), "SOURCE_CMAKE_UNSUPPORTED", "unknown target cannot be guessed");
await Reject(() => Task.FromResult(service.Prepare(context, "app", [new(SourceRegistrationKind.Add, "../outside.c")])), "PATH_UNSAFE", "source registration refuses project traversal");
await Reject(() => Task.FromResult(service.Prepare(context, "app", [new(SourceRegistrationKind.Add, "device/generated.c")])), "SOURCE_PATH", "device-managed sources cannot be changed through registration");
await Reject(() => Task.FromResult(service.Prepare(context, "app", [new(SourceRegistrationKind.Add, "src/missing.c")])), "SOURCE_REGISTRATION_STALE", "missing new source cannot enter a compile list");
await Write("src/notes.h", "#pragma once\n");
await Reject(() => Task.FromResult(service.Prepare(context, "app", [new(SourceRegistrationKind.Add, "src/notes.h")])), "SOURCE_PATH", "header is not guessed to be a translation unit");
var dirty = new WorkspaceBufferSnapshot(context.Configuration.Source, native + "# original unsaved note\r\n");
context = await service.ReadAsync(project, "CMakeLists.txt", [dirty]);
plan = service.Prepare(context, "app", [new(SourceRegistrationKind.Add, "src/中文 空格.c")]);
Check(plan.Context.WasOpen && plan.Change.Before == dirty.Text && plan.Change.After.Contains("# original unsaved note", StringComparison.Ordinal), "preview starts from unsaved CMake contents rather than disk");
await service.ValidateAsync(plan, [dirty]);
await Reject(() => service.ValidateAsync(plan, [dirty with { Text = dirty.Text + "# later edit" }]), "WORKSPACE_EDIT_STALE", "later unsaved CMake edits invalidate apply");
await Reject(() => service.ValidateAsync(plan, []), "WORKSPACE_EDIT_STALE", "closing an originally open CMake buffer invalidates apply");
await Reject(() => service.ValidateAsync(plan with { Change = plan.Change with { Matches = [] } }, [dirty]), "SOURCE_REGISTRATION_STALE", "forged edit ranges cannot bypass the generated plan");
await Write("CMakeLists.txt", native + "# external\r\n");
await Reject(() => service.ValidateAsync(plan, [dirty]), "WORKSPACE_EDIT_STALE", "external disk change invalidates preview without overwriting dirty contents");
await Write("CMakeLists.txt", native);
context = await service.ReadAsync(project, "CMakeLists.txt", []);
plan = service.Prepare(context, "app", [new(SourceRegistrationKind.Add, "src/中文 空格.c")]);
File.Delete(Path.Combine(project, "src/中文 空格.c"));
await Reject(() => service.ValidateAsync(plan, []), "SOURCE_REGISTRATION_STALE", "source deleted after preview prevents registration");
await Write("src/中文 空格.c", "int added;\n");
await JsonStore.WriteAsync(Path.Combine(project, ".studiox/project.json"), manifest with { Name = "changed" });
await Reject(() => service.ValidateAsync(plan, []), "SOURCE_REGISTRATION_STALE", "project identity changes invalidate preview");
await JsonStore.WriteAsync(Path.Combine(project, ".studiox/project.json"), manifest);
File.SetAttributes(Path.Combine(project, "CMakeLists.txt"), FileAttributes.ReadOnly);
await Reject(() => service.ReadAsync(project, "CMakeLists.txt", []), "SOURCE_READ_ONLY", "read-only CMake refuses registration");
File.SetAttributes(Path.Combine(project, "CMakeLists.txt"), FileAttributes.Normal);
foreach (var complex in new[] { "add_executable(app ${SOURCES})", "if(WIN32)\nadd_executable(app src/main.c)\nendif()", "macro(make_app)\nadd_executable(app src/main.c)\nendmacro()", "add_executable(app src/main.c)\ntarget_sources(app PRIVATE FILE_SET HEADERS FILES src/notes.h)", "add_executable(app src/main.c)\ntarget_sources(app INTERFACE src/helper.cpp)", "add_executable(app src/main.c)\nset_property(TARGET app APPEND PROPERTY SOURCES src/helper.cpp)", "add_executable(${NAME} src/main.c)", "add_executable(app \"src/\\main.c\")", "function(target_sources)\nendfunction()\nadd_executable(app src/main.c)" })
{
    await Write("CMakeLists.txt", complex + "\n");
    await Reject(() => service.ReadAsync(project, "CMakeLists.txt", []), "SOURCE_CMAKE_UNSUPPORTED", "complex CMake refuses automatic editing: " + complex.Split('\n')[0]);
}
await Write("CMakeLists.txt", "add_executable(app src/main.c\n");
await Reject(() => service.ReadAsync(project, "CMakeLists.txt", []), "SOURCE_CMAKE_SYNTAX", "unclosed command gives an original syntax diagnostic");
await Write("CMakeLists.txt", "add_executable(app \"src/main.c\"\"src/helper.cpp\")\n");
await Reject(() => service.ReadAsync(project, "CMakeLists.txt", []), "SOURCE_CMAKE_SYNTAX", "ambiguous adjacent arguments are not silently reinterpreted");
await Write("CMakeLists.txt", "add_executable(app \"src/main.c;src/helper.cpp\")\n");
File.Delete(Path.Combine(project, "src/helper.cpp"));
context = await service.ReadAsync(project, "CMakeLists.txt", []);
plan = service.Prepare(context, "app", [new(SourceRegistrationKind.Remove, "src/helper.cpp")]);
Check(plan.Change.After == "add_executable(app \"src/main.c\")\n", "removing one semicolon-list item retains the other source");
await Write("src/helper.cpp", "int helper;\n");
await Write("CMakeLists.txt", "add_executable(app src/main.c src/helper.cpp)\n");
File.Move(Path.Combine(project, "src/helper.cpp"), Path.Combine(project, "src/renamed.cpp"));
context = await service.ReadAsync(project, "CMakeLists.txt", []);
inventory = await service.DiscoverAsync(project);
var suggestions = service.Suggest(context, "app", inventory.Sources, [new("src/renamed.cpp", ProjectFileChangeKind.Renamed, "src/helper.cpp")]);
Check(suggestions.Contains(new(SourceRegistrationKind.Rename, "src/helper.cpp", "src/renamed.cpp")) && !suggestions.Any(item => item.Kind == SourceRegistrationKind.Add && item.Path == "src/renamed.cpp"), "observed rename yields one update instead of unrelated delete/add");
File.Move(Path.Combine(project, "src/renamed.cpp"), Path.Combine(project, "src/final.cpp"));
suggestions = service.Suggest(context, "app", (await service.DiscoverAsync(project)).Sources, [new("src/renamed.cpp", ProjectFileChangeKind.Renamed, "src/helper.cpp"), new("src/final.cpp", ProjectFileChangeKind.Renamed, "src/renamed.cpp")]);
Check(suggestions.Contains(new(SourceRegistrationKind.Rename, "src/helper.cpp", "src/final.cpp")), "rename chains retain the original registered path");
await Write("src/helper.cpp", "int copy;\n");
await Reject(() => Task.FromResult(service.Prepare(context, "app", [new(SourceRegistrationKind.Rename, "src/helper.cpp", "src/final.cpp")])), "SOURCE_CMAKE_UNSUPPORTED", "copy cannot impersonate a rename while original exists");
File.Delete(Path.Combine(project, "src/helper.cpp"));
suggestions = service.Suggest(context, "app", (await service.DiscoverAsync(project)).Sources, []);
Check(suggestions.Contains(new(SourceRegistrationKind.Remove, "src/helper.cpp")) && suggestions.Contains(new(SourceRegistrationKind.Add, "src/final.cpp")), "lost rename evidence exposes separate removal and addition for explicit review");
await Write("CMakeLists.txt", "add_executable(app src/main.c)\n");
File.Move(Path.Combine(project, "src/main.c"), Path.Combine(project, "src/Main.c"));
context = await service.ReadAsync(project, "CMakeLists.txt", []);
Check(service.Suggest(context, "app", (await service.DiscoverAsync(project)).Sources, [new("src/Main.c", ProjectFileChangeKind.Renamed, "src/main.c")]).Contains(new(SourceRegistrationKind.Rename, "src/main.c", "src/Main.c")), "case-only rename updates the literal spelling");
await Write("CMakeLists.txt", "add_executable(app src/final.cpp)\n");
Directory.Move(Path.Combine(project, "src"), Path.Combine(project, "moved"));
context = await service.ReadAsync(project, "CMakeLists.txt", []);
Check(service.Suggest(context, "app", (await service.DiscoverAsync(project)).Sources, [new("moved", ProjectFileChangeKind.Renamed, "src")]).Contains(new(SourceRegistrationKind.Rename, "src/final.cpp", "moved/final.cpp")), "directory move remaps registered leaf source paths");
await Write("build/ignored.c", "int ignored;\n");
await Write("device/sdk/ignored.c", "int ignored;\n");
await Write("managed_components/vendor/ignored.c", "int ignored;\n");
Check(!(await service.DiscoverAsync(project)).Sources.Any(path => path.Contains("ignored", StringComparison.Ordinal)), "generated products, SDK and managed components are excluded from discovery");
await JsonStore.WriteAsync(Path.Combine(project, ".studiox/project.json"), manifest with { ToolsetId = "espressif.idf", ToolsetVersion = "6.1.0", CompilerId = "esp-idf", Espressif = new("esp-idf", "esp32c3", "6.1.0") });
await Write("main/main.c", "void app_main(void) {}\n");
await Write("main/added.c", "int added;\n");
const string idf = "# user dependencies\nidf_component_register(SRCS \"main.c\" INCLUDE_DIRS \".\" PRIV_REQUIRES freertos esp_system)\n";
await Write("main/CMakeLists.txt", idf);
context = await service.ReadAsync(project, "main/CMakeLists.txt", []);
plan = service.Prepare(context, "当前组件", [new(SourceRegistrationKind.Add, "main/added.c")]);
Check(plan.Change.After.Contains("\"added.c\"", StringComparison.Ordinal) && plan.Change.After.Contains("PRIV_REQUIRES freertos esp_system", StringComparison.Ordinal) && plan.Change.After.StartsWith("# user dependencies", StringComparison.Ordinal), "IDF SRCS insertion preserves component dependencies and comments");
foreach (var value in new[] { "idf_component_register(INCLUDE_DIRS \".\")\n", "idf_component_register(SRCS INCLUDE_DIRS \".\")\n" })
{
    await Write("main/CMakeLists.txt", value);
    context = await service.ReadAsync(project, "main/CMakeLists.txt", []);
    plan = service.Prepare(context, "当前组件", [new(SourceRegistrationKind.Add, "main/added.c")]);
    Check(plan.Change.After.Split("SRCS", StringSplitOptions.None).Length == 2, "empty or absent IDF SRCS becomes exactly one source section");
}
foreach (var value in new[] { "idf_component_register(SRC_DIRS \".\")", "idf_component_register(SRCS main.c EXCLUDE_SRCS added.c)", "idf_component_register(SRCS ${SOURCES})", "if(CONFIG_FEATURE)\nidf_component_register(SRCS main.c)\nendif()", "idf_component_register(SRCS main.c SRCS added.c)" })
{
    await Write("main/CMakeLists.txt", value + "\n");
    await Reject(() => service.ReadAsync(project, "main/CMakeLists.txt", []), "SOURCE_CMAKE_UNSUPPORTED", "IDF directory rules, expressions, conditions and duplicate sections remain manual: " + value.Split('\n')[0]);
}
await Write("main/CMakeLists.txt", idf);
await Write("components/other/CMakeLists.txt", "idf_component_register(SRCS moved.c)\n");
File.Move(Path.Combine(project, "main/main.c"), Path.Combine(project, "components/other/moved.c"));
context = await service.ReadAsync(project, "main/CMakeLists.txt", []);
suggestions = service.Suggest(context, "当前组件", (await service.DiscoverAsync(project)).Sources, [new("components/other/moved.c", ProjectFileChangeKind.Renamed, "main/main.c")]);
Check(suggestions.Contains(new(SourceRegistrationKind.Remove, "main/main.c")) && !suggestions.Any(item => item.NewPath == "components/other/moved.c" || item.Path == "components/other/moved.c"), "cross-component move removes old registration without assigning another component's source");
await Reject(() => Task.FromResult(service.Prepare(context, "当前组件", [new(SourceRegistrationKind.Add, "components/other/moved.c")])), "SOURCE_CMAKE_UNSUPPORTED", "IDF registration refuses a different component");
await Write("main/nested/CMakeLists.txt", "idf_component_register(SRCS child.c)\n");
await Write("main/nested/child.c", "int child;\n");
Check(!service.Suggest(context, "当前组件", (await service.DiscoverAsync(project)).Sources, []).Any(item => item.Path == "main/nested/child.c"), "nested component source is not suggested to its parent");
using (var cancelled = new CancellationTokenSource()) { cancelled.Cancel(); try { await service.DiscoverAsync(project, cancelled.Token); throw new InvalidOperationException("Cancellation ignored."); } catch (OperationCanceledException) { Check(true, "discovery cancellation exits without writes"); } }
var realCompilation = "not_requested";
if (args.Length == 2) { await RealSourceRegistrationChecks.RunAsync(Path.Combine(output, "real-compilation"), Path.GetFullPath(args[1]), service, files, Check); realCompilation = "passed"; }
await JsonStore.WriteAsync(Path.Combine(output, "result.json"), new { success = true, checks, realCompilation, hardware = false });
