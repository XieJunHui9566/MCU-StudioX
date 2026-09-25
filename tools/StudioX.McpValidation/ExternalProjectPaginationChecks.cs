using System.Text.Json;
using StudioX.Application;
using StudioX.Application.Mcp;

internal static class ExternalProjectPaginationChecks
{
    public static async Task RunAsync(WorkbenchService services, SwitchingAuthorizer authorizer,
        string project, string temporaryRoot, Action<bool, string> check)
    {
        var root = Path.Combine(temporaryRoot, "external-pagination");
        var wide = Path.Combine(root, "Wide");
        var sparse = Path.Combine(root, "Sparse");
        var repeated = Path.Combine(root, "Repeated");
        Directory.CreateDirectory(wide);
        Directory.CreateDirectory(sparse);
        Directory.CreateDirectory(repeated);
        for (var index = 0; index < 145; index++)
            await File.WriteAllTextAsync(Path.Combine(wide, $"sample_{index:D3}.c"),
                $"int ANCHOR_MATCH_{index:D3};\n");
        for (var index = 0; index < 2_105; index++)
            await File.WriteAllTextAsync(Path.Combine(sparse, $"source_{index:D4}.c"), "int no_match;\n");
        await File.WriteAllTextAsync(Path.Combine(repeated, "many.c"),
            string.Concat(Enumerable.Range(1, 75).Select(number => $"REPEATED_MATCH {number}\n")));
        var hidden = Path.Combine(wide, "sample_hidden.c");
        await File.WriteAllTextAsync(hidden, "ANCHOR_MATCH must stay hidden\n");
        File.SetAttributes(hidden, FileAttributes.Hidden);

        var oldApproval = authorizer.Allow;
        try
        {
            authorizer.Allow = true;
            await using var session = await StudioXMcpSession.CreateAsync(
                new StudioXMcpTools(services, project, authorizer));
            Task<string> Call(string name, object args) => session.CallToolAsync(name, JsonSerializer.Serialize(args));
            static string? Next(JsonElement page) => page.GetProperty("nextCursor").ValueKind == JsonValueKind.Null
                ? null : page.GetProperty("nextCursor").GetString();

            var toolNames = (await session.ListToolsAsync()).Select(item => item.Name).ToArray();
            check(toolNames.Contains("external_project_roots", StringComparer.Ordinal) && toolNames.Length <= 64,
                "external root discovery remains inside the 64-tool request budget");

            var approvalCount = authorizer.Requests.Count;
            using var opened = JsonDocument.Parse(await Call("external_project_open", new { directory = root }));
            var rootId = opened.RootElement.GetProperty("rootId").GetString()!;
            using var reopened = JsonDocument.Parse(await Call("external_project_open",
                new { directory = root + Path.DirectorySeparatorChar }));
            check(rootId == reopened.RootElement.GetProperty("rootId").GetString() &&
                  reopened.RootElement.GetProperty("alreadyApproved").GetBoolean() &&
                  authorizer.Requests.Count == approvalCount + 1,
                "reopening an approved external directory reuses its root ID without another approval");
            using var roots = JsonDocument.Parse(await Call("external_project_roots", new { }));
            check(roots.RootElement.GetProperty("roots").EnumerateArray().Count() == 1 &&
                  roots.RootElement.GetProperty("roots")[0].GetProperty("rootId").GetString() == rootId,
                "approved external root IDs are discoverable in the same MCP session");

            var listed = new HashSet<string>(StringComparer.Ordinal);
            string? cursor = null;
            var pages = 0;
            var unique = true;
            do
            {
                using var page = JsonDocument.Parse(await Call("external_project_list_files",
                    new { rootId, directory = "Wide", cursor = cursor ?? "" }));
                foreach (var item in page.RootElement.GetProperty("entries").EnumerateArray())
                    unique &= listed.Add(item.GetProperty("path").GetString()!);
                cursor = Next(page.RootElement);
                pages++;
            } while (cursor is not null && pages < 20);
            check(unique && pages > 1 && cursor is null && listed.Count == 145 &&
                  !listed.Any(path => path.Contains("hidden", StringComparison.OrdinalIgnoreCase)),
                "external list pages cover a wide directory and exclude Hidden files");

            var found = new HashSet<string>(StringComparer.Ordinal);
            cursor = null;
            pages = 0;
            unique = true;
            do
            {
                using var page = JsonDocument.Parse(await Call("external_project_find_files",
                    new { rootId, directory = "Wide", query = "sample_", maxResults = 17,
                        cursor = cursor ?? "" }));
                foreach (var item in page.RootElement.GetProperty("results").EnumerateArray())
                    unique &= found.Add(item.GetProperty("path").GetString()!);
                cursor = Next(page.RootElement);
                pages++;
            } while (cursor is not null && pages < 30);
            check(unique && pages > 1 && cursor is null && found.SetEquals(listed),
                "external filename pagination reaches every visible match");

            var searched = new HashSet<string>(StringComparer.Ordinal);
            cursor = null;
            pages = 0;
            unique = true;
            do
            {
                using var page = JsonDocument.Parse(await Call("external_project_search",
                    new { rootId, directory = "Wide", query = "ANCHOR_MATCH", maxResults = 11,
                        cursor = cursor ?? "" }));
                foreach (var item in page.RootElement.GetProperty("results").EnumerateArray())
                    unique &= searched.Add(item.GetProperty("path").GetString()!);
                cursor = Next(page.RootElement);
                pages++;
            } while (cursor is not null && pages < 30);
            check(unique && pages > 1 && cursor is null && searched.SetEquals(listed),
                "external content pagination reaches every visible source match");

            var lineNumbers = new HashSet<int>();
            cursor = null;
            unique = true;
            do
            {
                using var page = JsonDocument.Parse(await Call("external_project_search",
                    new { rootId, directory = "Repeated", query = "REPEATED_MATCH", maxResults = 7,
                        cursor = cursor ?? "" }));
                foreach (var item in page.RootElement.GetProperty("results").EnumerateArray())
                    unique &= lineNumbers.Add(item.GetProperty("line").GetInt32());
                cursor = Next(page.RootElement);
            } while (cursor is not null && lineNumbers.Count < 100);
            check(unique && cursor is null && lineNumbers.Count == 75 &&
                  lineNumbers.Min() == 1 && lineNumbers.Max() == 75,
                "external search cursor preserves the position within a matching file");

            using var firstSparse = JsonDocument.Parse(await Call("external_project_search",
                new { rootId, directory = "Sparse", query = "ABSENT_NEEDLE", maxResults = 11 }));
            cursor = Next(firstSparse.RootElement);
            check(cursor is not null && firstSparse.RootElement.GetProperty("scannedEntries").GetInt32() == 2_000,
                "external search returns a cursor after a bounded scan even with no matches");
            if (cursor is not null)
            {
                var invalid = await Call("external_project_search",
                    new { rootId, directory = "Sparse", query = "DIFFERENT_QUERY", cursor });
                check(invalid.Contains("error", StringComparison.OrdinalIgnoreCase),
                    "an external search cursor cannot be reused with a different query");
                using var secondSparse = JsonDocument.Parse(await Call("external_project_search",
                    new { rootId, directory = "Sparse", query = "ABSENT_NEEDLE", cursor }));
                check(Next(secondSparse.RootElement) is null &&
                      secondSparse.RootElement.GetProperty("scannedEntries").GetInt32() == 105,
                    "external search continues beyond the first scan budget without rescanning entries");
            }

            for (var index = 1; index < 8; index++)
            {
                var extra = Path.Combine(temporaryRoot, $"external-slot-{index}");
                Directory.CreateDirectory(extra);
                using var extraOpened = JsonDocument.Parse(await Call("external_project_open", new { directory = extra }));
                check(extraOpened.RootElement.TryGetProperty("rootId", out _),
                    "independent external directories can be authorized within the session limit");
            }
            using var atCapacity = JsonDocument.Parse(await Call("external_project_open", new { directory = root }));
            check(atCapacity.RootElement.GetProperty("rootId").GetString() == rootId,
                "reopening the same directory still succeeds when all external slots are occupied");
        }
        finally
        {
            authorizer.Allow = oldApproval;
            if (File.Exists(hidden)) File.SetAttributes(hidden, FileAttributes.Normal);
        }
    }
}
