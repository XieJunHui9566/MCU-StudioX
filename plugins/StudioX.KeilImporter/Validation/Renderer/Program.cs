using System.Reflection;
using System.IO;
using System.Runtime.Loader;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using StudioX.Extensions.Abstractions;
using StudioX.KeilImporter;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length >= 5) { return MigrationFlow.Run(args); }
        var root = Path.GetDirectoryName(args[0])!;
        var output = args[1];
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        try
        {
            AssemblyLoadContext.Default.Resolving += (_, name) => File.Exists(Path.Combine(root, name.Name + ".dll"))
                ? AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(root, name.Name + ".dll")) : null;
            var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(args[0]);
            var rendererType = assembly.GetType("StudioX.Desktop.PluginPanelRenderer", true)!;
            var renderer = Activator.CreateInstance(rendererType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new object[] { (Func<string, JsonElement, Task>)((_, _) => Task.CompletedTask), (Action<string>)(message => throw new InvalidOperationException(message)) }, null)!;
            var render = rendererType.GetMethod("Render", BindingFlags.Instance | BindingFlags.Public)!;
            var inputsField = rendererType.GetField("inputs", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var app = new Application();
            var picker = new Picker();
            var host = new Host();
            var plugin = new KeilImporterPlugin(picker);
            plugin.ActivateAsync(host, default).GetAwaiter().GetResult();
            _ = (FrameworkElement)render.Invoke(renderer, [host.Latest!])!;
            var inputs = (Dictionary<string, TextBox>)inputsField.GetValue(renderer)!;
            inputs["projectFile"].Text = "stale manually typed path";
            inputs["projectName"].Text = "preserve-name";
            var project = Path.Combine(output, "工程 中文 空格.uvprojx");
            File.WriteAllText(project, "<Project />");
            picker.Result = project;
            var submitted = inputs.ToDictionary(pair => pair.Key, pair => pair.Value.Text);
            plugin.InvokeAsync("command", "browseProject", JsonSerializer.SerializeToElement(new { values = submitted }), default).GetAwaiter().GetResult();
            _ = (FrameworkElement)render.Invoke(renderer, [host.Latest!])!;
            inputs = (Dictionary<string, TextBox>)inputsField.GetValue(renderer)!;
            Check(!inputs.ContainsKey("projectFile") && inputs.Single(pair => pair.Key.StartsWith("projectFile_picked_", StringComparison.Ordinal)).Value.Text == project,
                "installed renderer displays selected Unicode path instead of retained stale input");
            Check(inputs["projectName"].Text == "preserve-name", "browsing preserves other actual WPF text box content");
            picker.Result = null;
            plugin.InvokeAsync("command", "browseProject", JsonSerializer.SerializeToElement(new { values = inputs.ToDictionary(pair => pair.Key, pair => pair.Value.Text) }), default).GetAwaiter().GetResult();
            _ = (FrameworkElement)render.Invoke(renderer, [host.Latest!])!;
            inputs = (Dictionary<string, TextBox>)inputsField.GetValue(renderer)!;
            Check(inputs.Values.Any(input => input.Text == project), "cancelled browser keeps selected path in installed renderer");
            plugin.DeactivateAsync(default).GetAwaiter().GetResult();
            app.Shutdown();
            File.WriteAllText(Path.Combine(output, "renderer.json"), JsonSerializer.Serialize(new { success = true, checks }, DeviceCatalog.Json));
            Console.WriteLine("PASS installed renderer: " + checks.Count);
            return 0;
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(output, "renderer.json"), JsonSerializer.Serialize(new { success = false, checks, error = error.ToString() }, DeviceCatalog.Json));
            Console.Error.WriteLine(error);
            return 1;
        }
        void Check(bool value, string name) { if (!value) { throw new InvalidOperationException(name); } checks.Add(name); }
    }
    private sealed class Picker : IPathPicker
    {
        public string? Result { get; set; }
        public Task<string?> PickAsync(PathPickerRequest request, CancellationToken token) => Task.FromResult(Result);
    }
    private sealed class Host : IPluginHost
    {
        public PluginPanelDefinition? Latest { get; private set; }
        public Task<JsonElement> CallAsync(string tool, JsonElement arguments, CancellationToken token) => throw new InvalidOperationException("Unexpected host tool.");
        public Task PublishPanelAsync(PluginPanelDefinition panel, CancellationToken token) { Latest = panel; return Task.CompletedTask; }
        public Task LogAsync(string level, string message, CancellationToken token) => Task.CompletedTask;
    }
}
