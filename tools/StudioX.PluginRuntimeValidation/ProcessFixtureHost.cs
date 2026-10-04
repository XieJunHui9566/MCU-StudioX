namespace StudioX.PluginRuntimeValidation;

using System.Text.Json;
using StudioX.Extensions;
using StudioX.Extensions.Abstractions;

/// <summary>实现同一 JSON 行协议的固定 EXE 夹具，模拟非 .NET SDK 的语言宿主。</summary>
internal static class ProcessFixtureHost
{
    public static async Task<int> RunAsync(bool wrongVersion)
    {
        var protocol = Console.Out;
        Console.SetOut(Console.Error);
        if (wrongVersion)
        {
            _ = await Console.In.ReadLineAsync();
            await protocol.WriteLineAsync("{\"protocolVersion\":1,\"kind\":\"response\",\"requestId\":\"wrong\",\"payload\":{}}");
            return 0;
        }
        var plugin = new ValidationPlugin();
        PluginProtocolConnection? connection = null;
        connection = new PluginProtocolConnection(Console.In, protocol, async (method, payload, token) =>
        {
            switch (method)
            {
                case "describe":
                    return JsonSerializer.SerializeToElement(plugin.Describe());
                case "activate":
                    await plugin.ActivateAsync(new Bridge(connection!), token);
                    return JsonSerializer.SerializeToElement(new
                    {
                        active = true
                    });
                case "invoke":
                    return await plugin.InvokeAsync(payload.GetProperty("kind").GetString()!, payload.GetProperty("id").GetString()!,
                        payload.GetProperty("arguments"), token);
                case "deactivate":
                    await plugin.DeactivateAsync(token);
                    return JsonSerializer.SerializeToElement(new
                    {
                        active = false
                    });
                default:
                    throw new InvalidOperationException(method);
            }
        }, (_, _, _) => Task.CompletedTask);
        try
        {
            await connection.Completion;
        }
        catch (Exception exception)
        {
            await Console.Error.WriteLineAsync(exception.ToString());
        }
        finally
        {
            await connection.DisposeAsync();
        }
        return 0;
    }

    private sealed class Bridge(PluginProtocolConnection connection) : IPluginHost
    {
        public Task<JsonElement> CallAsync(string tool, JsonElement arguments, CancellationToken cancellationToken)
        {
            return connection.RequestAsync("hostCall", JsonSerializer.SerializeToElement(new
            {
                tool,
                arguments
            }), TimeSpan.FromSeconds(10), cancellationToken);
        }

        public Task PublishPanelAsync(PluginPanelDefinition panel, CancellationToken cancellationToken)
        {
            return connection.PublishAsync("panel", JsonSerializer.SerializeToElement(panel), cancellationToken);
        }

        public Task LogAsync(string level, string message, CancellationToken cancellationToken)
        {
            return connection.PublishAsync("log", JsonSerializer.SerializeToElement(new
            {
                level,
                message
            }), cancellationToken);
        }
    }
}
