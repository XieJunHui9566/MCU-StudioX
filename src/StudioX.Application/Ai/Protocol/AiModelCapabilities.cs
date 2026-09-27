namespace StudioX.Application;

/// <summary>集中描述已核实的服务商能力，避免把专有字段发送到未知兼容端点。</summary>
internal static class AiModelCapabilities
{
    internal static bool IsOfficialDeepSeek(Uri endpoint) =>
        endpoint.Host.Equals("api.deepseek.com", StringComparison.OrdinalIgnoreCase);

    internal static bool SupportsInlineImages(string model, bool officialDeepSeek) =>
        officialDeepSeek && model is "deepseek-flash" or "deepseek-v4-flash-vision-exp";
}
