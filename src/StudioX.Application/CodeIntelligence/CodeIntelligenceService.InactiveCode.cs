namespace StudioX.Application.CodeIntelligence;

using System.Text.Json;
using StudioX.Foundation;

public sealed partial class CodeIntelligenceService
{
    private readonly Dictionary<string, CodeInactiveRegionBatch> inactiveRegionBatches = new(StringComparer.OrdinalIgnoreCase);
    private int inactiveTokenType = -1;
    private int semanticTokenTypeCount;

    public IReadOnlyList<CodeInactiveRegionBatch> GetInactiveRegions()
    {
        lock (diagnosticStateLock)
        {
            return DiagnosticsSuspended || !IsReady ? [] : inactiveRegionBatches.Values.ToArray();
        }
    }

    private void ConfigureInactiveCode(JsonElement initialization)
    {
        inactiveTokenType = -1;
        semanticTokenTypeCount = 0;
        if (!initialization.TryGetProperty("capabilities", out var capabilities) ||
            !capabilities.TryGetProperty("semanticTokensProvider", out var provider) ||
            provider.ValueKind != JsonValueKind.Object || !provider.TryGetProperty("full", out var full) ||
            full.ValueKind is not (JsonValueKind.True or JsonValueKind.Object) ||
            !provider.TryGetProperty("legend", out var legend) || !legend.TryGetProperty("tokenTypes", out var types) ||
            types.ValueKind != JsonValueKind.Array)
        {
            return;
        }
        var names = types.EnumerateArray().Select(type => type.GetString()).ToArray();
        // 内置 clangd 将 InactiveCode 映射为 comment；未声明该类型时不自行猜测宏条件。
        inactiveTokenType = Array.IndexOf(names, "comment");
        semanticTokenTypeCount = names.Length;
    }

    private async Task<CodeInactiveRegionBatch?> ReadInactiveCodeCoreAsync(string path, long revision, CancellationToken token)
    {
        if (inactiveTokenType < 0)
        {
            return null;
        }
        var uri = new Uri(ResolveDocumentPath(path)).AbsoluteUri;
        (int Version, string Text, long Revision) expected;
        lock (diagnosticStateLock)
        {
            if (revision != diagnosticRevision || DiagnosticsSuspended ||
                !diagnosticDocuments.TryGetValue(uri, out expected) || expected.Revision != revision || invalidatedDiagnostics.ContainsKey(uri))
            {
                return null;
            }
            if (inactiveRegionBatches.TryGetValue(uri, out var cached) && cached.Version == expected.Version)
            {
                return cached;
            }
        }
        JsonElement response = default;
        try
        {
            // 专用 inactiveRegions 通知没有文档版本；使用串行同步后的请求，才能绑定确切文本快照。
            response = await connection!.RequestAsync("textDocument/semanticTokens/full", new
            {
                textDocument = new
                {
                    uri
                }
            }, token).ConfigureAwait(false);
            if (response.ValueKind == JsonValueKind.Null)
            {
                return null;
            }
            var regions = InactiveCodeTokens.Decode(response, expected.Text, inactiveTokenType, semanticTokenTypeCount);
            lock (diagnosticStateLock)
            {
                return revision == diagnosticRevision && !DiagnosticsSuspended &&
                    diagnosticDocuments.TryGetValue(uri, out var current) && current == expected && !invalidatedDiagnostics.ContainsKey(uri)
                    ? new(projectRoot, path, expected.Text, expected.Version, regions) : null;
            }
        }
        catch (Exception ex) when (ex is StudioXException or JsonException or ArgumentException or InvalidOperationException or
            KeyNotFoundException or FormatException or OverflowException)
        {
            log.Enqueue("未启用代码范围暂不可用：" + path + "\n" + ex +
                (response.ValueKind == JsonValueKind.Undefined ? "" : "\n" + response.GetRawText()));
            return null;
        }
    }
}
