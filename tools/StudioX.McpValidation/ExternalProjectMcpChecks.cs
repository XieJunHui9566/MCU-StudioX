using System.Security.Cryptography;
using System.Text.Json;
using StudioX.Application;
using StudioX.Application.Mcp;
using StudioX.Engine;
using StudioX.Foundation;

internal static class ExternalProjectMcpChecks
{
    public static async Task RunAsync(StudioXMcpSession session, WorkbenchService services,
        SwitchingAuthorizer authorizer, string project, string temporaryRoot, Action<bool, string> check)
    {
        var example = Path.Combine(temporaryRoot, "external-example");
        var hal = Path.Combine(example, "Drivers", "STM32F4xx_HAL_Driver", "Src", "stm32f4xx_hal_gpio.c");
        var lvglCore = Path.Combine(example, "LVGL", "src", "core", "lv_obj.c");
        var lvglLabel = Path.Combine(example, "LVGL", "src", "widgets", "lv_label.c");
        var libraryRoot = Path.Combine(temporaryRoot, "external-library-root");
        var librarySource = Path.Combine(libraryRoot, "src", "common.c");
        var outside = Path.Combine(temporaryRoot, "outside-external.c");
        Directory.CreateDirectory(Path.GetDirectoryName(hal)!);
        Directory.CreateDirectory(Path.GetDirectoryName(lvglCore)!);
        Directory.CreateDirectory(Path.GetDirectoryName(lvglLabel)!);
        Directory.CreateDirectory(Path.Combine(example, "MixedLib", "src"));
        Directory.CreateDirectory(Path.Combine(example, "MixedLib", "build"));
        Directory.CreateDirectory(Path.Combine(example, "MixedLib", "secrets", "child"));
        Directory.CreateDirectory(Path.Combine(example, "LargeLib"));
        Directory.CreateDirectory(Path.GetDirectoryName(librarySource)!);
        Directory.CreateDirectory(Path.Combine(libraryRoot, "include"));
        await File.WriteAllTextAsync(hal, "#include \"stm32f4xx_hal_gpio.h\"\nvoid HAL_GPIO_Init(void) {}\n");
        await File.WriteAllTextAsync(lvglCore, "void lv_obj_init(void) {}\n");
        await File.WriteAllTextAsync(lvglLabel,
            "#include \"lv_label.h\"\nvoid lv_label_set_text(void) {\n    HAL_GPIO_Init();\n}\n");
        await File.WriteAllTextAsync(Path.Combine(example, "LVGL", "README.md"), "LVGL example library\n");
        await File.WriteAllTextAsync(Path.Combine(example, "MixedLib", "src", "safe.c"), "int mixed_safe;\n");
        await File.WriteAllTextAsync(Path.Combine(example, "MixedLib", "build", "generated.c"), "int excluded_build;\n");
        await File.WriteAllTextAsync(Path.Combine(example, "MixedLib", "secrets", "key.h"), "API_SECRET_NEVER_READ\n");
        await File.WriteAllTextAsync(Path.Combine(example, "credentials.h"), "API_SECRET_NEVER_READ\n");
        await File.WriteAllTextAsync(Path.Combine(example, ".env"), "API_SECRET_NEVER_READ\n");
        await File.WriteAllBytesAsync(Path.Combine(example, "binary.c"), [0, 1, 2, 0, 3]);
        await File.WriteAllTextAsync(Path.Combine(example, "LargeLib", "small.c"), "int preflight_small;\n");
        await using (var large = new FileStream(Path.Combine(example, "LargeLib", "too_large.c"),
                         FileMode.CreateNew, FileAccess.Write, FileShare.None))
            large.SetLength(17L * 1024 * 1024);
        await File.WriteAllTextAsync(outside, "OUTSIDE_PRIVATE_EXAMPLE\n");
        await File.WriteAllTextAsync(librarySource, "int shared_library_entry(void) { return 7; }\n");
        await File.WriteAllTextAsync(Path.Combine(libraryRoot, "include", "common.h"),
            "int shared_library_entry(void);\n");

        Task<string> Call(string name, object arguments) =>
            session.CallToolAsync(name, JsonSerializer.Serialize(arguments));
        static bool Error(string response) => response.Contains("\"error\"", StringComparison.OrdinalIgnoreCase);

        var toolNames = (await session.ListToolsAsync()).Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);
        check(new[] { "external_project_open", "external_project_list_files", "external_project_find_files", "external_project_read_file",
                "external_project_search", "external_project_copy" }.All(toolNames.Contains),
            "MCP handshake exposes external example read and approved copy tools to both clients");

