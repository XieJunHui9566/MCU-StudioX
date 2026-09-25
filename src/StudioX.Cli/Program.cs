using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;
using StudioX.Application;
using StudioX.Application.Mcp;
using StudioX.Cli;
using StudioX.Engine;
using StudioX.Extensions;
using StudioX.Foundation;
using StudioX.Packages;

Console.OutputEncoding = new UTF8Encoding(false);
using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
try
{
    switch (args)
    {
        case ["help"] or ["--help"] or ["-h"]:
            ShowHelp(); return 0;
        case ["mcp", var mcpProject, .. var mcpArguments] when mcpArguments.Length <= 1:
            await RunMcpAsync(mcpProject,
                mcpArguments.Length == 0 ? null : mcpArguments[0], cancel.Token);
            return 0;
        case ["pack", var source, var output]:
            await PackArchiveWriter.WriteAsync(source, output, cancel.Token);
            Console.WriteLine(Path.GetFullPath(output)); break;
        case ["import", var archive, var repository]:
            Print(await new PackRepository(repository).ImportAsync(archive, cancel.Token)); break;
        case ["create", var repository, var packId, var version, var device, var template, var name, var destination]:
            var pack = (await new PackRepository(repository).ListCatalogAsync(cancel.Token)).Single(p => p.Manifest.Id == packId && p.Manifest.Version == version);
            Print(await new ProjectService().CreateAsync(pack, device, template, name, destination, cancel.Token)); break;
        case ["project-info", var infoProject]:
            Print(await ProjectService.ReadAsync(infoProject, cancel.Token)); break;
        case ["list-files", var listProject]:
            _ = await ProjectService.ReadAsync(listProject, cancel.Token);
            Print(new ProjectFileService().List(listProject)); break;
        case ["list-files", var listProject, var relativeDirectory]:
            _ = await ProjectService.ReadAsync(listProject, cancel.Token);
            Print(new ProjectFileService().List(listProject, relativeDirectory)); break;
        case ["read-file", var readProject, var relativePath]:
            _ = await ProjectService.ReadAsync(readProject, cancel.Token);
            var document = await new ProjectFileService().ReadAsync(readProject, relativePath, cancel.Token);
            Print(new
            {
                document.RelativePath,
                document.Text,
                Encoding = document.Encoding.WebName,
                document.DiskHash,
                document.IsReadOnly,
                document.ReadOnlyReason
            });
            break;
        case ["build", var project, var toolsets]:
            var report = await new BuildService(new ToolsetCatalog(toolsets)).BuildAsync(project, new Progress<string>(Console.WriteLine), cancel.Token);
            Console.WriteLine(report.Log); Print(report.Artifacts); return report.Success ? 0 : 1;
        case ["inspect-cubemx", var source, var toolsets]:
            Print(await new CubeMxImportService(new ToolsetCatalog(toolsets)).InspectAsync(source, cancel.Token)); break;
        case ["import-cubemx", var source, var toolsets, var preset]:
            Print(await new CubeMxImportService(new ToolsetCatalog(toolsets)).ImportAsync(source, preset == "-" ? null : preset, token: cancel.Token)); break;
        case ["decode", var host, var manifest, var payload]:
            Print(await new PluginClient(host).DecodeAsync(manifest, Encoding.UTF8.GetBytes(payload), cancel.Token)); break;
        default:
            ShowHelp();
            return 2;
    }
    return 0;
}
catch (OperationCanceledException) { Console.Error.WriteLine("CANCELLED"); return 130; }
catch (Exception ex) { Console.Error.WriteLine(ex is StudioXException studio ? $"{studio.Code}: {studio.Message}" : ex.ToString()); return 1; }
static void Print<T>(T value) => Console.WriteLine(JsonSerializer.Serialize(value, JsonStore.Options));
static async Task RunMcpAsync(string project, string? runtimeArgument, CancellationToken token)
{
    if (!Path.IsPathFullyQualified(project))
        throw new StudioXException("MCP_PROJECT", "MCP 工程路径必须是绝对路径。");
    var projectDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(project));
    if (!Directory.Exists(projectDirectory) ||
        (File.GetAttributes(projectDirectory) & FileAttributes.ReparsePoint) != 0)
        throw new StudioXException("MCP_PROJECT", "MCP 工程不存在或是链接目录。");
    _ = await ProjectService.ReadAsync(projectDirectory, token);

    // 便携版将 MCP 主机放在 runtime/mcp-host，工具链位于其父目录 runtime。
    var runtimeDirectory = Path.GetFullPath(runtimeArgument is null
        ? Path.Combine(AppContext.BaseDirectory, "..") : runtimeArgument);
    if (!Directory.Exists(runtimeDirectory))
        throw new StudioXException("MCP_RUNTIME", $"MCP 运行时目录不存在：{runtimeDirectory}");
    var dataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MCUStudioX");
    await using var services = new WorkbenchService(runtimeDirectory, dataDirectory);
    await using var tools = new StudioXMcpTools(services, projectDirectory, new ExternalMcpAuthorizer());
    await using var server = McpServer.Create(new StdioServerTransport("MCU StudioX"),
        new McpServerOptions { ToolCollection = [.. tools.CreateToolCollection()] });
    await server.RunAsync(token);
}
static void ShowHelp() => Console.WriteLine("""
    StudioX CLI
      pack <source> <output.mcupack>
      import <pack> <repository>
      create <repository> <pack-id> <version> <device> <template> <name> <destination>
      project-info <project>
      list-files <project> [relative-directory]
      read-file <project> <relative-path>
      mcp <absolute-project> [runtime-directory]
      build <project> <toolsets-root>
      inspect-cubemx <source> <toolsets-root>
      import-cubemx <source> <toolsets-root> <preset|->
      decode <host.exe> <plugin.json> <text>
      help

    The MCP command is a stdio server; stdout is reserved for protocol messages.
    Mutating MCP tools require a separate Windows approval dialog for each call.
    Read-only commands return JSON on stdout. Paths within a project use forward slashes.
    """);
