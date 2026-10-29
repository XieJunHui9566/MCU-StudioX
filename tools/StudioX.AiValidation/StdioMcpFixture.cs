using System.ComponentModel;
using ModelContextProtocol.Server;

internal static class StdioMcpFixture
{
    public static async Task RunAsync()
    {
        await using var server = McpServer.Create(new StreamServerTransport(Console.OpenStandardInput(), Console.OpenStandardOutput()), new()
        {
            ServerInfo = new()
            {
                Name = "studiox-stdio-fixture",
                Version = "1.0"
            },
            ToolCollection = new() { McpServerTool.Create(typeof(StdioMcpFixture).GetMethod(nameof(Echo))!) }
        });
        await server.RunAsync();
    }

    [McpServerTool(Name = "echo"), Description("Offline fixture echo")]
    public static string Echo(string text) => text;
}
