namespace StudioX.Application;

using System.Text;
using StudioX.Engine;
using StudioX.Foundation;

public sealed class ToolInventoryService(ToolsetCatalog catalog)
{
    // 启动和列表刷新只读清单；校验只能来自明确的工程需求或所选组件操作。
    public Task<string> DescribeAsync(CancellationToken token = default) => Task.Run(async () =>
    {
        var text = new StringBuilder();
        foreach (var path in catalog.ManifestPaths().Order(StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var manifest = await JsonStore.ReadAsync<ToolsetManifest>(path, token);
                text.AppendLine($"{manifest.DisplayName ?? manifest.Id}  ·  {manifest.Id} / {manifest.Version}");
                if (manifest.ComponentVersions is not null)
                {
                    foreach (var (component, version) in manifest.ComponentVersions)
                    {
                        text.AppendLine($"  {component}: {version}");
                    }
                }
                text.AppendLine($"  编译器标识：{manifest.CompilerId}");
                text.AppendLine(catalog.IsEnabled(manifest.Id, manifest.Version) ? "  已安装 · 使用此组件时校验，未变化时复用结果" : "  已禁用 · 可在开发环境组件管理中启用");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) { text.AppendLine("  检查失败：" + ex); }
            text.AppendLine();
        }
        return text.Length > 0 ? text.ToString() : "尚未安装开发环境组件。请在开发环境组件管理中从 GitHub 获取，或导入工程要求的 .mcutoolchain。";
    }, token);

    public Task<string> VerifyProjectAsync(string projectDirectory, IProgress<string>? progress = null, CancellationToken token = default)
        => Task.Run(async () =>
        {
            var project = await ProjectService.ReadAsync(projectDirectory, token);
            var needs = await ProjectDevelopmentComponents.ReadAsync(projectDirectory, project, token);
            var pins = await ProjectDevelopmentComponents.ReadPinsAsync(projectDirectory, needs, token);
            var verified = new List<ResolvedToolset>();
            var text = new StringBuilder("只检查当前工程声明的开发环境组件，不修改工程或内容锁。\n\n");
            foreach (var need in needs)
            {
                token.ThrowIfCancellationRequested();
                progress?.Report($"校验工程开发环境组件：{need.Id} / {need.Version}…");
                var resolved = await catalog.ResolveAsync(need.Id, need.Version, need.CompilerId, token, true, progress);
                ProjectDevelopmentComponents.CheckFingerprint(pins, need.Id, resolved.Fingerprint);
                verified.Add(resolved);
                using var lease = ToolUsageLease.Acquire(resolved.RootDirectory);
                if (need.Id == project.ToolsetId && project.Espressif is { } sdk)
                {
                    resolved = resolved.ForEspressifTarget(sdk.Target);
                }
                foreach (var role in new[] { "gcc", "gxx", "gdb", "cmake", "ninja", "openocd", "sdcc" })
                {
                    if (!resolved.Manifest.Executables.ContainsKey(role))
                    {
                        continue;
                    }
                    var arguments = role == "gdb" ? new[] { "--nx", "--nh", "--version" } : ["--version"];
                    var result = await new ProcessRunner().RunAsync(new(resolved.Tool(role), arguments, resolved.RootDirectory, TimeSpan.FromSeconds(20),
                        ToolsetEnvironment.Create(resolved), RemoveEnvironment: ToolsetEnvironment.AmbientVariables), token);
                    if (!result.Success)
                    {
                        throw new StudioXException("TOOL_EXECUTE", $"{need.Id} / {need.Version} · {role} 无法启动：\n{result.StandardOutput}\n{result.StandardError}");
                    }
                }
                text.AppendLine($"✓ {resolved.Manifest.DisplayName ?? need.Id} · {need.Id} / {need.Version}：完整性与启动检查通过（{resolved.Manifest.Sha256.Count:N0} 个文件）");
            }
            var current = await ProjectService.ReadAsync(projectDirectory, token);
            if (current != project || !needs.SequenceEqual(await ProjectDevelopmentComponents.ReadAsync(projectDirectory, current, token)))
            {
                throw new StudioXException("TOOLS_PROJECT_CHANGED", "校验期间工程开发环境组件需求发生变化，请重试。");
            }
            pins = await ProjectDevelopmentComponents.ReadPinsAsync(projectDirectory, needs, token);
            foreach (var resolved in verified)
            {
                ProjectDevelopmentComponents.CheckFingerprint(pins, resolved.Manifest.Id, resolved.Fingerprint);
            }
            if (needs.Count == 0)
            {
                text.AppendLine("此工程未声明本机开发环境组件，无需校验。");
            }
            return text.ToString();
        }, token);
}
