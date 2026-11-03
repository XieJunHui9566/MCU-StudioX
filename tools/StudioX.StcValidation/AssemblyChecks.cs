using System.Text.Json;
using System.Text.RegularExpressions;
using StudioX.Engine;
using StudioX.Foundation;
using StudioX.Packages;

internal static class AssemblyChecks
{
    internal static async Task RunAsync(string runtime, string archive, string output, string sdkFixture)
    {
        if (Directory.Exists(output))
        {
            throw new IOException("Use new evidence directory");
        }
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        void Check(bool value, string message)
        {
            if (!value)
            {
                throw new InvalidOperationException(message);
            }
            checks.Add(message);
            Console.WriteLine("PASS " + message);
        }
        async Task Reject(Func<Task> action, string message)
        {
            try
            {
                await action();
                throw new InvalidOperationException("Expected rejection: " + message);
            }
            catch (StudioXException error) when (error.Code == "BUILD_ASSEMBLY") { Check(true, message); }
        }
        var tools = new ToolsetCatalog(Path.Combine(runtime, "toolsets"));
        var builder = new BuildService(tools);
        var viewer = new BuildAssemblyService(tools);
        var pack = await new PackRepository(Path.Combine(output, "packs")).ImportAsync(archive);
        var device = pack.Manifest.Devices.First(device => device.Architecture == "mcs51" && device.FlashBytes >= 32768);
        var root = Path.Combine(output, "8bit 中文 project");
        await new ProjectService().CreateAsync(pack, device.Id, device.Templates[0].Id, "AssemblyView", root);
        var entryPath = Path.Combine(root, "src/main.c");
        await File.WriteAllTextAsync(entryPath, (await File.ReadAllTextAsync(entryPath)).Replace("for (;;)", "while (1)", StringComparison.Ordinal));
        await Reject(async () => { await viewer.ListAsync(root); }, "unbuilt project cannot display old or guessed assembly");
        foreach (var folder in new[] { "one", "two" })
        {
            Directory.CreateDirectory(Path.Combine(root, "src", folder));
            await File.WriteAllTextAsync(Path.Combine(root, "src", folder, "shared.c"), "unsigned char helper_" + folder + "(unsigned char x) { return x + 1; }\n");
        }
        await File.AppendAllTextAsync(Path.Combine(root, "CMakeLists.txt"), "\ntarget_sources(firmware PRIVATE src/one/shared.c src/two/shared.c)\n");
        Check((await builder.BuildAsync(root)).Success, "8-bit SDCC real multi-file build");
        var catalog = await Verify(viewer, root, "8051");
        Check(catalog.Files.Any(file => file.SourcePath == "src/one/shared.c") && catalog.Files.Any(file => file.SourcePath == "src/two/shared.c"),
            "same basename from different directories keeps separate source associations");
        var selected = catalog.Files.First(file => file.RelativePath.EndsWith(".rst", StringComparison.Ordinal));
        var path = Path.Combine(root, selected.RelativePath);
        var original = await File.ReadAllBytesAsync(path);
        await File.AppendAllTextAsync(path, "changed");
        await Reject(async () => { await viewer.ReadAsync(root, catalog, selected); }, "modified assembly is rejected against its build hash");
        await File.WriteAllBytesAsync(path, original);
        await Reject(async () => { await viewer.ReadAsync(root, catalog, selected with { RelativePath = "../outside.asm" }); }, "unlisted and escaping artifact is rejected");
        var source = Path.Combine(root, "src/main.c");
        var text = await File.ReadAllTextAsync(source);
        await File.AppendAllTextAsync(source, "\n/* changed source */\n");
        await Reject(async () => { await viewer.ListAsync(root); }, "changed source cannot masquerade as corresponding assembly");
        await File.WriteAllTextAsync(source, "#error expected failure\n");
        Check(!(await builder.BuildAsync(root)).Success, "actual compile failure revokes successful assembly receipt");
        await Reject(async () => { await viewer.ListAsync(root); }, "failed build leaves no current assembly catalog");
        await File.WriteAllTextAsync(source, text + "\nvolatile unsigned char assembly_test_value;\n");
        Check((await builder.BuildAsync(root)).Success, "8-bit rebuild recovers assembly after failure");
        await Reject(async () => { await viewer.ReadAsync(root, catalog, selected); }, "previous build selection cannot read a newer build silently");
        await Verify(viewer, root, "8051-rebuilt");

        using var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(sdkFixture, "fixture.json")));
        var mxTools = new ToolsetCatalog(fixture.RootElement.GetProperty("tools").GetString()!);
        var mxBuilder = new BuildService(mxTools);
        var mxViewer = new BuildAssemblyService(mxTools);
        foreach (var name in new[] { "calls-small", "ai-fixed-calls" })
        {
            var input = Path.Combine(sdkFixture, "projects/ide", name);
            var project = Path.Combine(output, name);
            Copy(input, project);
            var result = await mxBuilder.BuildAsync(project);
            await File.WriteAllTextAsync(Path.Combine(output, name + ".log"), result.Log);
            Check(result.Success, name + " real MX251 CMake / Ninja build without modifying the SDK");
            var files = await Verify(mxViewer, project, name);
            Check(files.Files.Any(file => file.SourcePath is not null), name + " maps compiler unit names to original source files");
        }
        await JsonStore.WriteAsync(Path.Combine(output, "result.json"), new
        {
            passed = true,
            hardware = false,
            checks,
            sdccProject = root
        });

        async Task<BuildAssemblyCatalog> Verify(BuildAssemblyService service, string project, string label)
        {
            var files = await service.ListAsync(project);
            using var receipt = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(project, ".build/studiox-build-receipt.json")));
            var hex = receipt.RootElement.GetProperty("images")[0].GetProperty("relativePath").GetString()!;
            var memory = HexBytes(await File.ReadAllLinesAsync(Path.Combine(project, hex)));
            Check(files.Files.Any(file => file.RelativePath.EndsWith(".rst", StringComparison.Ordinal)) &&
                files.Files.Any(file => file.RelativePath.EndsWith(".asm", StringComparison.Ordinal)), label + " exposes linked listing and compiler assembly without debug mode");
            foreach (var entry in files.Files.Where(file => file.RelativePath.EndsWith(".rst", StringComparison.Ordinal)).Take(3))
            {
                var document = await service.ReadAsync(project, files, entry);
                Check(Regex.IsMatch(document.Text, @"(?m)^[0-9A-Fa-f]{4,8}\s+[0-9A-Fa-f]{2}\s+") &&
                    document.RawText.Length > 0, label + " reads verified addresses, machine bytes and instructions: " + Path.GetFileName(entry.RelativePath));
                var rows = document.Text.Split('\n').Where(line => line.Length > 0).Take(16).ToArray();
                Check(rows.All(line =>
                {
                    var columns = line.Split("  ", StringSplitOptions.RemoveEmptyEntries);
                    var address = Convert.ToUInt32(columns[0], 16);
                    var encoded = Convert.FromHexString(columns[1].Trim().Replace(" ", "", StringComparison.Ordinal));
                    return encoded.Select((value, offset) => memory.TryGetValue(address + (uint)offset, out var actual) && actual == value).All(value => value);
                }), label + " linked listing bytes match actual HEX addresses: " + Path.GetFileName(entry.RelativePath));
                await File.WriteAllTextAsync(Path.Combine(output, label + "-" + Path.GetFileName(entry.RelativePath) + ".txt"), document.Text);
            }
            await JsonStore.WriteAsync(Path.Combine(output, label + "-catalog.json"), files);
            return files;
        }
    }
    private static Dictionary<uint, byte> HexBytes(string[] lines)
    {
        var result = new Dictionary<uint, byte>();
        uint upper = 0;
        foreach (var line in lines.Where(line => line.StartsWith(':')))
        {
            var record = Convert.FromHexString(line.AsSpan(1));
            var address = (uint)(record[1] * 256 + record[2]);
            if (record[3] == 0)
            {
                for (var index = 0; index < record[0]; index++)
                {
                    result.Add(upper + address + (uint)index, record[4 + index]);
                }
            }
            else if (record[3] == 4)
            {
                upper = (uint)(record[4] * 256 + record[5]) << 16;
            }
            else if (record[3] == 2)
            {
                upper = (uint)(record[4] * 256 + record[5]) << 4;
            }
        }
        return result;
    }
    private static void Copy(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var item in new DirectoryInfo(source).EnumerateFileSystemInfos())
        {
            if (item.Name is ".build" or ".git" || (item.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }
            var target = Path.Combine(destination, item.Name);
            if (item is DirectoryInfo)
            {
                Copy(item.FullName, target);
            }
            else
            {
                File.Copy(item.FullName, target);
            }
        }
    }
}
