using System.Text;
using StudioX.Application;
using StudioX.Application.Editing;
using StudioX.Application.PeripheralDevelopment;
using StudioX.Foundation;

internal static class PeripheralAdditionChecks
{
    public static async Task RunAsync(PeripheralDevelopmentService service, PeripheralDevelopmentContext context,
        PeripheralCodePreview preview, Action<bool, string> check)
    {
        var root = context.ProjectDirectory;
        var files = new ProjectFileService();
        const string sourcePath = "main/nested/app.c";
        const string cmakePath = "main/CMakeLists.txt";
        var sourceFile = PathBoundary.Resolve(root, sourcePath);
        var cmakeFile = PathBoundary.Resolve(root, cmakePath);
        Directory.CreateDirectory(Path.GetDirectoryName(sourceFile)!);
        const string originalSource = "// user code\r\nvoid app_main(void) {}\r\n";
        await File.WriteAllTextAsync(sourceFile, originalSource, new UTF8Encoding(true));
        async Task<PeripheralProjectAddition> Prepare(string cmake)
        {
            await File.WriteAllTextAsync(cmakeFile, cmake, new UTF8Encoding(true));
            var component = await service.ReadComponentAsync(context, sourcePath, []);
            return service.PrepareAddition(context, component, preview);
        }
        async Task Reject(Func<Task> operation, string code, string label)
        {
            try
            {
                await operation();
                throw new InvalidOperationException("Expected rejection: " + label);
            }
            catch (StudioXException ex) when (ex.Code == code) { check(true, label); }
        }

        const string originalCMake = "# original user comment\r\nidf_component_register(SRCS \"nested/app.c\"\r\n INCLUDE_DIRS \".\" PRIV_REQUIRES freertos esp_system)\r\n# keep this tail\r\n";
        var addition = await Prepare(originalCMake);
        check(addition.Changes.Count == 2 && addition.AddedComponents.SequenceEqual(preview.RequiredComponents), "addition plans source and missing dependencies together");
        check(addition.Files[1].After.Replace(" esp_driver_gpio", "", StringComparison.Ordinal) == originalCMake,
            "dependency patch preserves existing clauses, order and comments byte for byte");
        check(addition.Files[0].After.EndsWith(originalSource, StringComparison.Ordinal) &&
            !addition.Files[0].After.Replace("\r\n", "", StringComparison.Ordinal).Contains('\n'), "source insertion preserves user text and CRLF");
        check(await File.ReadAllTextAsync(sourceFile) == originalSource && await File.ReadAllTextAsync(cmakeFile) == originalCMake,
            "addition preview never writes source or CMake");
        await service.ValidateAdditionAsync(addition, []);
        check(true, "matching source, CMake and SDK snapshots revalidate");
        var workspace = new WorkspaceEditService(files);
        await workspace.ValidateAsync(root, addition.Changes, []);
        check(true, "addition uses valid reversible workspace edits");
        foreach (var change in addition.Changes)
        {
            check(WorkspaceEditService.ApplyText(change.Before, change.Matches) == change.After, "planned edits reproduce " + change.Path);
        }
        check((await files.ReadAsync(root, cmakePath)).Encoding.GetPreamble().Length == 3, "BOM encoding survives snapshot capture");

        var publicExisting = await Prepare("idf_component_register(SRCS nested/app.c REQUIRES esp_driver_gpio PRIV_REQUIRES freertos)\n");
        check(publicExisting.Changes.Count == 1 && publicExisting.AddedComponents.Count == 0 && publicExisting.Files[1].Before == publicExisting.Files[1].After,
            "existing public dependency is retained without duplicate private registration");
        var noDependencies = await Prepare("#[=[ idf_component_register(SRCS fake.c) ]=]\nidf_component_register(SRCS [[nested/app.c]] INCLUDE_DIRS \".\") # tail\n");
        check(noDependencies.Files[1].After.Contains("PRIV_REQUIRES esp_driver_gpio", StringComparison.Ordinal) &&
            noDependencies.Files[1].After.EndsWith(") # tail\n", StringComparison.Ordinal), "bracket strings and fake registrations in comments preserve syntax");
        var emptyPrivate = await Prepare("idf_component_register(SRCS nested/app.c PRIV_REQUIRES INCLUDE_DIRS .)\n");
        check(emptyPrivate.Files[1].After.Contains("PRIV_REQUIRES esp_driver_gpio INCLUDE_DIRS", StringComparison.Ordinal), "empty dependency clause is filled without consuming the next keyword");
        var lists = await Prepare("idf_component_register(SRCS \"unused.c;nested/app.c\" PRIV_REQUIRES \"freertos;esp_system\")\n");
        check(lists.Files[1].After.Contains("\"freertos;esp_system\" esp_driver_gpio", StringComparison.Ordinal), "literal semicolon lists retain their original quoting");
        var directories = await Prepare("idf_component_register(SRC_DIRS \"./nested\" EXCLUDE_SRCS \"nested/other.c\" PRIV_REQUIRES freertos)\n");
        check(directories.AddedComponents.Count == 1, "literal SRC_DIRS membership is supported at its actual directory depth");
        var includeVariable = await Prepare("idf_component_register(SRCS nested/app.c INCLUDE_DIRS ${MY_INCLUDE_DIR} PRIV_REQUIRES freertos)\n");
        check(includeVariable.Files[1].After.Contains("INCLUDE_DIRS ${MY_INCLUDE_DIR}", StringComparison.Ordinal), "unrelated include expressions are retained without evaluation");

        foreach (var (cmake, label) in new[]
        {
            ("idf_component_register(SRCS ${sources} PRIV_REQUIRES freertos)", "variable source list"),
            ("idf_component_register(SRCS nested/app.c PRIV_REQUIRES ${deps})", "variable dependency list"),
            ("if(CONFIG_FEATURE)\nidf_component_register(SRCS nested/app.c)\nendif()", "conditional registration"),
            ("function(register)\nidf_component_register(SRCS nested/app.c)\nendfunction()", "function registration"),
            ("function(idf_component_register)\nendfunction()\nidf_component_register(SRCS nested/app.c)", "redefined registration command"),
            ("idf_component_register(SRCS nested/app.c)\nidf_component_register(SRCS nested/app.c)", "duplicate registration"),
            ("idf_component_register(SRCS other.c)", "unregistered source"),
            ("idf_component_register(SRC_DIRS . SRCS nested/app.c)", "SRC_DIRS overriding a matching SRCS list"),
            ("idf_component_register(SRC_DIRS nested EXCLUDE_SRCS nested/app.c)", "explicitly excluded source"),
            ("idf_component_register(SRCS nested/app.c PRIV_REQUIRES one PRIV_REQUIRES two)", "duplicate dependency keyword"),
            ("idf_component_register(SRCS nested/app.c PRIV_REQUIRES $<IF:x,a,b>)", "generator expression"),
            ("idf_component_register(SRCS nested/app.c # unclosed", "incomplete command"),
            ("idf_component_register(SRCS nested/app.c)\n#[=[ unclosed", "incomplete bracket comment")
        })
        {
            await Reject(() => Prepare(cmake), "PERIPHERAL_CMAKE_UNSUPPORTED", label + " prevents both edits");
        }

        await Prepare(originalCMake);
        var nestedCMake = PathBoundary.Resolve(root, "main/nested/CMakeLists.txt");
        await File.WriteAllTextAsync(nestedCMake, "# independent nested entry\n");
        try
        {
            await Reject(() => service.ReadComponentAsync(context, sourcePath, []), "PERIPHERAL_CMAKE_UNSUPPORTED", "unrecognized nearest entry is not bypassed to guess an outer component");
        }
        finally { File.Delete(nestedCMake); }
        await Reject(() => service.ReadComponentAsync(context, "Device/sdk/app.c", []), "PERIPHERAL_COMPONENT", "case variants of reserved device paths remain protected");
        var componentForLimit = await service.ReadComponentAsync(context, sourcePath, []);
        var largeSource = componentForLimit.Source with
        {
            Text = new string('x', 4 * 1024 * 1024 - 3)
        };
        await Reject(() => Task.FromResult(service.PrepareAddition(context, componentForLimit with { Source = largeSource }, preview)),
            "PERIPHERAL_ADDITION_SIZE", "addition refuses a file that would exceed the editor byte limit");

        addition = await Prepare(originalCMake);
        var openSource = new WorkspaceBufferSnapshot(await files.ReadAsync(root, sourcePath), originalSource + "// unsaved source\r\n");
        var openCMake = new WorkspaceBufferSnapshot(await files.ReadAsync(root, cmakePath), originalCMake + "# unsaved dependency notes\r\n");
        var opened = await service.ReadComponentAsync(context, sourcePath, [openSource, openCMake]);
        var unsaved = service.PrepareAddition(context, opened, preview);
        await service.ValidateAdditionAsync(unsaved, [openSource, openCMake]);
        check(unsaved.Files[0].After.EndsWith(openSource.Text, StringComparison.Ordinal) && unsaved.Files[1].After.EndsWith("# unsaved dependency notes\r\n", StringComparison.Ordinal),
            "unsaved source and CMake changes are merged from buffers");
        await Reject(() => service.ValidateAdditionAsync(unsaved, [openSource, openCMake with { Text = openCMake.Text + "# later" }]),
            "PERIPHERAL_ADDITION_STALE", "later CMake buffer edit invalidates the entire addition");
        await Reject(() => service.ValidateAdditionAsync(unsaved, [openSource]), "PERIPHERAL_ADDITION_STALE", "closing the previewed CMake buffer invalidates the entire addition");
        await Reject(() => service.ValidateAdditionAsync(unsaved, [openSource with { Source = openSource.Source with { IsReadOnly = true } }, openCMake]),
            "PERIPHERAL_ADDITION_STALE", "read-only source blocks the entire addition");
        await File.WriteAllTextAsync(cmakeFile, originalCMake + "# external edit\r\n");
        await Reject(() => service.ValidateAdditionAsync(addition, []), "PERIPHERAL_ADDITION_STALE", "external CMake edit invalidates preview");
        await Reject(() => service.ReadComponentAsync(context, sourcePath, [openSource, openCMake]), "PERIPHERAL_ADDITION_STALE", "stale open CMake disk baseline is rejected");
        addition = await Prepare(originalCMake);
        File.SetAttributes(cmakeFile, File.GetAttributes(cmakeFile) | FileAttributes.ReadOnly);
        try
        {
            await Reject(() => service.ValidateAdditionAsync(addition, []), "PERIPHERAL_ADDITION_STALE", "CMake becoming read-only blocks the entire addition");
        }
        finally { File.SetAttributes(cmakeFile, FileAttributes.Normal); }
        var saved = await files.SaveAsync(root, addition.Files[1].Source, addition.Files[1].After);
        check((await File.ReadAllBytesAsync(cmakeFile)).AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) && saved.Text == addition.Files[1].After,
            "normal editor save preserves CMake UTF-8 BOM");
        var idempotent = await service.ReadComponentAsync(context, sourcePath, []);
        check(service.PrepareAddition(context, idempotent, preview).AddedComponents.Count == 0, "repeated dependency preparation is idempotent");
    }
}