        var unknownId = "unapproved-example-root";
        var unknownList = await Call("external_project_list_files", new { rootId = unknownId, directory = "" });
        var unknownRead = await Call("external_project_read_file", new { rootId = unknownId, path = "LVGL/src/widgets/lv_label.c" });
        var unknownCopy = await Call("external_project_copy", new
        { rootId = unknownId, sourcePath = "LVGL", destinationPath = "src/unknown-LVGL" });
        check(Error(unknownList) && Error(unknownRead) && Error(unknownCopy) &&
              !Directory.Exists(Path.Combine(project, "src", "unknown-LVGL")),
            "unguessed root ID is required for browsing and copying external source");

        var missingOpen = await Call("external_project_open", new
        { directory = Path.Combine(temporaryRoot, "missing-example") });
        var relativeOpen = await Call("external_project_open", new { directory = "external-example" });
        var protectedOpen = await Call("external_project_open", new
        { directory = Path.Combine(example, "MixedLib", "secrets", "child") });
        check(Error(missingOpen) && Error(relativeOpen) && Error(protectedOpen),
            "external root must be absolute, exist, and avoid protected ancestor directories");

        var beforeOpen = authorizer.Requests.Count;
        var previousApproval = authorizer.Allow;
        string? linkedRoot = null;
        string? linkedFile = null;
        try
        {
            authorizer.Allow = false;
            var deniedOpen = await Call("external_project_open", new { directory = example });
            check(Error(deniedOpen) && authorizer.Requests.Skip(beforeOpen).Any(request =>
                    request.Tool == "external_project_open" &&
                    request.Permission == StudioXMcpPermission.ExternalRead &&
                    request.Project == Path.GetFullPath(project)),
                "external directory grant requires an explicit ExternalRead approval");

            authorizer.Allow = true;
            var opened = await Call("external_project_open", new { directory = example });
            using var openedJson = JsonDocument.Parse(opened);
            var rootId = openedJson.RootElement.GetProperty("rootId").GetString();
            check(!string.IsNullOrWhiteSpace(rootId) && !Error(opened),
                "approved external example directory receives an opaque root ID");

            var rootEntries = await Call("external_project_list_files", new { rootId, directory = "" });
            var driverEntries = await Call("external_project_list_files", new { rootId, directory = "Drivers/STM32F4xx_HAL_Driver/Src" });
            check(rootEntries.Contains("Drivers", StringComparison.Ordinal) &&
                  rootEntries.Contains("LVGL", StringComparison.Ordinal) &&
                  driverEntries.Contains("stm32f4xx_hal_gpio.c", StringComparison.Ordinal),
                "approved external MCU example lists HAL and LVGL directories");
            check(!rootEntries.Contains("credentials.h", StringComparison.OrdinalIgnoreCase) &&
                  !rootEntries.Contains(".env", StringComparison.OrdinalIgnoreCase),
                "external directory listing omits credential-like and hidden files");

            var foundFiles = await Call("external_project_find_files", new
            { rootId, query = "lv_label", directory = "", maxResults = 20 });
            var hiddenFiles = await Call("external_project_find_files", new
            { rootId, query = "API_SECRET", directory = "", maxResults = 20 });
            check(foundFiles.Contains("LVGL/src/widgets/lv_label.c", StringComparison.Ordinal) &&
                  !hiddenFiles.Contains("credentials.h", StringComparison.OrdinalIgnoreCase) &&
                  !hiddenFiles.Contains("key.h", StringComparison.OrdinalIgnoreCase),
                "external filename search locates library sources without walking folders or exposing secrets");

            var read = await Call("external_project_read_file", new
            { rootId, path = "LVGL/src/widgets/lv_label.c", startLine = 2, maxLines = 1 });
            using var readJson = JsonDocument.Parse(read);
            var chunk = readJson.RootElement.GetProperty("text").GetString()!;
            var halRead = await Call("external_project_read_file", new
            { rootId, path = "Drivers/STM32F4xx_HAL_Driver/Src/stm32f4xx_hal_gpio.c" });
            check(chunk.Contains("lv_label_set_text", StringComparison.Ordinal) &&
                  !chunk.Contains("HAL_GPIO_Init", StringComparison.Ordinal) &&
                  halRead.Contains("HAL_GPIO_Init", StringComparison.Ordinal),
                "external source read is bounded by requested lines and includes HAL code");

            var found = await Call("external_project_search", new
            { rootId, query = "HAL_GPIO_Init", directory = "", maxResults = 40 });
            check(found.Contains("LVGL/src/widgets/lv_label.c", StringComparison.Ordinal) &&
                  found.Contains("Drivers/STM32F4xx_HAL_Driver/Src/stm32f4xx_hal_gpio.c", StringComparison.Ordinal),
                "external example search traverses allowed HAL and LVGL code");
            var secretSearch = await Call("external_project_search", new
            { rootId, query = "API_SECRET_NEVER_READ", directory = "", maxResults = 40 });
            using var secretSearchJson = JsonDocument.Parse(secretSearch);
            check(secretSearchJson.RootElement.GetProperty("results").GetArrayLength() == 0,
                "external search excludes secret files and metadata");

            var invalidReads = new[]
            {
                await Call("external_project_read_file", new { rootId, path = "../outside-external.c" }),
                await Call("external_project_read_file", new { rootId, path = outside }),
                await Call("external_project_read_file", new { rootId, path = "LVGL/missing.c" }),
                await Call("external_project_read_file", new { rootId, path = "credentials.h" }),
                await Call("external_project_read_file", new { rootId, path = "binary.c" }),
                await Call("external_project_read_file", new { rootId, path = "LargeLib/too_large.c" })
            };
            var escapedList = await Call("external_project_list_files", new { rootId, directory = "../" });
            var escapedSearch = await Call("external_project_search", new
            { rootId, query = "OUTSIDE_PRIVATE_EXAMPLE", directory = "../", maxResults = 40 });
            check(invalidReads.All(Error) && Error(escapedList) && Error(escapedSearch),
                "external reads reject traversal, absolute, missing, secret, binary and oversized paths");

            try
            {
                linkedRoot = Path.Combine(temporaryRoot, "linked-external-example");
                Directory.CreateSymbolicLink(linkedRoot, example);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            {
                Console.WriteLine("SKIP external root symlink check: " + ex.Message);
            }
            if (linkedRoot is not null && Directory.Exists(linkedRoot))
            {
                var linkedOpen = await Call("external_project_open", new { directory = linkedRoot });
                check(Error(linkedOpen), "linked external root cannot bypass explicit path approval");
            }
            try
            {
                linkedFile = Path.Combine(example, "LVGL", "src", "linked.c");
                File.CreateSymbolicLink(linkedFile, outside);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            {
                Console.WriteLine("SKIP external child symlink check: " + ex.Message);
            }
            if (linkedFile is not null && File.Exists(linkedFile))
            {
                var linkedRead = await Call("external_project_read_file", new { rootId, path = "LVGL/src/linked.c" });
                check(Error(linkedRead), "linked external child cannot escape an approved root");
            }

            var originalSourceSha = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(lvglLabel)));
            var destination = Path.Combine(project, "src", "LVGL");
            authorizer.Allow = false;
            var deniedCopy = await Call("external_project_copy", new
            { rootId, sourcePath = "LVGL", destinationPath = "src/LVGL" });
            check(Error(deniedCopy) && !Directory.Exists(destination) &&
                  authorizer.Requests.Any(request => request.Tool == "external_project_copy" &&
                      request.Permission == StudioXMcpPermission.FileWrite),
                "external library copy requires separate FileWrite approval");

            authorizer.Allow = true;
            var copied = await Call("external_project_copy", new
            { rootId, sourcePath = "LVGL", destinationPath = "src/LVGL" });
            var copiedLabel = Path.Combine(destination, "src", "widgets", "lv_label.c");
            check(!Error(copied) && File.Exists(copiedLabel) &&
                  File.Exists(Path.Combine(destination, "src", "core", "lv_obj.c")) &&
                  Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(copiedLabel))) == originalSourceSha &&
                  Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(lvglLabel))) == originalSourceSha,
                "approved directory copy preserves library structure and source bytes");

            var copiedAgain = await Call("external_project_copy", new
            { rootId, sourcePath = "LVGL", destinationPath = "src/LVGL" });
            var existingTarget = await Call("external_project_copy", new
            { rootId, sourcePath = "LVGL/src/widgets/lv_label.c", destinationPath = "src/main.c" });
            check(Error(copiedAgain) && Error(existingTarget) &&
                  Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(copiedLabel))) == originalSourceSha &&
                  (await File.ReadAllTextAsync(Path.Combine(project, "src", "main.c"))).Contains("return 1", StringComparison.Ordinal),
                "external copy never overwrites existing files or directories");

            var mixedCopy = await Call("external_project_copy", new
            { rootId, sourcePath = "MixedLib", destinationPath = "src/MixedLib" });
            var mixedDestination = Path.Combine(project, "src", "MixedLib");
            check(!Error(mixedCopy) && File.Exists(Path.Combine(mixedDestination, "src", "safe.c")) &&
                  !Directory.Exists(Path.Combine(mixedDestination, "build")) &&
                  !Directory.Exists(Path.Combine(mixedDestination, "secrets")),
                "recursive copy skips build output and secret subdirectories");

            var tooLargeCopy = await Call("external_project_copy", new
            { rootId, sourcePath = "LargeLib", destinationPath = "src/LargeLib" });
            check(Error(tooLargeCopy) && !Directory.Exists(Path.Combine(project, "src", "LargeLib")),
                "copy preflight rejects oversized library without leaving a partial destination");

            var escapedSource = await Call("external_project_copy", new
            { rootId, sourcePath = "../outside-external.c", destinationPath = "src/escaped-source.c" });
            var escapedTarget = await Call("external_project_copy", new
            { rootId, sourcePath = "LVGL/src/widgets/lv_label.c", destinationPath = "../escaped-target.c" });
            var absoluteTarget = await Call("external_project_copy", new
            { rootId, sourcePath = "LVGL/src/widgets/lv_label.c", destinationPath = Path.Combine(temporaryRoot, "escaped-target.c") });
            check(Error(escapedSource) && Error(escapedTarget) && Error(absoluteTarget) &&
                  !File.Exists(Path.Combine(project, "src", "escaped-source.c")) &&
                  !File.Exists(Path.Combine(temporaryRoot, "escaped-target.c")),
                "external copy source and target remain inside their separately approved roots");

            var libraryOpen = await Call("external_project_open", new { directory = libraryRoot });
            using var libraryOpenJson = JsonDocument.Parse(libraryOpen);
            var libraryRootId = libraryOpenJson.RootElement.GetProperty("rootId").GetString();
            var librarySha = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(librarySource)));
            var copiedLibraryRoot = await Call("external_project_copy", new
            { rootId = libraryRootId, sourcePath = "", destinationPath = "src/LibRoot" });
            var copiedLibrarySource = Path.Combine(project, "src", "LibRoot", "src", "common.c");
            check(!Error(copiedLibraryRoot) && File.Exists(copiedLibrarySource) &&
                  File.Exists(Path.Combine(project, "src", "LibRoot", "include", "common.h")) &&
                  Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(copiedLibrarySource))) == librarySha &&
                  Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(librarySource))) == librarySha,
                "approved library root can be copied directly into current project with its tree intact");

            var otherProject = Path.Combine(temporaryRoot, "other-project");
            Directory.CreateDirectory(Path.Combine(otherProject, ".studiox"));
            await JsonStore.WriteAsync(Path.Combine(otherProject, ".studiox", "project.json"),
                new ProjectManifest(1, "other-project", "demo.pack", "1.0.0", "offline", "demo",
                    "blank", "gcc", "1.0.0", "gcc"));
            await using var otherSession = await StudioXMcpSession.CreateAsync(
                new StudioXMcpTools(services, otherProject, authorizer));
            var otherRead = await otherSession.CallToolAsync("external_project_read_file",
                JsonSerializer.Serialize(new { rootId, path = "LVGL/src/widgets/lv_label.c" }));
            check(Error(otherRead), "external root grant cannot be reused by another project MCP session");
        }
        finally
        {
            authorizer.Allow = previousApproval;
            if (linkedFile is not null && File.Exists(linkedFile)) File.Delete(linkedFile);
            if (linkedRoot is not null && Directory.Exists(linkedRoot)) Directory.Delete(linkedRoot);
        }
    }
}
