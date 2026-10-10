using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using StudioX.Extensions.Abstractions;
using StudioX.KeilImporter;

/// <summary>使用安装版渲染器的真实文本框、下拉框、复选框和提交函数，防止手工 JSON 掩盖界面状态问题。</summary>
internal sealed class MigrationFlow
{
    private readonly List<string> checks = [];
    private readonly List<object> submissions = [];
    private readonly KeilImporterPlugin plugin;
    private readonly Picker picker = new();
    private readonly Host host = new();
    private readonly object renderer;
    private readonly MethodInfo render;
    private readonly MethodInfo invokeButton;
    private readonly FieldInfo inputs;
    private FrameworkElement view = null!;
    private JsonElement result;
    private JsonElement? openedProject;

    private MigrationFlow(string desktop)
    {
        var root = Path.GetDirectoryName(desktop)!;
        AssemblyLoadContext.Default.Resolving += (_, name) => File.Exists(Path.Combine(root, name.Name + ".dll"))
            ? AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(root, name.Name + ".dll")) : null;
        var type = AssemblyLoadContext.Default.LoadFromAssemblyPath(desktop).GetType("StudioX.Desktop.PluginPanelRenderer", true)!;
        renderer = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, new object[] { (Func<string, JsonElement, Task>)SubmitAsync, (Action<string>)(message => throw new InvalidOperationException(message)) }, null)!;
        type.GetProperty("OpenProjectAsync")!.SetValue(renderer, (Func<JsonElement, Task>)(target =>
        {
            openedProject = target.Clone();
            return Task.CompletedTask;
        }));
        render = type.GetMethod("Render")!;
        invokeButton = type.GetMethod("InvokeButtonAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        inputs = type.GetField("inputs", BindingFlags.Instance | BindingFlags.NonPublic)!;
        plugin = new(picker);
        host.Publish = panel => view = (FrameworkElement)render.Invoke(renderer, [panel])!;
    }

    public static int Run(string[] args)
    {
        var output = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(output);
        var app = new Application();
        var exitCode = 1;
        app.Startup += async (_, _) =>
        {
            MigrationFlow? flow = null;
            try
            {
                flow = new(args[0]);
                await flow.CheckAsync(args, output);
                Write(true, null);
                exitCode = 0;
                Console.WriteLine("PASS installed renderer migration: " + flow.checks.Count);
            }
            catch (Exception error) { Write(false, error.ToString()); Console.Error.WriteLine(error); }
            finally { app.Shutdown(); }
            void Write(bool success, string? error) => File.WriteAllText(Path.Combine(output, "flow.json"),
                JsonSerializer.Serialize(new
                {
                    success,
                    flow?.checks,
                    flow?.submissions,
                    error
                }, DeviceCatalog.Json));
        };
        app.Run();
        return exitCode;
    }

    private async Task CheckAsync(string[] args, string output)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        var project = Path.GetFullPath(args[4]);
        var source = Path.GetDirectoryName(Path.GetDirectoryName(project))!;
        await plugin.ActivateAsync(host, deadline.Token);
        Text("projectName").Text = "renderer-confirm-f407";
        await Browse("browseProject", project);
        await Browse("browseSource", source);
        await Browse("browseParent", output);
        await Browse("browseCli", args[2]);
        await Browse("browsePacks", args[3]);
        await Click("load");
        Choose(0, KeilProjectReader.Read(project).Single().Name);
        await Click("search");
        var device = (await DeviceCatalog.SearchAsync(args[3], "STM32F407", deadline.Token))
            .Single(choice => choice.Id == "STM32F407ZG" && choice.PackVersion == "0.1.3");
        Choose(1, device.Key);
        await Click("templates");
        Choose(2, "hal");
        await Click("preview");
        Check(result.GetProperty("canCreate").GetBoolean(), "actual WPF browse and dropdown values produce valid preview");
        var firstConfirmation = Confirmation().Id;
        var destination = Path.Combine(output, "renderer-confirm-f407");
        await Click("create");
        Check(!result.GetProperty("success").GetBoolean() && !Directory.Exists(destination), "unchecked actual checkbox prevents writes");
        if (args.Contains("--reproduce", StringComparer.Ordinal))
        {
            Check(!host.Latest!.Widgets.Any(widget => widget.Id == "summary"), "0.1.3 unchecked click discards valid preview");
            Controls<CheckBox>().Single().IsChecked = true;
            await Click("create");
            Check(!result.GetProperty("success").GetBoolean() && !Directory.Exists(destination), "0.1.3 retry after checking still rejected because preview was discarded");
            await plugin.DeactivateAsync(default);
            return;
        }
        Check(host.Latest!.Widgets.Any(widget => widget.Id == "summary") && Confirmation().Id == firstConfirmation,
            "unchecked click preserves preview and confirmation identity");
        Controls<CheckBox>().Single().IsChecked = true;
        Text("projectName").Text = "renderer-edited";
        await Click("create");
        Check(!result.GetProperty("success").GetBoolean() && !Directory.Exists(Path.Combine(output, "renderer-edited")) &&
            !host.Latest!.Widgets.Any(widget => widget.Id == "summary"), "edited actual text field invalidates checked preview and writes nothing");
        Check(result.GetProperty("message").GetString()!.Contains("工程名", StringComparison.Ordinal), "changed-field message identifies actual edited field");
        Text("projectName").Text = "renderer-confirm-f407";
        await Click("preview");
        Check(Confirmation().Id != firstConfirmation && Controls<CheckBox>().Single().IsChecked == false,
            "fresh preview resets actual retained checkbox");
        Controls<CheckBox>().Single().IsChecked = true;
        picker.Result = null;
        await Click("browseParent");
        Check(Controls<CheckBox>().Single().IsChecked == true && host.Latest!.Widgets.Any(widget => widget.Id == "summary"),
            "cancelled browse preserves actual checked confirmation and preview");
        await Click("preview");
        Check(Controls<CheckBox>().Single().IsChecked == false, "explicit re-preview always requires fresh consent");
        await Click("create");
        Check(!result.GetProperty("success").GetBoolean() && host.Latest!.Widgets.Any(widget => widget.Id == "summary"),
            "repeated unchecked click keeps valid preview");
        Controls<CheckBox>().Single().IsChecked = true;
        await Click("create");
        Check(result.TryGetProperty("compilationVerified", out var compiled) && compiled.GetBoolean() &&
            result.GetProperty("directory").GetString() == destination,
            "checking after refusal ports real F407 HAL project and automatically compiles using installed managed tools");
        var log = Path.Combine(destination, result.GetProperty("buildLog").GetString()!);
        File.Copy(log, Path.Combine(output, "renderer-migration-build.txt"));
        Check(!Controls<CheckBox>().Any() && !host.Latest!.Widgets.Any(widget => widget.Id == "summary"),
            "completed migration consumes preview and removes obsolete confirmation");
        Check(openedProject is null, "successful migration does not implicitly replace current workspace");
        var submittedCount = submissions.Count;
        await (Task)invokeButton.Invoke(renderer, [host.Latest!.Widgets.Single(widget => widget.Kind == "projectLink")])!;
        Check(openedProject?.GetProperty("directory").GetString() == destination && submissions.Count == submittedCount,
            "actual renderer project button forwards exact migrated directory without running another plugin command");
        await plugin.DeactivateAsync(default);

        async Task Browse(string command, string path)
        {
            picker.Result = path;
            await Click(command);
            Check(result.GetProperty("success").GetBoolean(), "actual renderer submits " + command);
        }
    }

    private async Task SubmitAsync(string command, JsonElement arguments)
    {
        result = await plugin.InvokeAsync("command", command, arguments, default);
        submissions.Add(new
        {
            command,
            arguments,
            result
        });
    }

    private Task Click(string command) => (Task)invokeButton.Invoke(renderer,
        [host.Latest!.Widgets.Single(widget => widget.Kind == "form").Children!.Single(widget => widget.CommandId == command)])!;
    private TextBox Text(string field) => ((Dictionary<string, TextBox>)inputs.GetValue(renderer)!).Single(pair =>
        pair.Key == field || pair.Key.StartsWith(field + "_picked_", StringComparison.Ordinal)).Value;
    private PluginPanelWidget Confirmation() => host.Latest!.Widgets.Single(widget => widget.Kind == "form").Children!.Single(widget => widget.Kind == "checkbox");
    private void Choose(int index, string value)
    {
        var select = Controls<ComboBox>().ElementAt(index);
        select.SelectedItem = select.Items.Cast<object>().Single(option => ((JsonElement)option.GetType().GetProperty("Value")!.GetValue(option)!).GetString() == value);
    }
    private IEnumerable<T> Controls<T>() where T : DependencyObject => Walk(view).OfType<T>();
    private static IEnumerable<DependencyObject> Walk(DependencyObject item)
    {
        yield return item;
        foreach (var child in LogicalTreeHelper.GetChildren(item).OfType<DependencyObject>())
        {
            foreach (var descendant in Walk(child))
            {
                yield return descendant;
            }
        }
    }
    private void Check(bool value, string name)
    {
        if (!value)
        {
            throw new InvalidOperationException(name);
        }
        checks.Add(name);
    }
    private sealed class Picker : IPathPicker
    {
        public string? Result
        {
            get; set;
        }
        public Task<string?> PickAsync(PathPickerRequest request, CancellationToken token) => Task.FromResult(Result);
    }
    private sealed class Host : IPluginHost
    {
        public Action<PluginPanelDefinition> Publish { get; set; } = null!;
        public PluginPanelDefinition? Latest
        {
            get; private set;
        }
        public Task<JsonElement> CallAsync(string tool, JsonElement arguments, CancellationToken token) => throw new InvalidOperationException("Unexpected host tool.");
        public Task PublishPanelAsync(PluginPanelDefinition panel, CancellationToken token)
        {
            Latest = panel;
            Publish(panel);
            return Task.CompletedTask;
        }
        public Task LogAsync(string level, string message, CancellationToken token) => Task.CompletedTask;
    }
}
