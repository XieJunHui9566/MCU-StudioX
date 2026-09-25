namespace StudioX.Application.Mcp;

using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using StudioX.Foundation;

/// <summary>向内置和外部 MCP 客户端传递可修复的业务错误，同时保留 SDK 对未知异常的泛化处理。</summary>
internal sealed class StudioXMcpDiagnosticFunction(AIFunction innerFunction) : DelegatingAIFunction(innerFunction)
{
    private static readonly Regex UrlCredentials = new(
        @"(https?://)[^/\s@]+@", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex SecretAssignment = new(
        @"\b((?:api[_-]?key|access[_-]?token|password|secret|authorization)\s*[:=]\s*)(?:bearer\s+)?[^\s,;]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex BearerToken = new(
        @"\b(bearer\s+)[^\s,;]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        try
        {
            return await InnerFunction.InvokeAsync(arguments, cancellationToken).ConfigureAwait(false);
        }
        catch (StudioXException ex) when (SafeCode(ex.Code) is { } code)
        {
            // 不转发 InnerException、堆栈或未经约束的长文本。
            throw new ModelContextProtocol.McpException($"{code}: {SafeMessage(ex.Message)}");
        }
        catch (ArgumentException ex)
        {
            // 工具的参数范围校验也是模型可修复错误；消息有界脱敏，不转发堆栈或内部异常对象。
            throw new ModelContextProtocol.McpException($"MCP_ARGUMENT: {SafeMessage(ex.Message)}");
        }
    }

    private static string? SafeCode(string code) =>
        code.Length is > 0 and <= 64 && Regex.IsMatch(code, "^[A-Z][A-Z0-9_]*$", RegexOptions.CultureInvariant)
            ? code : null;

    private static string SafeMessage(string message)
    {
        var normalized = string.Concat(message.Take(600).Select(ch => char.IsControl(ch) ? ' ' : ch)).Trim();
        if (normalized.Length == 0) return "MCP 工具操作失败。";
        normalized = UrlCredentials.Replace(normalized, "$1[redacted]@");
        normalized = SecretAssignment.Replace(normalized, "$1[redacted]");
        return BearerToken.Replace(normalized, "$1[redacted]");
    }
}
