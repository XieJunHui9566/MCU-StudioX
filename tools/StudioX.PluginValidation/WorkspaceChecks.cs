namespace StudioX.PluginValidation;

using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using StudioX.Application;
using StudioX.Application.Mcp;
using StudioX.Application.Plugins;
using StudioX.Engine;
using StudioX.Extensions;
using StudioX.Foundation;

/// <summary>通过真实应用服务与 MCP 客户端验证插件生命周期和工程权限。</summary>
internal static class WorkspaceChecks
{
    public static async Task RunAsync(string repository, string archive, string cli, string scratch, ValidationChecks checks)
    {
        var runtime = Path.Combine(scratch, "runtime");
        var data = Path.Combine(scratch, "data");
        Directory.CreateDirectory(runtime);
        RepositoryChecks.CopyDirectory(Path.Combine(repository, "src/StudioX.PluginHost/bin/Debug/net10.0"), Path.Combine(runtime, "plugin-host"));
        var project = Path.Combine(scratch, "project");
        Directory.CreateDirectory(Path.Combine(project, ".studiox"));
        Directory.CreateDirectory(Path.Combine(project, "src"));
        await JsonStore.WriteAsync(Path.Combine(project, ".studiox/project.json"),
            new ProjectManifest(1, "plugin-fixture", "validation.pack", "1.0.0", "offline", "validation", "blank", "gcc", "1.0.0", "gcc"));
        var mainPath = Path.Combine(project, "src/main.c");
        const string diskText = "int main(void) { return 0; }\n";
        await File.WriteAllTextAsync(mainPath, diskText);

        await using var services = new WorkbenchService(runtime, data);
        var entry = await services.PluginManager.ImportAsync(archive);
        var pluginId = entry.Id;
        checks.Check(!entry.Enabled && entry.CanEnable, "installation discovers metadata without execution or implicit enable");
        await services.PluginManager.SetEnabledAsync(pluginId, true);
        await using (var reopened = new PluginManagerService(runtime, data))
        {
            checks.Check((await reopened.ListAsync()).Single().Enabled, "explicit trust persists across manager instances");
        }
        var editor = new FixtureEditor();
        var dirty = false;
        await using var broker = new PluginWorkspaceBroker(services, project, new DenyStudioXMcpAuthorizer(), () => Task.FromResult(dirty), editor);
        await using var workspace = await services.PluginManager.OpenWorkspaceAsync(project, broker.CallAsync);
        checks.Check(workspace.Contributions.Count == 1 && workspace.LatestPanels.Count == 1,
            "workspace activation validates contributions and delivers initial panel before return");
        var overview = await workspace.InvokeAsync(pluginId, "command", "refresh", JsonSerializer.SerializeToElement(new { }));
        checks.Check(ProjectName(overview.GetProperty("project")) == "plugin-fixture", "real .NET command uses project-scoped host broker");

        var edited = await broker.CallAsync(pluginId, "editor_read", JsonSerializer.SerializeToElement(new { path = "src/main.c" }));
        checks.Check(edited.GetProperty("text").GetString() == editor.Buffer && edited.GetProperty("IsDirty").GetBoolean(),
            "editor_read uses current unsaved buffer instead of disk text");
        editor.Buffer = new string('a', 15999) + "😀tail";
        var firstPage = await broker.CallAsync(pluginId, "editor_read", JsonSerializer.SerializeToElement(new { path = "src/main.c" }));
        var secondPage = await broker.CallAsync(pluginId, "editor_read", JsonSerializer.SerializeToElement(new { path = "src/main.c", offset = 15999 }));
        checks.Check(firstPage.GetProperty("text").GetString()!.Length == 15999 && firstPage.GetProperty("nextOffset").GetInt32() == 15999 &&
            secondPage.GetProperty("text").GetString() == "😀tail",
            "editor paging preserves Unicode surrogate pair at 16000-character boundary");
        checks.Check(firstPage.GetProperty("contentHash").GetString() != edited.GetProperty("contentHash").GetString(),
            "unsaved buffer changes produce new contentHash independently of diskHash");
        await checks.RejectAsync(async () => { _ = await broker.CallAsync(pluginId, "editor_read", JsonSerializer.SerializeToElement(new { path = "src/main.c", offset = 16000 })); },
            "editor paging offset inside surrogate pair rejected", "PLUGIN_EDITOR_OFFSET");
        _ = await broker.CallAsync(pluginId, "editor_open", JsonSerializer.SerializeToElement(new { path = "src/main.c", line = 3, column = 7 }));
        checks.Check(editor.LastLocation == ("src/main.c", 3, 7), "editor_open routes validated line and column to desktop abstraction");
        await checks.RejectAsync(async () => { _ = await broker.CallAsync(pluginId, "editor_read", JsonSerializer.SerializeToElement(new { path = "../secret.c" })); },
            "editor path escape rejected", "PLUGIN_EDITOR_PATH", "PATH_UNSAFE");
        await checks.RejectAsync(async () => { _ = await broker.CallAsync(pluginId, "plugin_recursive", JsonSerializer.SerializeToElement(new { })); },
            "broker rejects recursive plugin tools", "PLUGIN_HOST_TOOL");

        var disk = await services.Files.ReadAsync(project, "src/main.c");
        var writeArguments = JsonSerializer.SerializeToElement(new { path = "src/main.c", originalSha256 = disk.DiskHash, content = "int changed;\n" });
        var refused = await broker.CallAsync(pluginId, "project_edit_file", writeArguments);
        checks.Check(refused.GetRawText().Contains("MCP_APPROVAL_DENIED", StringComparison.Ordinal) && await File.ReadAllTextAsync(mainPath) == diskText,
            "plugin host writes require approval and denied write preserves source");
        dirty = true;
        var dirtyRefusal = await broker.CallAsync(pluginId, "project_edit_file", writeArguments);
        checks.Check(dirtyRefusal.GetRawText().Contains("MCP_UNSAVED_FILES", StringComparison.Ordinal) && await File.ReadAllTextAsync(mainPath) == diskText,
            "dirty editor blocks plugin writes before approval");
        dirty = false;

        await using (var mcp = await StudioXMcpSession.CreateAsync(new StudioXMcpTools(services, project, new DenyStudioXMcpAuthorizer())))
        {
            var tool = (await mcp.ListToolsAsync()).Single(item => item.Name.StartsWith("plugin_", StringComparison.Ordinal) && item.Name != "plugin_status");
            checks.Check(tool.Name.Length <= 64 && tool.ParametersJson.Contains("object", StringComparison.Ordinal),
                "real internal MCP discovery exposes namespaced plugin Agent tool and schema");
            using var result = JsonDocument.Parse(await mcp.CallToolAsync(tool.Name, "{}"));
            checks.Check(ProjectName(result.RootElement.GetProperty("project")) == "plugin-fixture", "internal Agent MCP calls real plugin and nested host tools");
            await services.PluginManager.SetEnabledAsync(pluginId, false);
            using var disabled = JsonDocument.Parse(await mcp.CallToolAsync(tool.Name, "{}"));
            checks.Check(disabled.RootElement.GetRawText().Contains("PLUGIN_INACTIVE", StringComparison.Ordinal),
                "disable revokes frozen Agent tool execution immediately");
            await checks.RejectAsync(async () => { _ = await workspace.InvokeAsync(pluginId, "command", "refresh", JsonSerializer.SerializeToElement(new { })); },
                "disable stops every existing workspace session", "PLUGIN_INACTIVE");
        }
        await services.PluginManager.SetEnabledAsync(pluginId, true);
        await RunExternalAsync(cli, project, runtime, data, checks);

        var installed = (await new PluginRepository(Path.Combine(data, "plugins")).ListAsync()).Single();
        var updateSource = Path.Combine(scratch, "manager-upgrade-source");
        RepositoryChecks.CopyDirectory(installed.Directory, updateSource);
        await JsonStore.WriteAsync(Path.Combine(updateSource, "plugin.json"), installed.Manifest with { Version = "1.2.0" });
        var updateArchive = Path.Combine(scratch, "manager-upgrade.studioxplugin");
        await PluginRepository.PackAsync(updateSource, updateArchive);
        var updated = await services.PluginManager.ImportAsync(updateArchive);
        checks.Check(updated.Version == "1.2.0" && !updated.Enabled, "content update revokes previous trust and stays disabled");
        await services.PluginManager.UninstallAsync(pluginId);
        checks.Check((await services.PluginManager.ListAsync()).Count == 0, "manager uninstall clears settings and user plugin");
    }

