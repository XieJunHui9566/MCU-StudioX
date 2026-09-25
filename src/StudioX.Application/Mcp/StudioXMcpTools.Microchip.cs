namespace StudioX.Application.Mcp;

using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

/// <summary>仅把明确的检索词发送到 Microchip 官方只读 MCP；不会上传工程或本地器件包。</summary>
public sealed partial class StudioXMcpTools
{
    private static readonly Uri MicrochipMcpEndpoint = new("https://api.microchip.com/mcp/resources");
    private const int MicrochipMaximumResponseChars = 12_000;

    [McpServerTool(Name = "microchip_search_products")]
    [Description("在线检索 Microchip 官方器件数据库的型号、产品信息和数据手册链接。仅适用于 Microchip/PIC/AVR/SAM/dsPIC；只发送 searchTerm，不发送本地工程。网络不可用时返回 unavailable。")]
    public Task<string> MicrochipSearchProductsAsync(
        [Description("Microchip 型号或简短产品关键词，例如 ATmega328P。不要填写工程文件内容。")]
        string searchTerm,
        [Description("返回结果数，1–10，默认 5。")]
        int limit = 5,
        [Description("跳过的结果数，0–200，默认 0。")]
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        var term = ValidateMicrochipQuery(searchTerm, nameof(searchTerm), 120);
        if (limit is < 1 or > 10) throw new ArgumentOutOfRangeException(nameof(limit), "范围为 1–10。");
        if (offset is < 0 or > 200) throw new ArgumentOutOfRangeException(nameof(offset), "范围为 0–200。");
        return CallMicrochipAsync("search_products", new Dictionary<string, object?>
        {
            ["searchTerm"] = term,
            ["limit"] = limit,
            ["offset"] = offset
        }, cancellationToken);
    }

    [McpServerTool(Name = "microchip_product_profile")]
    [Description("按准确 Microchip 型号取得官方完整产品概况、规格和数据手册链接。只发送 partNumber；在线资料须与本机已核验器件包和具体料号交叉检查。")]
    public Task<string> MicrochipProductProfileAsync(
        [Description("准确的 Microchip 订货型号，例如 ATMEGA328P-PU。")]
        string partNumber,
        CancellationToken cancellationToken = default)
    {
        var part = ValidateMicrochipPartNumber(partNumber);
        return CallMicrochipAsync("get_full_product_profile", new Dictionary<string, object?>
        {
            ["partNumber"] = part
        }, cancellationToken);
    }

    [McpServerTool(Name = "microchip_search_documents")]
    [Description("在线搜索 Microchip 官方产品文档、数据手册与支持文章，返回来源链接和截断的文档摘要。此检索可能返回同系列其他型号的内容，编程前需核对准确料号及文档版本。")]
    public Task<string> MicrochipSearchDocumentsAsync(
        [Description("简短器件型号和外设/寄存器关键词，例如 ATmega328P Timer0 register。不要填写工程文件内容。")]
        string query,
        [Description("返回文档数，1–3，默认 2。")]
        int limit = 2,
        CancellationToken cancellationToken = default)
    {
        var term = ValidateMicrochipQuery(query, nameof(query), 200);
        if (limit is < 1 or > 3) throw new ArgumentOutOfRangeException(nameof(limit), "范围为 1–3。");
        return CallMicrochipAsync("search_microchip_product_documents", new Dictionary<string, object?>
        {
            ["query"] = term,
            ["limit"] = limit
        }, cancellationToken);
    }

    private static string ValidateMicrochipQuery(string? query, string name, int maximumLength)
    {
        var term = query?.Trim() ?? "";
        if (term.Length is 0 || term.Length > maximumLength || term.Any(char.IsControl) ||
            term.Contains('\\') || term.Contains('{') || term.Contains('}'))
            throw new ArgumentException($"必须提供 1–{maximumLength} 字符的单行器件查询词。", name);
        return term;
    }

    private static string ValidateMicrochipPartNumber(string? partNumber)
    {
        var part = partNumber?.Trim() ?? "";
        if (part.Length is 0 or > 64 || !char.IsAsciiLetterOrDigit(part[0]) ||
            part.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch is not ('-' or '_' or '.' or '/' or '+')))
            throw new ArgumentException("请输入不超过 64 字符的准确 Microchip 订货型号。", nameof(partNumber));
        return part;
    }

    private static async Task<string> CallMicrochipAsync(string remoteTool,
        Dictionary<string, object?> arguments, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            var transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = MicrochipMcpEndpoint,
                TransportMode = HttpTransportMode.StreamableHttp,
                EnableStandaloneGetStream = false,
                ConnectionTimeout = TimeSpan.FromSeconds(10)
            });
            await using var client = await McpClient.CreateAsync(transport,
                cancellationToken: timeout.Token).ConfigureAwait(false);
            var result = await client.CallToolAsync(remoteTool, arguments,
                cancellationToken: timeout.Token).ConfigureAwait(false);
            var raw = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(item => item.Text));
            if (result.IsError != true && remoteTool == "search_microchip_product_documents")
            {
                var documents = SummarizeMicrochipDocuments(raw, remoteTool);
                if (documents is not null) return documents;
            }
            return JsonSerializer.Serialize(new
            {
                status = result.IsError == true ? "remote_error" : "ok",
                source = "Microchip Technology official MCP",
                endpoint = MicrochipMcpEndpoint.AbsoluteUri,
                remoteTool,
                retrievedAtUtc = DateTimeOffset.UtcNow,
                truncated = raw.Length > MicrochipMaximumResponseChars,
                responseExcerpt = LimitOutput(raw, MicrochipMaximumResponseChars)
            });
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return MicrochipUnavailable(remoteTool, "官方 MCP 查询超时。");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return MicrochipUnavailable(remoteTool,
                $"官方 MCP 暂不可用：{LimitOutput(ex.Message, 300)}");
        }
    }

    private static string MicrochipUnavailable(string remoteTool, string message) =>
        JsonSerializer.Serialize(new
        {
            status = "unavailable",
            source = "Microchip Technology official MCP",
            endpoint = MicrochipMcpEndpoint.AbsoluteUri,
            remoteTool,
            message
        });

    private static string? SummarizeMicrochipDocuments(string raw, string remoteTool)
    {
        try
        {
            using var parsed = JsonDocument.Parse(raw);
            if (!parsed.RootElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("documents", out var documents) ||
                documents.ValueKind != JsonValueKind.Array)
                return null;

            var summarized = documents.EnumerateArray().Take(3).Select(item => new
            {
                title = MicrochipString(item, "title", 300),
                url = MicrochipString(item, "url", 1_000),
                contentExcerpt = MicrochipString(item, "content", 2_500),
                truncated = item.TryGetProperty("content", out var content) &&
                    content.ValueKind == JsonValueKind.String && content.GetString()!.Length > 2_500
            }).ToArray();
            return JsonSerializer.Serialize(new
            {
                status = "ok",
                source = "Microchip Technology official MCP",
                endpoint = MicrochipMcpEndpoint.AbsoluteUri,
                remoteTool,
                retrievedAtUtc = DateTimeOffset.UtcNow,
                totalResults = data.TryGetProperty("totalResults", out var total) &&
                    total.TryGetInt32(out var count) ? count : (int?)null,
                documents = summarized,
                note = "外部检索结果可能包含相邻器件资料；请核对准确料号、文档链接和版本。"
            });
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? MicrochipString(JsonElement item, string name, int maximumLength) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? LimitOutput(value.GetString() ?? "", maximumLength)
            : null;
}
