namespace StudioX.Application;

using System.Text.Json;

/// <summary>固定的小工具前缀；其余工具按需查询，避免每轮重发整个 MCP schema 集合。</summary>
public sealed class AiToolCatalog(IReadOnlyList<AiToolDefinition> tools)
{
    public const string SearchName = "studiox_tool_search";
    public const string CallName = "studiox_tool_call";
    private static readonly HashSet<string> Core = new(StringComparer.Ordinal)
    {
        "project_info", "project_read_file", "project_search", "project_patch_file",
        "project_build", "editor_context", "editor_read", "editor_search", "editor_plan_changes",
        "editor_apply_plan", "editor_task_status", "skill_read"
    };

    public IReadOnlyList<AiToolDefinition> Definitions
    {
        get;
    } = tools.Where(tool => Core.Contains(tool.Name))
        .Concat([
            new(SearchName, "按名称或用途查找其余 MCP 工具及参数 schema；query 可为关键词或工具名。空查询返回分组索引。用 studiox_tool_call 调用查到的工具。",
                """{"type":"object","properties":{"query":{"type":"string"}},"required":["query"],"additionalProperties":false}"""),
            new(CallName, "调用通过 studiox_tool_search 查到的 MCP 工具；arguments 按返回 schema 填写。保留原工具的权限、校验和错误；不会授予新权限。",
                """{"type":"object","properties":{"name":{"type":"string"},"arguments":{"type":"object"}},"required":["name","arguments"],"additionalProperties":false}""")
        ]).OrderBy(tool => tool.Name, StringComparer.Ordinal).ToArray();

    public string Search(string query)
    {
        if (query.Length > 200)
        {
            throw new ArgumentException("工具查询不能超过 200 字符。");
        }
        var words = query.Split([' ', ',', '，'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0)
        {
            return JsonSerializer.Serialize(new
            {
                groups = tools.GroupBy(tool => tool.Name.Split('_')[0])
                .Select(group => new { group = group.Key, count = group.Count(), names = group.Select(tool => tool.Name) })
            });
        }
        var matches = tools.Select(tool => new
        {
            tool,
            score = words.Sum(word =>
            tool.Name.Equals(word, StringComparison.OrdinalIgnoreCase) ? 100 :
            tool.Name.Contains(word, StringComparison.OrdinalIgnoreCase) ? 10 :
            tool.Description.Contains(word, StringComparison.OrdinalIgnoreCase) ? 1 : 0)
        })
            .Where(item => item.score > 0).OrderByDescending(item => item.score)
            .ThenBy(item => item.tool.Name, StringComparer.Ordinal).ToArray();
        return JsonSerializer.Serialize(new
        {
            tools = matches.Take(6).Select(item => new
            {
                name = item.tool.Name,
                description = item.tool.Description,
                parameters = JsonSerializer.Deserialize<JsonElement>(item.tool.ParametersJson)
            }),
            remaining = Math.Max(0, matches.Length - 6),
            hint = "用更精确的名称查询可获取其余工具。"
        });
    }
}
