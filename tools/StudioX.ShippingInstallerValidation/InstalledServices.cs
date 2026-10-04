namespace StudioX.ShippingInstallerValidation;

using System.Reflection;
using System.Runtime.Loader;

internal sealed class InstalledServices
{
    private readonly string installed;
    private readonly Dictionary<string, Assembly> assemblies = new(StringComparer.Ordinal);
    internal InstalledServices(string directory)
    {
        installed = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (!File.Exists(Path.Combine(installed, "release-files.sha256.json")))
        {
            throw new IOException("Select an actual installed shipping payload.");
        }
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            var path = FileEvidence.Resolve(installed, name.Name + ".dll");
            return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
        };
        foreach (var name in new[] { "StudioX.Foundation", "StudioX.Packages", "StudioX.Engine", "StudioX.Application" })
        {
            assemblies.Add(name, AssemblyLoadContext.Default.LoadFromAssemblyPath(FileEvidence.Resolve(installed, name + ".dll")));
        }
    }
    private Type Type(string name) => assemblies.Values.Select(assembly => assembly.GetType(name)).FirstOrDefault(type => type is not null)
        ?? throw new MissingMemberException("Shipping API not found: " + name);
    private object New(string type, params object?[] arguments)
    {
        var constructor = Type(type).GetConstructors().Single(info => Accepts(info.GetParameters(), arguments.Length));
        return constructor.Invoke(Pad(constructor.GetParameters(), arguments));
    }
    private static bool Accepts(ParameterInfo[] parameters, int count) => parameters.Length >= count && parameters.Skip(count).All(parameter => parameter.IsOptional);
    private static object?[] Pad(ParameterInfo[] parameters, object?[] arguments) => arguments.Concat(parameters.Skip(arguments.Length).Select(_ => System.Type.Missing)).ToArray();
    private static async Task<object?> CallAsync(object target, string name, params object?[] arguments)
    {
        var method = target.GetType().GetMethods().Single(info => info.Name == name && !info.IsStatic && Accepts(info.GetParameters(), arguments.Length));
        var task = method.Invoke(target, Pad(method.GetParameters(), arguments)) as Task ?? throw new InvalidOperationException("Expected asynchronous shipping API: " + name);
        await task;
        return task.GetType().GetProperty("Result")?.GetValue(task);
    }
    internal object Bindings()
    {
        var files = AppDomain.CurrentDomain.GetAssemblies().Where(assembly => (assembly.GetName().Name ?? "").StartsWith("StudioX.", StringComparison.Ordinal)
            && assembly != typeof(InstalledServices).Assembly).Select(assembly =>
        {
            if (!assembly.Location.StartsWith(installed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("A tested StudioX assembly was loaded outside the actual installation.");
            }
            return new
            {
                path = Path.GetRelativePath(installed, assembly.Location),
                sha256 = FileEvidence.Hash(assembly.Location)
            };
        }).ToArray();
        return new
        {
            passed = true,
            installed,
            actualShippingAssemblies = true,
            files,
            toolsetCatalogConstructorParameters = Type("StudioX.Engine.ToolsetCatalog").GetConstructors().Select(info => info.GetParameters().Length).ToArray()
        };
    }
    private (object Tools, object Packs, object Manager) Services(string data)
    {
        var toolRoot = Path.Combine(installed, "runtime/toolsets");
        // 已发布的 0.2.5.4B 只有工具根参数；新版增加独立用户数据目录，按实际程序集选择。
        var tools = Type("StudioX.Engine.ToolsetCatalog").GetConstructors().Any(info => Accepts(info.GetParameters(), 2))
            ? New("StudioX.Engine.ToolsetCatalog", toolRoot, data)
            : New("StudioX.Engine.ToolsetCatalog", toolRoot);
        var packs = New("StudioX.Packages.PackRepository", Path.Combine(data, "packs"));
        var recent = New("StudioX.Application.RecentProjectService", data);
        var manager = New("StudioX.Application.Tools.ToolManagementService", tools, packs, recent, data);
        return (tools, packs, manager);
    }
    internal async Task ImportAsync(string archive, string data, string output)
    {
        var services = Services(data);
        var progress = new Progress<string>(message => Console.WriteLine(message));
        var preview = await CallAsync(services.Manager, "PreviewInstallAsync", archive, progress);
        var result = await CallAsync(services.Manager, "InstallAsync", preview, progress);
        FileEvidence.Write(output, new
        {
            passed = true,
            preview,
            result,
            bindings = Bindings()
        });
    }
    internal async Task CreateAsync(string archive, string device, string template, string name, string project, string data, string output)
    {
        var services = Services(data);
        var pack = await CallAsync(services.Packs, "ImportAsync", archive);
        var manifest = await CallAsync(New("StudioX.Engine.ProjectService"), "CreateAsync", pack, device, template, name, project);
        var requirements = await CallAsync(New("StudioX.Application.Tools.ProjectToolPreparationService", services.Tools, services.Manager), "InspectAsync", project);
        FileEvidence.Write(output, new
        {
            passed = true,
            manifest,
            requirements,
            bindings = Bindings()
        });
    }
    internal async Task BuildAsync(string project, string data, string output)
    {
        var previous = new[] { "PATH", "IDF_PATH", "IDF_TOOLS_PATH", "IDF_PYTHON_ENV_PATH", "PYTHONHOME", "PYTHONPATH" }.ToDictionary(key => key, Environment.GetEnvironmentVariable);
        object? result = null;
        try
        {
            // 验证安装目录提供所需工具，禁止继承宿主预先配置的开发环境路径。
            Environment.SetEnvironmentVariable("PATH", Environment.GetFolderPath(Environment.SpecialFolder.System));
            foreach (var name in previous.Keys.Where(name => name != "PATH"))
            {
                Environment.SetEnvironmentVariable(name, null);
            }
            var services = Services(data);
            result = await CallAsync(New("StudioX.Engine.BuildService", services.Tools), "BuildAsync", project, new Progress<string>(Console.WriteLine));
            var success = result?.GetType().GetProperty("Success")?.GetValue(result) is true;
            var log = result?.GetType().GetProperty("Log")?.GetValue(result) as string;
            File.WriteAllText(output + ".build.log", log ?? "");
            FileEvidence.Write(output, new
            {
                passed = success,
                result,
                bindings = Bindings(),
                hardware = false
            });
            if (!success)
            {
                throw new InvalidOperationException("Actual installed toolchain build failed; see its original log.");
            }
        }
        finally { foreach (var (name, value) in previous) { Environment.SetEnvironmentVariable(name, value); } }
    }
}
