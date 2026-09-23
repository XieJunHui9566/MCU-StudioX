using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using StudioX.Extensions;
using StudioX.Extensions.Abstractions;
using StudioX.Foundation;

Console.InputEncoding = new UTF8Encoding(false);
Console.OutputEncoding = new UTF8Encoding(false);
if (args is ["--serial-script"]) return await StudioX.PluginHost.SerialScriptHost.RunAsync();
var protocol = Console.Out;
Console.SetOut(Console.Error); // 插件 Console.WriteLine 不得破坏宿主响应。
DecoderRequest? request = null;
try
{
    if (args is not ["--plugin", var manifestPath]) throw new StudioXException("PLUGIN_ARGS", "需要 --plugin <清单路径>。");
    var manifest = await PluginManifest.ReadAsync(manifestPath);
    var input = new StringBuilder();
    var one = new char[1];
    while (await Console.In.ReadAsync(one) > 0 && one[0] != '\n')
    {
        if (input.Length >= 100000) throw new StudioXException("PLUGIN_INPUT_LIMIT", "协议输入过长。");
        input.Append(one[0]);
    }
    request = JsonSerializer.Deserialize<DecoderRequest>(input.ToString(), JsonStore.Options);
    if (request is null || request.ProtocolVersion != 1 || string.IsNullOrEmpty(request.RequestId)) throw new StudioXException("PLUGIN_PROTOCOL", "协议请求无效。");
    var payload = Convert.FromBase64String(request.PayloadBase64);
    if (payload.Length > 65536) throw new StudioXException("PLUGIN_INPUT_LIMIT", "单次解码最多 64 KiB。");
    var assemblyPath = PathBoundary.Resolve(Path.GetDirectoryName(Path.GetFullPath(manifestPath))!, manifest.EntryAssembly);
    var context = new DecoderLoadContext(assemblyPath);
    var type = context.LoadFromAssemblyPath(assemblyPath).GetType(manifest.EntryType, throwOnError: false);
    if (type is null || !typeof(IFrameDecoder).IsAssignableFrom(type) || type.IsAbstract)
        throw new StudioXException("PLUGIN_ENTRY", "指定入口未实现 IFrameDecoder。");
    var decoder = (IFrameDecoder)(Activator.CreateInstance(type) ?? throw new StudioXException("PLUGIN_ENTRY", "无法创建解码器。"));
    var result = decoder.Decode(payload);
    await protocol.WriteLineAsync(JsonSerializer.Serialize(new DecoderResponse(1, request.RequestId, result, null, null), JsonStore.Options));
    return 0;
}
catch (Exception ex)
{
    var code = ex is StudioXException studio ? studio.Code : "PLUGIN_EXCEPTION";
    await protocol.WriteLineAsync(JsonSerializer.Serialize(new DecoderResponse(1, request?.RequestId ?? "", null, code, ex.ToString()), JsonStore.Options));
    return 0;
}

sealed class DecoderLoadContext(string entry) : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver resolver = new(entry);
    protected override Assembly? Load(AssemblyName name)
    {
        if (name.Name == typeof(IFrameDecoder).Assembly.GetName().Name) return typeof(IFrameDecoder).Assembly;
        var path = resolver.ResolveAssemblyToPath(name);
        return path is null ? null : LoadFromAssemblyPath(path);
    }
    protected override nint LoadUnmanagedDll(string name)
    {
        var path = resolver.ResolveUnmanagedDllToPath(name);
        return path is null ? nint.Zero : LoadUnmanagedDllFromPath(path);
    }
}
