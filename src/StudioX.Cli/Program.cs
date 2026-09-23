using System.Text;
using System.Text.Json;
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
        case ["pack", var source, var output]:
            await PackArchiveWriter.WriteAsync(source, output, cancel.Token);
            Console.WriteLine(Path.GetFullPath(output)); break;
        case ["import", var archive, var repository]:
            Print(await new PackRepository(repository).ImportAsync(archive, cancel.Token)); break;
        case ["create", var repository, var packId, var version, var device, var template, var name, var destination]:
            var pack = (await new PackRepository(repository).ListCatalogAsync(cancel.Token)).Single(p => p.Manifest.Id == packId && p.Manifest.Version == version);
            Print(await new ProjectService().CreateAsync(pack, device, template, name, destination, cancel.Token)); break;
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
            Console.WriteLine("StudioX CLI 0.1\n  pack <source> <output.mcupack>\n  import <pack> <repository>\n  create <repository> <pack-id> <version> <device> <template> <name> <destination>\n  build <project> <toolsets-root>\n  decode <host.exe> <plugin.json> <text>");
            return 2;
    }
    return 0;
}
catch (OperationCanceledException) { Console.Error.WriteLine("CANCELLED"); return 130; }
catch (Exception ex) { Console.Error.WriteLine(ex is StudioXException studio ? $"{studio.Code}: {studio.Message}" : ex.ToString()); return 1; }
static void Print<T>(T value) => Console.WriteLine(JsonSerializer.Serialize(value, JsonStore.Options));
