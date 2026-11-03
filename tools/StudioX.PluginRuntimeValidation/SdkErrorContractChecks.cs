namespace StudioX.PluginRuntimeValidation;

using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security;
using System.Security.Cryptography;
using System.Text.Json;
using StudioX.Extensions;
using StudioX.Extensions.Abstractions;
using StudioX.Foundation;

/// <summary>以仅引用公开 SDK 的独立插件验证宿主错误，不让夹具借用内部异常类型。</summary>
internal static class SdkErrorContractChecks
{
    public static async Task<string> RunAsync(string repository, string scratch, string hostExecutable)
    {
        var source = Path.Combine(scratch, "sdk-error-source");
        var output = Path.Combine(scratch, "sdk-error-plugin");
        Directory.CreateDirectory(source);
        var sdk = typeof(IStudioXPlugin).Assembly.Location;
        var project = Path.Combine(source, "SdkErrorOnlyPlugin.csproj");
        await File.WriteAllTextAsync(project, $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <RuntimeIdentifier></RuntimeIdentifier>
                <EnableDynamicLoading>true</EnableDynamicLoading>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
              <ItemGroup>
                <Reference Include="StudioX.Extensions.Abstractions">
                  <HintPath>{{SecurityElement.Escape(sdk)}}</HintPath>
                  <Private>false</Private>
                </Reference>
              </ItemGroup>
            </Project>
            """);
        await File.WriteAllTextAsync(Path.Combine(source, "Plugin.cs"), PluginSource);
        var nuget = Path.Combine(source, "NuGet.Config");
        await File.WriteAllTextAsync(nuget, "<configuration><packageSources><clear /></packageSources></configuration>");
        var dotnet = FindDotnet();
        var runner = new ProcessRunner();
        foreach (var arguments in new[]
        {
            new[] { "restore", project, "--configfile", nuget, "--ignore-failed-sources", "--nologo" },
            new[] { "build", project, "--no-restore", "--configuration", "Release", "--output", output, "--nologo" }
        })
        {
            var build = await runner.RunAsync(new ProcessRequest(dotnet, arguments, repository, TimeSpan.FromMinutes(2)));
            var operation = arguments[0];
            await File.WriteAllTextAsync(Path.Combine(source, operation + ".log"), build.StandardOutput + build.StandardError);
            if (!build.Success || build.OutputTruncated)
            {
                throw new InvalidOperationException($"SDK-only fixture {operation} failed: {build.StandardOutput}\n{build.StandardError}");
            }
        }
        var entry = Path.Combine(output, "SdkErrorOnlyPlugin.dll");
        using (var assembly = File.OpenRead(entry))
        {
            using var pe = new PEReader(assembly);
            var metadata = pe.GetMetadataReader();
            var references = metadata.AssemblyReferences.Select(handle => metadata.GetString(metadata.GetAssemblyReference(handle).Name)).ToArray();
            if (!references.Contains("StudioX.Extensions.Abstractions", StringComparer.Ordinal) ||
                references.Any(name => name.StartsWith("StudioX.", StringComparison.Ordinal) && name != "StudioX.Extensions.Abstractions"))
            {
                throw new InvalidOperationException("SDK-only fixture unexpectedly references an internal StudioX assembly.");
            }
        }
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in Directory.GetFiles(output, "*", SearchOption.AllDirectories))
        {
            await using var file = File.OpenRead(path);
            hashes[Path.GetRelativePath(output, path).Replace('\\', '/')] = Convert.ToHexString(await SHA256.HashDataAsync(file));
        }
        var manifest = new PluginManifest(1, 2, "validation.sdk-errors", "1.0.0", "SDK errors",
            Path.GetFileName(entry), "SdkErrorOnlyPlugin", ["commands"], hashes,
            HostTools: ["validation.denied", "validation.unknown", "validation.timeout", "validation.cancel"]);
        var manifestPath = Path.Combine(output, "plugin.json");
        await JsonStore.WriteAsync(manifestPath, manifest);
        var errors = new Dictionary<string, (string Code, string Message)>(StringComparer.Ordinal)
        {
            ["denied"] = ("MCP_APPROVAL_DENIED", "用户拒绝本次操作 / denied"),
            ["unknown"] = ("MCP_TOOL_NOT_FOUND", "没有注册此工具 / unknown tool"),
            ["timeout"] = ("PLUGIN_TIMEOUT", "宿主工具超过运行时限 / timeout")
        };
        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var client = await PluginRuntimeClient.StartAsync(hostExecutable, manifestPath, async (tool, _, token) =>
        {
            var id = tool["validation.".Length..];
            if (id == "cancel")
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    cancelled.TrySetResult(true);
                    throw;
                }
                return JsonSerializer.SerializeToElement(new { });
            }
            var error = errors[id];
            throw new StudioXException(error.Code, error.Message);
        }, (_, _) => Task.CompletedTask);
        foreach (var (id, expected) in errors)
        {
            var arguments = JsonSerializer.SerializeToElement(new { tool = "validation." + id });
            var caught = await client.InvokeAsync("command", "catch", arguments);
            if (caught.GetProperty("code").GetString() != expected.Code ||
                caught.GetProperty("message").GetString()?.Contains(expected.Message, StringComparison.Ordinal) != true ||
                caught.GetProperty("sdkException").GetBoolean() != true)
            {
                throw new InvalidOperationException("SDK-only plugin could not inspect the original host error: " + id);
            }
            try
            {
                _ = await client.InvokeAsync("command", "rethrow", arguments);
                throw new InvalidOperationException("SDK-only plugin did not rethrow the host error: " + id);
            }
            catch (StudioXException exception) when (exception.Code == expected.Code &&
                exception.Message.Contains(expected.Message, StringComparison.Ordinal))
            {
                // 宿主应用仍获得原有 StudioXException；公开 SDK 异常重新抛出不改变错误码。
            }
        }
        var cancellationResult = await client.InvokeAsync("command", "cancel",
            JsonSerializer.SerializeToElement(new { tool = "validation.cancel" }));
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (!cancellationResult.GetProperty("operationCancelled").GetBoolean() ||
            cancellationResult.GetProperty("sdkException").GetBoolean())
        {
            throw new InvalidOperationException("SDK host cancellation was incorrectly converted to PluginHostException.");
        }
        var result = "SDK-only .NET plugin: public error Code/message, denial/unknown/timeout, rethrow compatibility and cancellation passed";
        await JsonStore.WriteAsync(Path.Combine(source, "results.json"), new { success = true, references = "StudioX.Extensions.Abstractions only", result });
        return result;
    }

    private static string FindDotnet()
    {
        var configured = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && Path.IsPathFullyQualified(configured) && File.Exists(configured))
        {
            return configured;
        }
        return (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Where(directory => !string.IsNullOrWhiteSpace(directory))
            .Select(directory => Path.Combine(directory.Trim('"'), "dotnet.exe"))
            .Where(Path.IsPathFullyQualified).FirstOrDefault(File.Exists)
            ?? throw new InvalidOperationException("SDK-only plugin validation requires an existing dotnet.exe; no SDK is downloaded.");
    }

    private const string PluginSource = """
        using System.Text.Json;
        using StudioX.Extensions.Abstractions;

        public sealed class SdkErrorOnlyPlugin : IStudioXPlugin
        {
            private IPluginHost? host;

            public PluginContribution Describe() => new(
                new[] { "catch", "rethrow", "cancel" }.Select(id => new PluginCommandDefinition(id, id)).ToArray(), [], []);

            public Task ActivateAsync(IPluginHost host, CancellationToken token)
            {
                this.host = host;
                return Task.CompletedTask;
            }

            public async Task<JsonElement> InvokeAsync(string kind, string id, JsonElement arguments, CancellationToken token)
            {
                using var ownCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
                if (id == "cancel") ownCancellation.CancelAfter(TimeSpan.FromMilliseconds(100));
                try
                {
                    return await host!.CallAsync(arguments.GetProperty("tool").GetString()!, arguments, ownCancellation.Token);
                }
                catch (PluginHostException exception)
                {
                    if (id == "rethrow") throw;
                    return JsonSerializer.SerializeToElement(new { code = exception.Code, message = exception.Message, sdkException = true });
                }
                catch (OperationCanceledException) when (ownCancellation.IsCancellationRequested)
                {
                    return JsonSerializer.SerializeToElement(new { operationCancelled = true, sdkException = false });
                }
            }

            public Task DeactivateAsync(CancellationToken token)
            {
                host = null;
                return Task.CompletedTask;
            }
        }
        """;
}
