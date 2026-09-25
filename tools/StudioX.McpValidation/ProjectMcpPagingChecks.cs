using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StudioX.Application.Mcp;

internal static class ProjectMcpPagingChecks
{
    public static async Task RunAsync(StudioXMcpSession session, SwitchingAuthorizer authorizer,
        string project, Action<bool, string> check)
    {
        var drivers = Path.Combine(project, "Drivers", "Example");
        var middleware = Path.Combine(project, "Middlewares", "Example");
        var device = Path.Combine(project, "device");
        Directory.CreateDirectory(drivers);
        Directory.CreateDirectory(middleware);
        Directory.CreateDirectory(device);
        var largePath = Path.Combine(drivers, "large.c");
        var original = new StringBuilder();
        for (var line = 0; line < 2000; line++)
            original.Append("static const unsigned line_").Append(line).Append(" = ").Append(line).AppendLine(";");
        original.AppendLine("int UniqueMarker(void) { return 7; }");
        var initialText = original.ToString();
        await File.WriteAllTextAsync(largePath, initialText);
        var originalSha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(initialText)));

        var first = await Call(session, "project_read_file", new { path = "Drivers/Example/large.c" });
        using var firstJson = JsonDocument.Parse(first);
        check(!firstJson.RootElement.GetProperty("endOfFile").GetBoolean() &&
              firstJson.RootElement.GetProperty("sha256").GetString() == originalSha &&
              first.Length < 32_000,
            "large project source returns a bounded page and whole-file SHA");
        var rebuilt = new StringBuilder();
        var page = first;
        var pageCount = 0;
        while (true)
        {
            using var data = JsonDocument.Parse(page);
            rebuilt.Append(data.RootElement.GetProperty("text").GetString());
            pageCount++;
            if (data.RootElement.GetProperty("endOfFile").GetBoolean()) break;
            page = await Call(session, "project_read_file", new
            {
                path = "Drivers/Example/large.c",
                startLine = data.RootElement.GetProperty("nextLine").GetInt32(),
                startColumn = data.RootElement.GetProperty("nextColumn").GetInt32()
            });
            if (pageCount > 30) throw new Exception("Source page cursor did not advance.");
        }
        check(pageCount > 1 && rebuilt.ToString() == initialText,
            "line and column page cursors reconstruct source larger than 64 KiB");

        var longLine = "/*" + new string('x', 37_000) + "*/";
        await File.WriteAllTextAsync(Path.Combine(drivers, "one_line.c"), longLine);
        var longFirst = await Call(session, "project_read_file", new { path = "Drivers/Example/one_line.c" });
        using var longJson = JsonDocument.Parse(longFirst);
        check(longJson.RootElement.GetProperty("nextLine").GetInt32() == 1 &&
              longJson.RootElement.GetProperty("nextColumn").GetInt32() > 1,
            "a long source line exposes a column cursor rather than losing its suffix");

        var foundLarge = await Call(session, "project_search", new
        {
            query = "UniqueMarker", directory = "Drivers/Example", maxResults = 10
        });
        check(foundLarge.Contains("Drivers/Example/large.c", StringComparison.Ordinal) &&
              foundLarge.Contains("UniqueMarker", StringComparison.Ordinal),
            "project search scans source above the old 32 KiB cutoff");

        for (var index = 0; index < 90; index++)
            await File.WriteAllTextAsync(Path.Combine(middleware, $"sample_{index:D2}.c"),
                index == 89 ? "// LaterPageMarker\n" : $"// sample {index}\n");
        var listedFirst = await Call(session, "project_list_files", new { directory = "Middlewares/Example" });
        using var listedFirstJson = JsonDocument.Parse(listedFirst);
        var listedNext = listedFirstJson.RootElement.GetProperty("nextCursor").GetString();
        var listedSecond = await Call(session, "project_list_files", new
        {
            directory = "Middlewares/Example", cursor = listedNext
        });
        using var listedSecondJson = JsonDocument.Parse(listedSecond);
        check(listedFirstJson.RootElement.GetProperty("entries").GetArrayLength() == 80 &&
              listedSecondJson.RootElement.GetProperty("entries").GetArrayLength() == 10 &&
              listedSecond.Contains("sample_89.c", StringComparison.Ordinal),
            "project directory listing pages beyond its former 80-entry cutoff");
        var searchCursor = "";
        var foundLater = false;
        var searchPages = 0;
        do
        {
            var search = await Call(session, "project_search", new
            {
                query = "LaterPageMarker", directory = "Middlewares/Example",
                maxResults = 1, cursor = searchCursor
            });
            using var data = JsonDocument.Parse(search);
            foundLater |= data.RootElement.GetProperty("results").GetArrayLength() > 0;
            searchPages++;
            searchCursor = data.RootElement.GetProperty("nextCursor").GetString() ?? "";
            if (searchPages > 4) throw new Exception("Search cursor did not advance.");
        } while (!foundLater && searchCursor.Length > 0);
        check(foundLater && searchPages >= 2,
            "project search cursor continues beyond the per-call file budget");

        var devicePath = Path.Combine(device, "chip.h");
        await File.WriteAllTextAsync(devicePath, "#define CHIP_VALUE 1\n");
        var deviceRead = await Call(session, "project_read_file", new { path = "device/chip.h" });
        using var deviceJson = JsonDocument.Parse(deviceRead);
        var rejectedDevicePatch = await Call(session, "project_patch_file", new
        {
            path = "device/chip.h",
            originalSha256 = deviceJson.RootElement.GetProperty("sha256").GetString(),
            hunks = new[] { new { oldText = "CHIP_VALUE 1", newText = "CHIP_VALUE 2" } }
        });
        check(deviceRead.Contains("CHIP_VALUE", StringComparison.Ordinal) &&
              deviceJson.RootElement.GetProperty("readOnly").GetBoolean() &&
              (rejectedDevicePatch.Contains("MCP_FILE_READ_ONLY", StringComparison.Ordinal) ||
               rejectedDevicePatch.Contains("只能读取", StringComparison.Ordinal)) &&
              await File.ReadAllTextAsync(devicePath) == "#define CHIP_VALUE 1\n",
            "device sources are readable while managed device writes remain blocked");

        var patchArgs = new
        {
            path = "Drivers/Example/large.c", originalSha256 = originalSha,
            hunks = new[]
            {
                new { oldText = "line_20 = 20", newText = "line_20 = 21" },
                new { oldText = "return 7;", newText = "return 9;" }
            }
        };
        var previousApproval = authorizer.Allow;
        authorizer.Allow = false;
        var deniedPatch = await Call(session, "project_patch_file", patchArgs);
        check(deniedPatch.Contains("MCP_APPROVAL_DENIED", StringComparison.OrdinalIgnoreCase) &&
              await File.ReadAllTextAsync(largePath) == initialText,
            "a denied patch leaves the large source unchanged");
        authorizer.Allow = true;
        var patched = await Call(session, "project_patch_file", patchArgs);
        using var patchJson = JsonDocument.Parse(patched);
        var modified = await File.ReadAllTextAsync(largePath);
        check(patchJson.RootElement.GetProperty("saved").GetBoolean() &&
              patchJson.RootElement.GetProperty("hunksApplied").GetInt32() == 2 &&
              modified.Contains("line_20 = 21", StringComparison.Ordinal) &&
              modified.Contains("return 9;", StringComparison.Ordinal) &&
              authorizer.Requests.Any(request => request.Tool == "project_patch_file" &&
                  request.Summary.Contains("@@ 1 / line", StringComparison.Ordinal) &&
                  request.Summary.Contains("- line_20 = 20", StringComparison.Ordinal) &&
                  request.Summary.Contains("+ line_20 = 21", StringComparison.Ordinal)),
            "approved multi-hunk patch saves a large file with a concrete diff preview");
        var stale = await Call(session, "project_patch_file", patchArgs);
        check(stale.Contains("MCP_FILE_CHANGED", StringComparison.Ordinal) &&
              await File.ReadAllTextAsync(largePath) == modified,
            "patch refuses stale SHA after the file changes");
        var currentSha = patchJson.RootElement.GetProperty("sha256").GetString();
        var ambiguous = await Call(session, "project_patch_file", new
        {
            path = "Drivers/Example/large.c", originalSha256 = currentSha,
            hunks = new[] { new { oldText = "static const unsigned", newText = "const unsigned" } }
        });
        var overlapping = await Call(session, "project_patch_file", new
        {
            path = "Drivers/Example/large.c", originalSha256 = currentSha,
            hunks = new[]
            {
                new { oldText = "line_20 = 21", newText = "line_20 = 22" },
                new { oldText = "line_20 = 21;", newText = "line_20 = 23;" }
            }
        });
        check(ambiguous.Contains("MCP_PATCH_MATCH", StringComparison.Ordinal) &&
              overlapping.Contains("MCP_PATCH_OVERLAP", StringComparison.Ordinal) &&
              await File.ReadAllTextAsync(largePath) == modified,
            "patch rejects ambiguous and overlapping matches before approval or write");
        authorizer.Allow = previousApproval;

        var previousCreateApproval = authorizer.Allow;
        authorizer.Allow = false;
        var deniedDirectory = await Call(session, "project_create_directory", new { path = "Middlewares/Generated" });
        check(deniedDirectory.Contains("MCP_APPROVAL_DENIED", StringComparison.OrdinalIgnoreCase) &&
              !Directory.Exists(Path.Combine(project, "Middlewares", "Generated")),
            "denied project directory creation leaves the workspace unchanged");
        authorizer.Allow = true;
        var created = await Call(session, "project_create_directory", new { path = "Middlewares/Generated" });
        check(created.Contains("\"created\":true", StringComparison.Ordinal) &&
              Directory.Exists(Path.Combine(project, "Middlewares", "Generated")),
            "approved project directory creation uses the project file service");
        authorizer.Allow = previousCreateApproval;
    }

    private static Task<string> Call(StudioXMcpSession session, string tool, object arguments) =>
        session.CallToolAsync(tool, JsonSerializer.Serialize(arguments));
}
