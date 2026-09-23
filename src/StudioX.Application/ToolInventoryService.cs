namespace StudioX.Application;

using System.Text;
using StudioX.Engine;
using StudioX.Foundation;

public sealed class ToolInventoryService(ToolsetCatalog catalog)
{
    public async Task<string> DescribeAsync(bool verify, IProgress<string>? progress = null, CancellationToken token = default)
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
                    foreach (var (component, version) in manifest.ComponentVersions) text.AppendLine($"  {component}: {version}");
                text.AppendLine($"  编译器标识：{manifest.CompilerId}");
                if (verify)
                {
                    progress?.Report("校验工具集：" + manifest.Id);
                    var resolved = await catalog.ResolveAsync(manifest.Id, manifest.Version, manifest.CompilerId, token, forceVerification: true, progress: progress);
                    foreach (var role in new[] { "gcc", "gxx", "gdb", "cmake", "ninja", "openocd" })
                    {
                        if (!manifest.Executables.ContainsKey(role)) continue;
                        var result = await new ProcessRunner().RunAsync(new(resolved.Tool(role), ["--version"], resolved.RootDirectory, TimeSpan.FromSeconds(20),
                            ToolsetEnvironment.Create(resolved), RemoveEnvironment: ToolsetEnvironment.AmbientVariables), token);
                        if (!result.Success) throw new StudioXException("TOOL_EXECUTE", $"{role} 无法启动：\n{result.StandardOutput}\n{result.StandardError}");
                    }
                    text.AppendLine($"  完整性与启动检查通过（{manifest.Sha256.Count} 个文件）");
                }
                else text.AppendLine("  已内置 · 首次构建完整校验，未变化时复用结果");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) { text.AppendLine("  检查失败：" + ex); }
            text.AppendLine();
        }
        return text.Length > 0 ? text.ToString() : "此安装尚未附带工具集。请使用包含工具资源的 StudioX 发行版。";
    }
}
