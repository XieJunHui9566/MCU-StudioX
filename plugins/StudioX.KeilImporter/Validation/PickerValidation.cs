namespace StudioX.KeilImporter.Validation;

using System.Text.Json;
using StudioX.Extensions.Abstractions;

internal static class PickerValidation
{
    public static async Task<int> NativeSmokeAsync(string scratch)
    {
        System.Diagnostics.Trace.Listeners.Add(new System.Diagnostics.TextWriterTraceListener(Console.Error));
        System.Diagnostics.Trace.AutoFlush = true;
        Directory.CreateDirectory(scratch);
        var checks = new List<string>();
        var originalDirectory = Environment.CurrentDirectory;
        var originalDrive = Environment.GetEnvironmentVariable("SystemDrive");
        var originalProgramData = Environment.GetEnvironmentVariable("ProgramData");
        try
        {
            // 复现安装版宿主的最小环境，检查 Shell 缓存不会写入插件工作目录。
            Environment.SetEnvironmentVariable("SystemDrive", null, EnvironmentVariableTarget.Process);
            Environment.SetEnvironmentVariable("ProgramData", null, EnvironmentVariableTarget.Process);
            Environment.CurrentDirectory = Path.GetFullPath(scratch);
            var project = Path.Combine(scratch, "测试 工程中文.uvprojx");
            var legacy = Path.Combine(scratch, "测试 工程中文.uvproj");
            var cli = Path.Combine(scratch, "StudioX.Cli.exe");
            await File.WriteAllTextAsync(project, "<Project />");
            await File.WriteAllTextAsync(legacy, "<Project />");
            await File.WriteAllTextAsync(cli, "selection fixture; never executed");
            foreach (var request in new[]
            {
                new PathPickerRequest("", false, project, "Keil 工程", "*.uvprojx;*.uvproj"),
                new PathPickerRequest("", false, legacy, "Keil 工程", "*.uvprojx;*.uvproj"),
                new PathPickerRequest("", true, scratch, "", ""),
                new PathPickerRequest("", false, cli, "StudioX CLI", "StudioX.Cli.exe")
            })
            {
                var title = "StudioX 自有窗口选择验证 " + Guid.NewGuid().ToString("N");
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                var choose = NativeDialogProbe.PressOwnDialogAsync(title, 1, deadline.Token);
                var result = await new WindowsPathPicker().PickAsync(request with { Title = title }, deadline.Token).WaitAsync(TimeSpan.FromSeconds(12));
                await choose;
                if (!string.Equals(result, request.InitialPath, StringComparison.OrdinalIgnoreCase)) { throw new InvalidOperationException("Native selected path mismatch: " + result); }
                checks.Add("native selection returns exact path: " + Path.GetFileName(request.InitialPath));
            }
            var cancelTitle = "StudioX 自有窗口取消验证 " + Guid.NewGuid().ToString("N");
            using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8)))
            {
                var cancel = NativeDialogProbe.PressOwnDialogAsync(cancelTitle, 2, deadline.Token);
                var result = await new WindowsPathPicker().PickAsync(new(cancelTitle, false, project, "Keil 工程", "*.uvprojx;*.uvproj"), deadline.Token)
                    .WaitAsync(TimeSpan.FromSeconds(12));
                await cancel;
                if (result is not null) { throw new InvalidOperationException("Native cancel returned a path."); }
                checks.Add("native Cancel button returns no selection");
            }
            foreach (var folder in new[] { false, true })
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(1200));
                try
                {
                    await new WindowsPathPicker().PickAsync(new("StudioX 路径选择验证（自动关闭）", folder, scratch, "Keil 工程", "*.uvprojx;*.uvproj"), timeout.Token)
                        .WaitAsync(TimeSpan.FromSeconds(12));
                    throw new InvalidOperationException("Expected native cancellation.");
                }
                catch (OperationCanceledException) { checks.Add(folder ? "native folder dialog opens on STA and cancellation closes it" : "native Keil file dialog accepts file filters and cancellation closes it"); }
            }
            if (Directory.Exists(Path.Combine(scratch, "%SystemDrive%"))) { throw new InvalidOperationException("Shell wrote caches under plugin working directory."); }
            checks.Add("minimal host environment does not create literal SystemDrive cache directory");
            await File.WriteAllTextAsync(Path.Combine(scratch, "native-picker.json"), JsonSerializer.Serialize(new { success = true, checks }, DeviceCatalog.Json));
            Console.WriteLine("PASS native dialogs: " + checks.Count);
            return 0;
        }
        catch (Exception error)
        {
            await File.WriteAllTextAsync(Path.Combine(scratch, "native-picker.json"), JsonSerializer.Serialize(new { success = false, checks, error = error.ToString() }, DeviceCatalog.Json));
            Console.Error.WriteLine(error);
            return 1;
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
            Environment.SetEnvironmentVariable("SystemDrive", originalDrive, EnvironmentVariableTarget.Process);
            Environment.SetEnvironmentVariable("ProgramData", originalProgramData, EnvironmentVariableTarget.Process);
        }
    }

    public static async Task RunAsync(string scratch, string projectFile, string root, string cli, string packs, DeviceChoice device,
        Action<bool, string, object?> check, CancellationToken token)
    {
        var picker = new ScriptedPicker();
        var plugin = new KeilImporterPlugin(picker);
        var host = new PanelHost();
        await plugin.ActivateAsync(host, token);
        var input = host.Values();
        input["projectName"] = "picker-project";
        picker.Result = projectFile;
        await InvokeAsync("browseProject", input);
        var pickedId = host.Latest!.Widgets.Single(widget => widget.Kind == "form").Children!.Single(widget => widget.Kind == "input" && widget.Label.StartsWith("Keil 工程文件", StringComparison.Ordinal)).Id;
        check(pickedId != "projectFile" && host.Values()[pickedId] == projectFile && host.Values()["projectName"] == "picker-project",
            "picked project gets fresh widget identity and preserves other typed fields", null);
        check(!picker.Last!.Folder && picker.Last.FilterPattern == "*.uvprojx;*.uvproj", "Keil picker filters both supported project formats", null);
        picker.Result = root;
        await InvokeAsync("browseSource", host.Values());
        check(picker.Last!.Folder && picker.Last.InitialPath == Path.GetDirectoryName(projectFile), "source-folder picker starts beside selected project", null);
        picker.Result = scratch;
        await InvokeAsync("browseParent", host.Values());
        picker.Result = cli;
        await InvokeAsync("browseCli", host.Values());
        check(!picker.Last!.Folder && picker.Last.FilterPattern == "StudioX.Cli.exe", "CLI picker limits executable choice", null);
        picker.Result = packs;
        await InvokeAsync("browsePacks", host.Values());
        check(picker.Last!.Folder, "installed pack path has native folder selection", null);
        var selectedFields = host.Values();
        check(selectedFields.Values.Contains(root) && selectedFields.Values.Contains(scratch) && selectedFields.Values.Contains(cli) &&
            selectedFields.Values.Contains(packs), "all five selected paths are published back to the form", null);
        picker.Result = null;
        var before = JsonSerializer.Serialize(host.Values());
        var cancelled = await InvokeAsync("browseSource", host.Values());
        check(cancelled.GetProperty("cancelled").GetBoolean() && JsonSerializer.Serialize(host.Values()) == before,
            "cancelled picker preserves current paths and unrelated fields", null);
        await InvokeAsync("load", host.Values());
        var fields = host.Values();
        var targetId = fields.Keys.Single(key => key == "target" || key.StartsWith("target_picked_", StringComparison.Ordinal));
        fields[targetId] = KeilProjectReader.Read(projectFile).Single().Name;
        await InvokeAsync("search", fields);
        fields = host.Values();
        fields[fields.Keys.Single(key => key == "device" || key.StartsWith("device_picked_", StringComparison.Ordinal))] = device.Key;
        await InvokeAsync("templates", fields);
        fields = host.Values();
        fields[fields.Keys.Single(key => key == "template" || key.StartsWith("template_picked_", StringComparison.Ordinal))] = "hal";
        var preview = await InvokeAsync("preview", fields);
        check(preview.GetProperty("canCreate").GetBoolean(), "browse-selected paths feed normal migration preview", null);
        var confirmation = host.Latest!.Widgets.Single(widget => widget.Kind == "form").Children!.Single(widget => widget.Kind == "checkbox").Id;
        picker.Result = null;
        await InvokeAsync("browseParent", host.Values());
        check(host.Latest!.Widgets.Any(widget => widget.Id == "summary"), "cancelled browse preserves an existing valid preview", null);
        var nextParent = Directory.CreateDirectory(Path.Combine(scratch, "浏览输出 中文 空格")).FullName;
        picker.Result = nextParent;
        await InvokeAsync("browseParent", host.Values());
        check(!host.Latest!.Widgets.Any(widget => widget.Id == "summary"), "changing output by browsing invalidates the old preview", null);
        fields = host.Values();
        fields[confirmation] = "true";
        // stale confirmation is deliberately submitted as boolean like the actual desktop.
        var values = fields.ToDictionary(pair => pair.Key, pair => (object?)pair.Value);
        values[confirmation] = true;
        var refused = await plugin.InvokeAsync("command", "create", JsonSerializer.SerializeToElement(new { values }), token);
        check(!refused.GetProperty("success").GetBoolean() && !Directory.Exists(Path.Combine(nextParent, "picker-project")),
            "browse change cannot reuse previous migration confirmation", null);
        picker.Result = cli;
        var invalid = await InvokeAsync("browseProject", host.Values());
        check(!invalid.GetProperty("success").GetBoolean() && host.Values().Values.Contains(projectFile), "incorrect file type rejected without replacing selected Keil path", null);
        check(!Directory.Exists(Path.Combine(scratch, "picker-project")), "browsing and preview create no converted project", null);
        await plugin.DeactivateAsync(token);

        Task<JsonElement> InvokeAsync(string command, Dictionary<string, string> values) =>
            plugin.InvokeAsync("command", command, JsonSerializer.SerializeToElement(new { values }), token);
    }

    private sealed class ScriptedPicker : IPathPicker
    {
        public string? Result { get; set; }
        public PathPickerRequest? Last { get; private set; }
        public Task<string?> PickAsync(PathPickerRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Last = request;
            return Task.FromResult(Result);
        }
    }

    private sealed class PanelHost : IPluginHost
    {
        public PluginPanelDefinition? Latest { get; private set; }
        public Dictionary<string, string> Values() => Latest!.Widgets.Single(widget => widget.Kind == "form").Children!
            .Where(widget => widget.Kind is "input" or "select").ToDictionary(widget => widget.Id,
                widget => widget.Kind == "select" ? widget.Value!.Value.GetProperty("selected").GetString()! : widget.Value!.Value.GetString()!);
        public Task<JsonElement> CallAsync(string tool, JsonElement arguments, CancellationToken token) => throw new InvalidOperationException("No host tools expected.");
        public Task PublishPanelAsync(PluginPanelDefinition panel, CancellationToken token) { Latest = panel; return Task.CompletedTask; }
        public Task LogAsync(string level, string message, CancellationToken token) => Task.CompletedTask;
    }
}
