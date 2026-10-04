using StudioX.Application.CodeIntelligence;
using StudioX.Application.PeripheralDevelopment;
using StudioX.Engine;
using StudioX.Foundation;

internal static class PeripheralDiagnosticChecks
{
    public static async Task RunAsync(string output, string runtime, string projects)
    {
        if (Directory.Exists(output) || File.Exists(output))
        {
            throw new IOException("Use a new diagnostic output directory.");
        }
        Directory.CreateDirectory(output);
        var results = new List<object>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        foreach (var directory in Directory.EnumerateDirectories(projects))
        {
            var peripherals = new PeripheralDevelopmentService(new ToolsetCatalog(Path.Combine(runtime, "toolsets")));
            var context = await peripherals.ReadAsync(directory, deadline.Token);
            var sourceFiles = Directory.GetFiles(Path.Combine(directory, "main"), "*_assist.c");
            if (sourceFiles.Length != 8)
            {
                throw new InvalidOperationException("Need all eight generated peripheral files.");
            }
            await using var service = new CodeIntelligenceService(runtime, Path.Combine(output, Path.GetFileName(directory)));
            await service.StartAsync(directory, deadline.Token);
            foreach (var sourceFile in sourceFiles)
            {
                var path = "main/" + Path.GetFileName(sourceFile);
                var text = await File.ReadAllTextAsync(sourceFile, deadline.Token);
                var option = context.Options.Single(item => item.Id == Path.GetFileName(sourceFile)[..^"_assist.c".Length]);
                var values = option.Parameters.ToDictionary(parameter => parameter.Id, parameter => parameter.DefaultValue, StringComparer.Ordinal);
                values["name"] = option.Id;
                var pin = 0;
                foreach (var parameter in option.Parameters.Where(parameter => parameter.Id is "pin" or "tx" or "rx" or "sda" or "scl" or "mosi" or "miso" or "sclk" or "cs"))
                {
                    values[parameter.Id] = (pin++).ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
                var component = await peripherals.ReadComponentAsync(context, path, [], deadline.Token);
                var sourceBeforeInsertion = component.Source with
                {
                    Text = "// original source\n"
                };
                var addition = peripherals.PrepareAddition(context, component with
                {
                    Source = sourceBeforeInsertion,
                    SourceWasOpen = true
                }, peripherals.Generate(context, option.Id, values));
                await peripherals.ValidateAdditionAsync(addition, [sourceBeforeInsertion], deadline.Token);
                if (addition.Files[0].After != text || addition.AddedComponents.Count != 0 || addition.Files[1].Before != addition.Files[1].After)
                {
                    throw new InvalidOperationException("Current peripheral service differs from actually compiled source or dependencies: " + path);
                }
                await service.SynchronizeDiagnosticsAsync(path, text, [new(path, text)], deadline.Token);
                CodeDiagnosticBatch? batch = null;
                for (var attempt = 0; attempt < 200; attempt++)
                {
                    batch = service.GetDiagnostics().SingleOrDefault(item => item.Path == path && item.Text == text && item.IsComplete);
                    if (batch is not null)
                    {
                        break;
                    }
                    await Task.Delay(100, deadline.Token);
                }
                if (batch is null)
                {
                    throw new InvalidOperationException("No current diagnostic batch: " + path + "\n" + string.Join('\n', service.DrainLog()));
                }
                var errors = batch.Items.Where(diagnostic => diagnostic.Severity == 1).ToArray();
                results.Add(new
                {
                    project = directory,
                    path,
                    success = errors.Length == 0,
                    generationMatchesBuild = true,
                    errors,
                    hardware = false
                });
                await JsonStore.WriteAsync(Path.Combine(output, "result.json"), new
                {
                    results,
                    hardware = false
                }, deadline.Token);
                if (errors.Length > 0)
                {
                    throw new InvalidOperationException(path + ": " + string.Join("; ", errors.Select(e => e.Message)));
                }
                Console.WriteLine("PASS " + Path.GetFileName(directory) + "/" + path + " real clangd parses generated SDK APIs without errors");
            }
            await File.WriteAllLinesAsync(Path.Combine(output, Path.GetFileName(directory) + ".log"), service.DrainLog(), deadline.Token);
        }
        if (results.Count != 16)
        {
            throw new InvalidOperationException("Need two SDKs × eight peripheral sources.");
        }
    }
}