    private static async Task RunExternalAsync(string cli, string project, string runtime, string data, ValidationChecks checks)
    {
        if (!File.Exists(cli))
        {
            throw new FileNotFoundException("外部 CLI 验证需要已构建的 CLI EXE。", cli);
        }
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = cli,
            Arguments = ["mcp", project, runtime, data],
            Name = "StudioX isolated plugin validation",
            InheritEnvironmentVariables = false,
            EnvironmentVariables = StdioClientTransportOptions.GetDefaultEnvironmentVariables()
        });
        await using var client = await McpClient.CreateAsync(transport);
        var listed = await client.ListToolsAsync();
        var tool = listed.Single(item => item.ProtocolTool.Name.StartsWith("plugin_", StringComparison.Ordinal) && item.ProtocolTool.Name != "plugin_status");
        checks.Check(tool.ProtocolTool.Name.Length <= 64 && listed.Any(item => item.ProtocolTool.Name == "plugin_status"),
            "external CLI stdio handshake discovers dynamic plugin tool and status");
        var response = await client.CallToolAsync(tool.ProtocolTool.Name, new Dictionary<string, object?>());
        using var result = JsonDocument.Parse(string.Join("\n", response.Content.OfType<TextContentBlock>().Select(item => item.Text)));
        checks.Check(response.IsError != true && ProjectName(result.RootElement.GetProperty("project")) == "plugin-fixture",
            "external stdio client invokes actual plugin with project-scoped host callback");
    }

    private static string? ProjectName(JsonElement project)
    {
        return project.TryGetProperty("Name", out var name) ? name.GetString() : project.GetProperty("name").GetString();
    }

    private sealed class FixtureEditor : IPluginEditorAccess
    {
        public string Buffer { get; set; } = "int unsaved_editor_buffer;\n";
        public (string Path, int Line, int Column)? LastLocation { get; private set; }

        public Task<PluginEditorSnapshot?> ReadAsync(string relativePath, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult<PluginEditorSnapshot?>(new(relativePath, Buffer, true, new string('A', 64)));
        }

        public Task OpenAsync(string relativePath, int line, int column, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            LastLocation = (relativePath, line, column);
            return Task.CompletedTask;
        }
    }
}
