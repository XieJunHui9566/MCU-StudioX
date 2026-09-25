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
            query = "HAL_GPIO_TogglePin", directory = "src", maxResults = 5
        });
        var first = await session.CallToolAsync("project_qmd_search", request);
        if (first.Contains("MCP_QMD_NOT_INSTALLED", StringComparison.Ordinal))
        {
            check(true, "QMD 缺失时返回可操作的安装提示；无需联网或下载模型");
            return;
        }
        using var firstJson = JsonDocument.Parse(first);
        var firstRoot = firstJson.RootElement;
        check(firstRoot.GetProperty("results").EnumerateArray().Any(item =>
                item.GetProperty("path").GetString() == "src/qmd_led_fixture.c" &&
                item.GetProperty("snippet").GetString()!.Contains("GPIO_PIN_9", StringComparison.Ordinal)) &&
            !firstRoot.GetProperty("reusedIndex").GetBoolean(),
            "QMD BM25 在临时工程中建立有界索引并返回相对路径、行号和片段");

        var indexRoot = Path.Combine(services.DataDirectory, "qmd-bm25");
        var indexDb = Directory.EnumerateFiles(indexRoot, "studiox.sqlite", SearchOption.AllDirectories)
            .Single();
        var snapshotState = Directory.EnumerateFiles(indexRoot, "snapshot.json", SearchOption.AllDirectories)
            .Single();
        var modified = File.GetLastWriteTimeUtc(snapshotState);
        var second = await session.CallToolAsync("project_qmd_search", request);
        using var secondJson = JsonDocument.Parse(second);
        check(secondJson.RootElement.GetProperty("reusedIndex").GetBoolean() &&
            secondJson.RootElement.GetProperty("results").GetArrayLength() > 0 &&
            File.GetLastWriteTimeUtc(snapshotState) == modified && File.Exists(indexDb),
            "未变更工程的第二次 QMD 搜索复用索引，不再调用 update");
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
            query = "uniqueQmdSecretNeedle", directory = "src", maxResults = 5
        }));
        using var excludedJson = JsonDocument.Parse(excluded);
        check(excludedJson.RootElement.GetProperty("results").GetArrayLength() == 0 &&
            excludedJson.RootElement.GetProperty("reusedIndex").GetBoolean(),
            "QMD 索引排除凭据名称源码且不因被排除文件而重建");

        await File.WriteAllTextAsync(fixture,
            "void studioxQmdFixture(void) { HAL_GPIO_WritePin(GPIOF, GPIO_PIN_10, GPIO_PIN_SET); }\n");
        var changed = await session.CallToolAsync("project_qmd_search", JsonSerializer.Serialize(new
        {
            query = "HAL_GPIO_WritePin", directory = "src", maxResults = 5
        }));
        using var changedJson = JsonDocument.Parse(changed);
        check(!changedJson.RootElement.GetProperty("reusedIndex").GetBoolean() &&
            changedJson.RootElement.GetProperty("results").EnumerateArray().Any(item =>
                item.GetProperty("path").GetString() == "src/qmd_led_fixture.c"),
            "源码修改后 QMD 快照重新索引并检索新内容");
    }
}
