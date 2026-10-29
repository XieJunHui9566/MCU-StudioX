using System.Text.Json;
using System.Text.Json.Nodes;
using StudioX.Application;
using StudioX.Application.Mcp;

internal static class QmdMcpChecks
{
    public static async Task RunAsync(StudioXMcpSession session, WorkbenchService services,
        string project, Action<bool, string> check)
    {
        var fixture = Path.Combine(project, "src", "qmd_led_fixture.c");
        await File.WriteAllTextAsync(fixture,
            "void studioxQmdFixture(void) { HAL_GPIO_TogglePin(GPIOF, GPIO_PIN_9); }\n");
        var request = JsonSerializer.Serialize(new
        {
            query = "HAL_GPIO_TogglePin",
            directory = "src",
            maxResults = 5
        });
        string first;
        var previousPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", "");
            first = await session.CallToolAsync("project_qmd_search", request);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", previousPath);
        }
        using var firstJson = JsonDocument.Parse(first);
        var firstRoot = firstJson.RootElement;
        check(firstRoot.GetProperty("results").EnumerateArray().Any(item =>
                item.GetProperty("path").GetString() == "src/qmd_led_fixture.c" &&
                item.GetProperty("snippet").GetString()!.Contains("GPIO_PIN_9", StringComparison.Ordinal)) &&
            !firstRoot.GetProperty("reusedIndex").GetBoolean() &&
            firstRoot.GetProperty("engine").GetString() == "studiox-csharp-bm25",
            "内置 BM25 在空 PATH 环境仍返回源码相对路径、行号和片段，不启动 Node 或 QMD");

        var indexRoot = Path.Combine(services.DataDirectory, "qmd-bm25");
        var snapshotState = Directory.EnumerateFiles(indexRoot, "snapshot.json", SearchOption.AllDirectories)
            .Single();
        var modified = File.GetLastWriteTimeUtc(snapshotState);
        var second = await session.CallToolAsync("project_qmd_search", request);
        using var secondJson = JsonDocument.Parse(second);
        check(secondJson.RootElement.GetProperty("reusedIndex").GetBoolean() &&
            secondJson.RootElement.GetProperty("results").GetArrayLength() > 0 &&
            File.GetLastWriteTimeUtc(snapshotState) == modified &&
            !Directory.EnumerateFiles(indexRoot, "*.sqlite", SearchOption.AllDirectories).Any(),
            "未变更工程复用安全快照，不写 SQLite 或调用外部 update");
        var indexBytes = Directory.EnumerateFiles(indexRoot, "*", SearchOption.AllDirectories)
            .Sum(file => new FileInfo(file).Length);
        check(!Directory.EnumerateDirectories(indexRoot, "models", SearchOption.AllDirectories).Any() &&
            indexBytes < 64L * 1024 * 1024,
            $"QMD 集成不下载模型且临时项目索引仅 {indexBytes} 字节");

        var stateNode = JsonNode.Parse(await File.ReadAllTextAsync(snapshotState))!.AsObject();
        var paths = stateNode["Paths"]!.AsObject();
        paths[paths.First().Key] = "src/not_the_indexed_file.c";
        await File.WriteAllTextAsync(snapshotState, stateNode.ToJsonString());
        var repaired = await session.CallToolAsync("project_qmd_search", request);
        using var repairedJson = JsonDocument.Parse(repaired);
        check(!repairedJson.RootElement.GetProperty("reusedIndex").GetBoolean() &&
            repairedJson.RootElement.GetProperty("results").EnumerateArray().Any(item =>
                item.GetProperty("path").GetString() == "src/qmd_led_fixture.c"),
            "被篡改的 QMD 持久映射被拒绝并从真实工程源码重建");

        await File.WriteAllTextAsync(Path.Combine(project, "src", "secret_password.c"),
            "uniqueQmdSecretNeedle\n");
        var excluded = await session.CallToolAsync("project_qmd_search", JsonSerializer.Serialize(new
        {
            query = "uniqueQmdSecretNeedle",
            directory = "src",
            maxResults = 5
        }));
        using var excludedJson = JsonDocument.Parse(excluded);
        check(excludedJson.RootElement.GetProperty("results").GetArrayLength() == 0 &&
            excludedJson.RootElement.GetProperty("reusedIndex").GetBoolean(),
            "QMD 索引排除凭据名称源码且不因被排除文件而重建");

        await File.WriteAllTextAsync(fixture,
            "void studioxQmdFixture(void) { HAL_GPIO_WritePin(GPIOF, GPIO_PIN_10, GPIO_PIN_SET); }\n");
        var changed = await session.CallToolAsync("project_qmd_search", JsonSerializer.Serialize(new
        {
            query = "HAL_GPIO_WritePin",
            directory = "src",
            maxResults = 5
        }));
        using var changedJson = JsonDocument.Parse(changed);
        check(!changedJson.RootElement.GetProperty("reusedIndex").GetBoolean() &&
            changedJson.RootElement.GetProperty("results").EnumerateArray().Any(item =>
                item.GetProperty("path").GetString() == "src/qmd_led_fixture.c"),
            "源码修改后 QMD 快照重新索引并检索新内容");

        File.Delete(fixture);
        var deleted = await session.CallToolAsync("project_qmd_search", JsonSerializer.Serialize(new
        {
            query = "studioxQmdFixture",
            directory = "src"
        }));
        using var deletedJson = JsonDocument.Parse(deleted);
        check(deletedJson.RootElement.GetProperty("results").GetArrayLength() == 0 &&
            !deletedJson.RootElement.GetProperty("reusedIndex").GetBoolean(),
            "已删除源码不会继续出现在检索缓存中");
        var punctuation = await session.CallToolAsync("project_qmd_search", """{"query":"!!!","directory":"src"}""");
        check(punctuation.Contains("MCP_QMD_QUERY", StringComparison.Ordinal), "纯标点查询返回明确诊断");

        await File.WriteAllTextAsync(Path.Combine(project, "src", "rank_high.c"), "BM25FixtureTerm BM25FixtureTerm filler\n");
        await File.WriteAllTextAsync(Path.Combine(project, "src", "rank_low.c"), "BM25FixtureTerm filler other\n");
        var ranking = await session.CallToolAsync("project_qmd_search", """{"query":"bm25fixtureterm","directory":"src","maxResults":2}""");
        using var rankingJson = JsonDocument.Parse(ranking);
        var ranked = rankingJson.RootElement.GetProperty("results");
        check(ranked.GetArrayLength() == 2 && ranked[0].GetProperty("path").GetString() == "src/rank_high.c" &&
            ranked[0].GetProperty("score").GetDouble() > ranked[1].GetProperty("score").GetDouble(),
            "BM25 不区分关键词大小写，同长度文档按实际词频排序");
    }
}
