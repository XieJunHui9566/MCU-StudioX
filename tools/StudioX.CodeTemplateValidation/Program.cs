using System.Text;
using StudioX.Application.Editing;
using StudioX.Foundation;

if (args.Length != 1) { throw new ArgumentException("A new evidence directory is required."); }
var root = Path.GetFullPath(args[0]);
if (Directory.Exists(root)) { throw new IOException("Use a new evidence directory."); }
Directory.CreateDirectory(root);
var checks = new List<string>();
void Check(bool success, string label) { if (!success) { throw new InvalidOperationException(label); } checks.Add(label); }
async Task Reject(Func<Task> action, string label)
{
    var rejected = false;
    try { await action(); }
    catch (Exception ex) when (ex is StudioXException or IOException or UnauthorizedAccessException or OperationCanceledException) { rejected = true; }
    Check(rejected, label);
}
Task Parse(string text) { CodeTemplateExpander.Describe(text); return Task.CompletedTask; }
var empty = new Dictionary<string, string>();
var context = new CodeTemplateContext("first();\nsecond();", "电机😀.c");
try
{
    var service = new CodeTemplateService(Path.Combine(root, "user"));
    var project = Path.Combine(root, "project"); Directory.CreateDirectory(project);
    var other = Path.Combine(root, "other"); Directory.CreateDirectory(other);
    var initial = await service.LoadAsync(project);
    Check(initial.Entries.Count == 6 && !File.Exists(initial.User.Path) && !File.Exists(initial.Project!.Path), "loading built-ins does not create or write template stores");
    foreach (var builtin in BuiltInCodeTemplates.All) { CodeTemplateExpander.Describe(builtin.Body); }
    Check(BuiltInCodeTemplates.All.Where(t => CodeTemplateService.Supports(t, "C")).Count() == 4 && !CodeTemplateService.Supports(BuiltInCodeTemplates.All[0], "Python"), "built-ins have explicit generic language applicability");
    Check((await service.CompleteAsync(project, "Python", "sxdef", 5)).Count == 1 && (await service.CompleteAsync(project, "Python", "# sxdef", 7)).Count == 0 && (await service.CompleteAsync(project, "Python", "\"\"\"sxdef", 8)).Count == 0, "Python templates respect comment and multiline string contexts");
    Check((await service.CompleteAsync(project, "CMake", "sxsources", 9)).Count == 1 && (await service.CompleteAsync(project, "CMake", "# sxsources", 11)).Count == 0 && (await service.CompleteAsync(project, "CMake", "#[=[sxsources", 13)).Count == 0, "CMake templates respect line and bracket comments");
    var template = new CodeTemplate(Guid.NewGuid().ToString("N"), "串口日志", "slog", "C/C++", "保存并填写重复参数", "print(${value:counter}, ${value});${cursor}");
    await service.SaveAsync(project, CodeTemplateScope.User, initial.User.Revision, [template]);
    var loaded = await service.LoadAsync(project);
    Check(loaded.User.Templates.Single() == template && loaded.Entries.Any(e => e.Scope == CodeTemplateScope.User), "personal templates round-trip including Unicode and variables");
    Check((await service.LoadAsync(other)).User.Templates.Single() == template, "personal templates are available across projects");
    var shared = template with { Id = Guid.NewGuid().ToString("N"), Name = "工程日志" };
    await service.SaveAsync(project, CodeTemplateScope.Project, loaded.Project!.Revision, [shared]);
    Check((await service.LoadAsync(other)).Project!.Templates.Count == 0 && (await service.LoadAsync(project)).Project!.Templates.Single() == shared, "project template store is isolated and shareable as plain JSON");
    await Reject(() => service.SaveAsync(project, CodeTemplateScope.User, initial.User.Revision, []), "stale saves refuse to overwrite another window's edits");
    Check((await service.LoadAsync(project)).User.Templates.Single() == template, "stale save preserves existing templates");
    await Reject(() => service.SaveAsync(null, CodeTemplateScope.Project, "missing", [template]), "project scope requires an open project");
    await Reject(() => service.SaveAsync(project, CodeTemplateScope.BuiltIn, "missing", [template]), "built-in templates cannot be overwritten");
    await Reject(() => service.SaveAsync(project, CodeTemplateScope.User, loaded.User.Revision, [template, template with { Id = Guid.NewGuid().ToString("N"), Shortcut = "SLOG" }]), "case-insensitive shortcut duplicates within a language are rejected");
    await Reject(() => service.SaveAsync(project, CodeTemplateScope.User, loaded.User.Revision, [template with { Id = "../outside" }]), "template identifiers cannot become paths");
    await Reject(() => service.SaveAsync(project, CodeTemplateScope.User, loaded.User.Revision, [template with { Body = new string('a', 65_537) }]), "template body limit is enforced");
    await Reject(() => service.SaveAsync(project, CodeTemplateScope.User, loaded.User.Revision, Enumerable.Range(0, 501).Select(i => template with { Id = Guid.NewGuid().ToString("N"), Shortcut = "log" + i }).ToArray()), "template count limit is enforced");
    await Reject(() => service.SaveAsync(project, CodeTemplateScope.User, loaded.User.Revision, Enumerable.Range(0, 80).Select(i => template with { Id = Guid.NewGuid().ToString("N"), Shortcut = "large" + i, Body = new string('x', 65_536) }).ToArray()), "serialized file size is bounded even when individual templates are valid");
    var bytesBefore = await File.ReadAllBytesAsync(loaded.User.Path);
    using (var cancel = new CancellationTokenSource())
    {
        cancel.Cancel();
        await Reject(() => service.SaveAsync(project, CodeTemplateScope.User, loaded.User.Revision, [], cancel.Token), "cancelled save does not replace the template file");
    }
    var bytesAfter = await File.ReadAllBytesAsync(loaded.User.Path);
    Check(bytesBefore.SequenceEqual(bytesAfter), "cancelled save retains exact original bytes");
    var revision = (await service.LoadAsync(project)).User.Revision;
    var outcomes = await Task.WhenAll(Enumerable.Range(0, 2).Select(i => Task.Run(async () =>
    {
        try { await new CodeTemplateService(Path.Combine(root, "user")).SaveAsync(project, CodeTemplateScope.User, revision, [template with { Name = "并发 " + i }]); return true; }
        catch (Exception ex) when (ex is IOException or StudioXException) { return false; }
    })));
    Check(outcomes.Count(v => v) == 1 && (await service.LoadAsync(project)).User.Templates.Count == 1, "concurrent writers produce exactly one committed update");
    var userFile = (await service.LoadAsync(project)).User.Path;
    var good = await File.ReadAllBytesAsync(userFile);
    foreach (var invalid in new[] { "{", "null", "{\"formatVersion\":2,\"templates\":[]}", "{\"formatVersion\":1,\"templates\":[null]}" })
    {
        await File.WriteAllTextAsync(userFile, invalid);
        await Reject(async () => { await service.LoadAsync(project); }, "invalid schema/JSON is visible: " + invalid);
        Check(await File.ReadAllTextAsync(userFile) == invalid, "invalid template file is not silently rewritten");
    }
    await File.WriteAllTextAsync(userFile, new string(' ', CodeTemplateService.MaximumFileBytes + 1));
    await Reject(async () => { await service.LoadAsync(project); }, "oversized JSON is rejected by bounded reads");
    await File.WriteAllBytesAsync(userFile, good);
    var described = CodeTemplateExpander.Describe("${name} ${name:motor} ${name}");
    Check(described.Single() == new CodeTemplateParameter("name", "motor"), "same-name fields share one default even if first reference precedes it");
    var expanded = CodeTemplateExpander.Expand("${value:counter}/${value}/${fileName}/${fileStem}/${cursor}$$${selection}", new Dictionary<string, string> { ["value"] = "${not_recursively_expanded}" }, context);
    Check(expanded.Text.StartsWith("${not_recursively_expanded}/${not_recursively_expanded}/电机😀.c/电机😀/", StringComparison.Ordinal) && expanded.Text[expanded.CaretOffset] == '$', "values remain literal and caret uses UTF-16 offsets");
    Check(CodeTemplateExpander.Expand("$${PROJECT_SOURCE_DIR} $$ \\ ", empty, context).Text == "${PROJECT_SOURCE_DIR} $ \\ ", "escaped dollars preserve CMake text and ordinary backslashes");
    var selected = CodeTemplateExpander.Expand("if (ok)\n{\n    ${selection}${cursor}\n}", empty, context);
    var indented = CodeTemplateExpander.PrepareInsertion(selected, "void f()\r\n{\r\n\t", 14);
    Check(indented.Text == "if (ok)\r\n\t{\r\n\t    first();\r\n\t    second();\r\n\t}" && indented.Text[..indented.CaretOffset].EndsWith("second();", StringComparison.Ordinal), "selection, tab indentation, CRLF and caret transform together");
    var lf = CodeTemplateExpander.PrepareInsertion(CodeTemplateExpander.Expand("😀\n${cursor}x\n", empty, context), "\n  ", 3);
    Check(lf.Text == "😀\n  x\n" && lf.CaretOffset == 5, "LF and supplementary Unicode preserve exact insertion offsets");
    var splitCrLf = CodeTemplateExpander.PrepareInsertion(CodeTemplateExpander.Expand("a\r${cursor}\nb", empty, context), "\n  ", 3);
    Check(splitCrLf.Text == "a\n  b" && splitCrLf.CaretOffset == 4, "caret between a CR/LF pair maps to the normalized line start");
    var capturedText = "    set(${value});\n        next();\n";
    var captured = CodeTemplateExpander.CaptureSelection(capturedText, 0, capturedText.Length);
    Check(CodeTemplateExpander.Expand(captured, empty, context).Text == "set(${value});\n    next();\n", "selection capture removes base indentation and escapes code dollars");
    Check(CodeTemplateExpander.CaptureSelection("", 0, 0) == "", "empty selection is valid at an empty document boundary");
    await Reject(() => Parse("${missing"), "unterminated variables are rejected");
    await Reject(() => Parse("${a:1}${a:2}"), "conflicting repeated defaults are rejected");
    await Reject(() => Parse("${a:}${a:2}"), "empty explicit defaults cannot silently change");
    await Reject(() => Parse("${cursor}${cursor}"), "multiple caret markers are rejected");
    await Reject(() => Parse("${selection:bad}"), "reserved fields cannot have defaults");
    await Reject(() => Parse(string.Join("", Enumerable.Range(0, 33).Select(i => "${v" + i + "}"))), "parameter count is bounded");
    await Reject(() => { CodeTemplateExpander.Expand("${a}", new Dictionary<string, string> { ["a"] = "line\nline" }, context); return Task.CompletedTask; }, "typed parameters cannot inject extra lines");
    await Reject(() => { CodeTemplateExpander.Expand("${selection}${selection}", empty, context with { Selection = new string('x', 200_000) }); return Task.CompletedTask; }, "expanded text growth is bounded");
    await JsonStore.WriteAsync(Path.Combine(root, "result.json"), new { success = true, hardware = false, checks });
    Console.WriteLine($"PASS {checks.Count} checks");
}
catch (Exception ex)
{
    await JsonStore.WriteAsync(Path.Combine(root, "result.json"), new { success = false, checks, diagnostic = ex.ToString() });
    throw;
}
