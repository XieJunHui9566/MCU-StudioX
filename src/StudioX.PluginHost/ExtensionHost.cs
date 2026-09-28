namespace StudioX.PluginHost;

using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using StudioX.Extensions;
using StudioX.Extensions.Abstractions;
using StudioX.Foundation;

/// <summary>独立 .NET 插件宿主；只有协议写入保存的 stdout，插件 Console 输出转入 stderr。</summary>
internal static class ExtensionHost
{
    public static async Task<int> RunAsync(string manifestPath)
    {
        var protocol = Console.Out;
        Console.SetOut(Console.Error);
        IStudioXPlugin? plugin = null;
        PluginProtocolConnection? connection = null;
        var lifecycleGate = new SemaphoreSlim(1, 1);
        var described = false;
        var active = false;
        try
        {
            var manifest = await PluginManifest.ReadAsync(manifestPath).ConfigureAwait(false);
            if (manifest.ApiVersion != 2 || manifest.Kind != "dotnet")
            {
                throw new StudioXException("PLUGIN_API", ".NET 通用宿主仅接受明确的 API 2 .NET 插件。");
            }
            var assemblyPath = PathBoundary.Resolve(Path.GetDirectoryName(Path.GetFullPath(manifestPath))!, manifest.EntryAssembly);
            var context = new ExtensionLoadContext(assemblyPath);
            var type = context.LoadFromAssemblyPath(assemblyPath).GetType(manifest.EntryType, throwOnError: false);
            if (type is null || !typeof(IStudioXPlugin).IsAssignableFrom(type) || type.IsAbstract)
            {
                throw new StudioXException("PLUGIN_ENTRY", "指定入口未实现 IStudioXPlugin。");
            }
            plugin = (IStudioXPlugin)(Activator.CreateInstance(type) ?? throw new StudioXException("PLUGIN_ENTRY", "无法创建插件入口。"));
            connection = new PluginProtocolConnection(Console.In, protocol, async (method, payload, token) =>
            {
                await lifecycleGate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    switch (method)
                    {
                        case "describe":
                            if (active)
                            {
                                throw new StudioXException("PLUGIN_STATE", "激活后不能重新获取贡献声明。");
                            }
                            var contribution = plugin.Describe();
                            described = true;
                            return JsonSerializer.SerializeToElement(contribution, JsonStore.Options);
                        case "activate":
                            if (!described || active)
                            {
                                throw new StudioXException("PLUGIN_STATE", "插件必须先声明，且只可激活一次。");
                            }
                            await plugin.ActivateAsync(new HostBridge(connection!), token).ConfigureAwait(false);
                            active = true;
                            return JsonSerializer.SerializeToElement(new { active = true });
                        case "invoke":
                            if (!active)
                            {
                                throw new StudioXException("PLUGIN_STATE", "插件尚未激活。");
                            }
                            return await plugin.InvokeAsync(payload.GetProperty("kind").GetString()!,
                                payload.GetProperty("id").GetString()!, payload.GetProperty("arguments"), token).ConfigureAwait(false);
                        case "deactivate":
                            if (active)
                            {
                                active = false;
                                await plugin.DeactivateAsync(token).ConfigureAwait(false);
                            }
                            return JsonSerializer.SerializeToElement(new { active = false });
                        default:
                            throw new StudioXException("PLUGIN_METHOD", $"未知插件请求：{method}");
                    }
                }
                finally
                {
                    lifecycleGate.Release();
                }
            }, (_, _, _) => throw new StudioXException("PLUGIN_PROTOCOL", "应用不能向插件宿主发布插件事件。"));
            await connection.Completion.ConfigureAwait(false);
            return 0;
        }
        catch (Exception exception)
        {
            await Console.Error.WriteLineAsync(exception.ToString()).ConfigureAwait(false);
            return 1;
        }
        finally
        {
            if (connection is not null)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
            if (plugin is not null && active)
            {
                try
                {
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await plugin.DeactivateAsync(deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    // 输入关闭后的清理失败仍写入原始宿主诊断；不能再发送协议响应。
                    await Console.Error.WriteLineAsync(exception.ToString()).ConfigureAwait(false);
                }
            }
        }
    }

    private sealed class HostBridge(PluginProtocolConnection connection) : IPluginHost
    {
        public Task<JsonElement> CallAsync(string tool, JsonElement arguments, CancellationToken cancellationToken)
        {
            return connection.RequestAsync("hostCall", JsonSerializer.SerializeToElement(new { tool, arguments }),
                TimeSpan.FromMinutes(15), cancellationToken);
        }

        public Task PublishPanelAsync(PluginPanelDefinition panel, CancellationToken cancellationToken)
        {
            return connection.PublishAsync("panel", JsonSerializer.SerializeToElement(panel, JsonStore.Options), cancellationToken);
        }

        public Task LogAsync(string level, string message, CancellationToken cancellationToken)
        {
            return connection.PublishAsync("log", JsonSerializer.SerializeToElement(new { level, message }), cancellationToken);
        }
    }

    private sealed class ExtensionLoadContext(string entry) : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver resolver = new(entry);

        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name == typeof(IStudioXPlugin).Assembly.GetName().Name)
            {
                return typeof(IStudioXPlugin).Assembly;
            }
            var path = resolver.ResolveAssemblyToPath(name);
            return path is null ? null : LoadFromAssemblyPath(path);
        }

        protected override nint LoadUnmanagedDll(string name)
        {
            var path = resolver.ResolveUnmanagedDllToPath(name);
            return path is null ? nint.Zero : LoadUnmanagedDllFromPath(path);
        }
    }
}
