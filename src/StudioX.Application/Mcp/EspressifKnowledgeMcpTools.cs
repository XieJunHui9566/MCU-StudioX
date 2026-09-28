namespace StudioX.Application.Mcp;

using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

/// <summary>向内置和外部 Agent 提供乐鑫官方只读资料与组件元数据，不扩大工程或设备权限。</summary>
internal sealed class EspressifKnowledgeMcpTools(McpSessionContext context) : StudioXMcpToolProvider(context)
{
    [McpServerTool(Name = "espressif_docs_status")]
    [Description("查看乐鑫官方 Documentation MCP 的登录状态，不联网、不打开浏览器、不返回令牌。未登录时在 IDE 的 AI 接口设置中连接乐鑫资料；组件数据库工具无需登录。")]
    public Task<string> DocumentationStatusAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(JsonSerializer.Serialize(Services.EspressifDocumentation.GetStatus(),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    }

    [McpServerTool(Name = "espressif_docs_search")]
    [Description("通过已登录的乐鑫官方 Documentation MCP 检索中英文数据手册、技术参考、SDK/API 与设计资料，返回来源链接与片段。仅发送简短公开查询词，不发送本地源码或凭据。知识库主要为最新版本；IDF 5.5.4 工程必须核对本机 SDK 和来源 URL 版本。未登录返回 authentication_required；远端限流如实返回，不反复重试。")]
    public Task<string> DocumentationSearchAsync(
        [Description("单行公开查询词，包括准确 ESP 型号、外设或 API；不要填写源码、密钥或本机路径。")]
        string query,
        [Description("资料语言：zh 中文，en 英文；默认 zh。")]
        string language = "zh",
        CancellationToken cancellationToken = default) =>
        Services.EspressifDocumentation.SearchAsync(query, language, cancellationToken);

    [McpServerTool(Name = "espressif_components_search")]
    [Description("匿名查询乐鑫官方 ESP Component Registry 的组件、驱动、BSP 或示例元数据。只发送公开查询词，不安装依赖。返回最新版信息，使用前核对所选目标和工程的 ESP-IDF 版本。")]
    public Task<string> ComponentsSearchAsync(
        [Description("简短组件名称或功能关键词，例如 button、LVGL、ESP32-S3 LCD。")]
        string query,
        CancellationToken cancellationToken = default) =>
        Services.EspressifComponents.SearchAsync(query, cancellationToken);

    [McpServerTool(Name = "espressif_component_info")]
    [Description("按 namespace/name 读取乐鑫官方组件注册表的最新版组件说明、依赖、版本和文档。只读，不下载或安装组件；不要把最新版兼容性当作 IDF 5.5.4 已验证。")]
    public Task<string> ComponentInformationAsync(
        [Description("组件命名空间，例如 espressif。")]
        string namespaceName,
        [Description("组件名称，例如 button。")]
        string componentName,
        CancellationToken cancellationToken = default) =>
        Services.EspressifComponents.GetInformationAsync(namespaceName, componentName, cancellationToken);
}
