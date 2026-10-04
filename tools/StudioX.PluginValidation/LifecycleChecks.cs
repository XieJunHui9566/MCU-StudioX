namespace StudioX.PluginValidation;

using System.Security.Cryptography;
using System.Text.Json;
using StudioX.Application;
using StudioX.Application.Mcp;
using StudioX.Application.Plugins;
using StudioX.Extensions;
using StudioX.Foundation;

/// <summary>验证声明拒绝发生在激活之前，以及停用撤销在途主机写入与回调。</summary>
internal static class LifecycleChecks
{
    public static async Task RunAsync(string repository, string scratch, ValidationChecks checks)
    {
        var runtime = Path.Combine(scratch, "runtime");
        var project = Path.Combine(scratch, "project");
        var source = Path.Combine(scratch, "lifecycle-source");
        RepositoryChecks.CopyDirectory(Path.Combine(repository, "tools/StudioX.PluginRuntimeValidation/bin/Debug/net10.0"), source);
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            await using var input = File.OpenRead(file);
            hashes[Path.GetRelativePath(source, file).Replace('\\', '/')] = Convert.ToHexString(await SHA256.HashDataAsync(input));
        }
        var manifest = new PluginManifest(1, 2, "validation.lifecycle", "1.0.0", "Lifecycle", "StudioX.PluginRuntimeValidation.dll",
            "StudioX.PluginRuntimeValidation.ValidationPlugin", ["commands", "panels", "agentTools"], hashes,
            HostTools: ["project_edit_file"]);
        var manifestPath = Path.Combine(source, "plugin.json");
        await JsonStore.WriteAsync(manifestPath, manifest);
        var archive = Path.Combine(scratch, "lifecycle.studioxplugin");
        await PluginRepository.PackAsync(source, archive);
        var data = Path.Combine(scratch, "lifecycle-data");
        await using var services = new WorkbenchService(runtime, data);
        _ = await services.PluginManager.ImportAsync(archive);
        await services.PluginManager.SetEnabledAsync(manifest.Id, true);
        var authorizer = new DelayedAuthorizer();
        await using var broker = new PluginWorkspaceBroker(services, project, authorizer);
        var operations = 0;
        broker.OperationCompleted += (_, _) => Interlocked.Increment(ref operations);
        await using var workspace = await services.PluginManager.OpenWorkspaceAsync(project, broker.CallAsync);
        checks.Check(workspace.Contributions.Count == 1, "lifecycle fixture passes validated activation");
        var document = await services.Files.ReadAsync(project, "src/main.c");
        var invocation = workspace.InvokeAsync(manifest.Id, "command", "write",
            JsonSerializer.SerializeToElement(new
            {
                path = "src/main.c",
                originalSha256 = document.DiskHash,
                content = "int must_not_be_written;\n"
            }));
        await authorizer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await services.PluginManager.SetEnabledAsync(manifest.Id, false);
        authorizer.Release.TrySetResult(true);
        try
        {
            _ = await invocation;
            throw new InvalidOperationException("停用后旧调用仍成功。");
        }
        catch (Exception exception) when (exception is OperationCanceledException or StudioXException)
        {
            // 进程结束及会话撤销均可终结在途调用，最终断言检查没有迟到副作用。
        }
        await authorizer.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await broker.StopPluginAsync(manifest.Id);
        checks.Check(await File.ReadAllTextAsync(Path.Combine(project, "src/main.c")) == document.Text && Volatile.Read(ref operations) == 0,
            "disable during pending host approval prevents late source write and completion callback");

        var invalidManifest = manifest with
        {
            Id = "validation.invalid",
            EntryType = "StudioX.PluginRuntimeValidation.InvalidContributionPlugin",
            Capabilities = ["panels"],
            HostTools = ["validation.activation"]
        };
        await JsonStore.WriteAsync(manifestPath, invalidManifest);
        var activationCalls = 0;
        await checks.RejectAsync(async () =>
        {
            await using var client = await PluginRuntimeClient.StartAsync(Path.Combine(runtime, "plugin-host/StudioX.PluginHost.exe"), manifestPath,
                (_, _, _) =>
                {
                    Interlocked.Increment(ref activationCalls);
                    return Task.FromResult(JsonSerializer.SerializeToElement(new
                    {
                    }));
                }, (_, _) => Task.CompletedTask, contribution => PluginContributionValidator.Validate(invalidManifest, contribution));
        }, "invalid executable UI contribution rejected before activation", "PLUGIN_CONTRIBUTION");
        checks.Check(activationCalls == 0, "invalid contribution never reaches Activate host callback");
    }

    private sealed class DelayedAuthorizer : IStudioXMcpAuthorizer
    {
        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<bool> ApproveAsync(StudioXMcpApprovalRequest request, CancellationToken token)
        {
            Entered.TrySetResult(true);
            try
            {
                // 故意模拟已经显示且稍后才返回的审批；业务层必须在返回后重新核对取消。
                return await Release.Task;
            }
            finally
            {
                Completed.TrySetResult(true);
            }
        }
    }
}
