namespace StudioX.Application.Mcp;

using System.Text.Json;
using Microsoft.Extensions.AI;

/// <summary>保留插件原始 JSON 参数模式，通过工作区会话调用独立宿主。</summary>
internal sealed class PluginMcpFunction(string name, string description, JsonElement schema,
    Func<JsonElement, CancellationToken, Task<JsonElement>> invoke) : AIFunction
{
    public override string Name => name;
    public override string Description => description;
    public override JsonElement JsonSchema => schema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToElement(arguments);
        return await invoke(payload, cancellationToken).ConfigureAwait(false);
    }
}
