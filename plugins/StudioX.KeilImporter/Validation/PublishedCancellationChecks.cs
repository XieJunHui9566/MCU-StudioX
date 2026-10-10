namespace StudioX.KeilImporter.Validation;

using System.Text.Json;
using StudioX.Extensions.Abstractions;

/// <summary>使用原工程的只读输入和可控 CLI，验证副本发布后取消编译仍保留打开入口。</summary>
internal static class PublishedCancellationChecks
{
    public static async Task RunAsync(ImportRequest input, string scratch, Action<string> passed, CancellationToken token)
    {
        var runtime = Path.Combine(scratch, "cancellation-runtime");
        var cliDirectory = Path.Combine(runtime, "mcp-host");
        Directory.CreateDirectory(cliDirectory);
        Directory.CreateDirectory(Path.Combine(runtime, "toolsets"));
        foreach (var file in Directory.GetFiles(AppContext.BaseDirectory))
        {
            File.Copy(file, Path.Combine(cliDirectory, Path.GetFileName(file)));
        }
        var cli = Path.Combine(cliDirectory, "StudioX.Cli.exe");
        File.Copy(Environment.ProcessPath!, cli);
        await File.WriteAllTextAsync(Path.Combine(cliDirectory, "cancel-published"), "controlled CLI fixture", token);
        var host = new Host();
        var plugin = new KeilImporterPlugin();
        await plugin.ActivateAsync(host, token);
        var form = new Dictionary<string, object?>
        {
            ["projectFile"] = input.ProjectFile,
            ["sourceRoot"] = input.SourceRoot,
            ["target"] = "",
            ["projectName"] = "published-cancel",
            ["parent"] = scratch,
            ["packs"] = input.PackRepository,
            ["cli"] = cli,
            ["query"] = input.Device.Id,
            ["device"] = "",
            ["template"] = ""
        };
        await Invoke("load");
        form["target"] = input.TargetName;
        await Invoke("search");
        form["device"] = input.Device.Key;
        await Invoke("templates");
        form["template"] = input.TemplateId;
        var preview = await Invoke("preview");
        Check(preview.GetProperty("canCreate").GetBoolean(), "cancelled compilation starts from validated migration preview");
        var checkbox = host.Latest!.Widgets.Single(widget => widget.Kind == "form").Children!.Single(widget => widget.Kind == "checkbox");
        form[checkbox.Id] = true;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var creation = plugin.InvokeAsync("command", "create", JsonSerializer.SerializeToElement(new
        {
            values = form
        }), cancellation.Token);
        var project = Path.Combine(scratch, "published-cancel");
        var ready = Path.Combine(project, ".studiox/cli-ready");
        while (!File.Exists(ready))
        {
            if (creation.IsCompleted)
            {
                await creation;
                throw new InvalidOperationException("Published cancellation fixture ended before compilation.");
            }
            await Task.Delay(20, token);
        }
        await Task.Delay(200, token);
        var queuedRefresh = plugin.InvokeAsync("command", "open", JsonSerializer.SerializeToElement(new { }), token);
        await Task.Delay(50, token);
        Check(!queuedRefresh.IsCompleted, "new panel request waits for in-flight migration state to settle");
        cancellation.Cancel();
        try
        {
            await creation;
            throw new InvalidOperationException("Published cancellation did not propagate.");
        }
        catch (ProjectCreationCanceledException error)
        {
            Check(error.Result.Directory == project && !error.Result.CompilationVerified, "published cancellation reports retained directory without compiled status");
        }
        await queuedRefresh;
        var link = host.Latest!.Widgets.Single(widget => widget.Kind == "projectLink");
        Check(link.Value!.Value.GetProperty("directory").GetString() == project && File.Exists(Path.Combine(project, ".studiox/project.json")),
            "cancelled plugin retains explicit open-project button for published copy");
        var log = await File.ReadAllTextAsync(Path.Combine(project, PortCompilation.LogPath), token);
        Check(log.Contains("fixture stdout", StringComparison.Ordinal) && log.Contains("fixture stderr", StringComparison.Ordinal),
            "published cancellation retains both original diagnostic channels");
        Check(!Directory.EnumerateDirectories(scratch, ".studiox-keil-import-*").Any(), "published cancellation removes only owned staging");
        await plugin.DeactivateAsync(token);

        Task<JsonElement> Invoke(string command) => plugin.InvokeAsync("command", command, JsonSerializer.SerializeToElement(new
        {
            values = form
        }), token);
        void Check(bool success, string name)
        {
            if (!success)
            {
                throw new InvalidOperationException(name);
            }
            passed(name);
        }
    }

    private sealed class Host : IPluginHost
    {
        public PluginPanelDefinition? Latest
        {
            get; private set;
        }
        public Task<JsonElement> CallAsync(string tool, JsonElement arguments, CancellationToken token) => throw new InvalidOperationException("Unexpected host tool.");
        public Task PublishPanelAsync(PluginPanelDefinition panel, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Latest = panel;
            return Task.CompletedTask;
        }
        public Task LogAsync(string level, string message, CancellationToken token) => Task.CompletedTask;
    }
}
