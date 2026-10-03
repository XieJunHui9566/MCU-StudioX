namespace StudioX.Application.Tools;

using System.Security.Cryptography;
using System.Text.Json;
using StudioX.Engine;
using StudioX.Foundation;

public sealed partial class ProjectToolPreparationService
{
    public Task<DevelopmentComponentCompatibility> PreviewCompatibilityAsync(ProjectToolPlan? plan, ToolArchivePreview candidate,
        CancellationToken token = default) => Task.Run(async () =>
    {
        // 保持归档读锁，防止读取的清单与已记录的归档哈希来自不同文件。
        await using var file = new FileStream(candidate.Archive, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
        if (!Convert.ToHexString(await SHA256.HashDataAsync(file, token)).Equals(candidate.ArchiveSha256, StringComparison.OrdinalIgnoreCase))
            throw new StudioXException("TOOLS_CHANGED", "组件归档在预览后发生变化，请重新预览。");
        file.Position = 0;
        using var container = ToolchainArchive.Open(file, token);
        var bytes = await container.ReadManifestAsync(token);
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(candidate.Fingerprint, StringComparison.OrdinalIgnoreCase))
            throw new StudioXException("TOOLS_CHANGED", "组件清单与安装预览不一致。");
        var next = Deserialize(bytes);
        container.ValidateIndex(next.Sha256);
        if (next.Identity != candidate.Identity) throw new StudioXException("TOOLS_CHANGED", "组件身份与安装预览不一致。");
        var differences = new List<string> { "待安装组件：" + candidate.Identity.Key + " · " + candidate.CompilerId };
        var blockers = new List<string>();
        var state = ComponentProjectCompatibility.NoProject;
        var removed = Array.Empty<string>();
        string summary;
        if (plan?.ProjectDirectory is not { } root) summary = "没有选择工程；此操作只并存安装开发环境组件。";
        else
        {
            if (plan.Fingerprint != await FingerprintAsync(root, token))
                throw new StudioXException("TOOLS_PROJECT_CHANGED", "工程配置或内容锁已变化，请重新检查和预览。");
            var current = await InspectAsync(root, token: token);
            if (current.Fingerprint != plan.Fingerprint) throw new StudioXException("TOOLS_PROJECT_CHANGED", "工程配置已变化。");
            var need = current.Requirements.SingleOrDefault(r => r.Id == candidate.Id);
            if (need is null)
            {
                state = ComponentProjectCompatibility.Unrelated;
                summary = "当前工程没有声明此组件；安装后当前工程不会使用它。";
            }
            else
            {
                var project = await ProjectService.ReadAsync(root, token);
                differences.Add("工程要求：" + need.Id + "/" + need.Version + " · " + need.CompilerId);
                state = candidate.Version == need.Version ? ComponentProjectCompatibility.ExactRequirement : ComponentProjectCompatibility.MigrationRequired;
                if (candidate.CompilerId != need.CompilerId) blockers.Add($"编译器身份不同：{need.CompilerId} → {candidate.CompilerId}，需要重新验证代码、ABI 与库。");
                if (candidate.Version == need.Version && need.LockedFingerprint is { } pin && !pin.Equals(candidate.Fingerprint, StringComparison.OrdinalIgnoreCase))
                    blockers.Add("相同版本的清单指纹与工程内容锁不同，不能替代锁定内容。");
                if (candidate.Version != need.Version)
                {
                    blockers.Add("器件/模板与工程需求仍锁定 " + need.Version + "；需要明确匹配的器件包和工程迁移验证，不能直接删锁或改用新版本。");
                    if (candidate.CompilerId == need.CompilerId) differences.Add("编译器身份相同，但组件文件与内部工具可能不同，尚未证明工程兼容。");
                }
                var oldPath = PathBoundary.Resolve(catalog.RootDirectory, need.Id + "/" + need.Version + "/toolset.json");
                if (File.Exists(oldPath))
                {
                    var old = await ReadInstalledMetadataAsync(oldPath, token);
                    if (old.Id != need.Id || old.Version != need.Version || old.CompilerId != need.CompilerId || old.Host != candidate.Host)
                        blockers.Add("原组件清单身份异常，不能使用它作可靠差异基准；请先修复原组件。");
                    else
                    {
                        foreach (var key in (old.ComponentVersions?.Keys.AsEnumerable() ?? []).Union(next.ComponentVersions?.Keys.AsEnumerable() ?? []).Order(StringComparer.Ordinal))
                        {
                            var before = old.ComponentVersions?.GetValueOrDefault(key) ?? "未声明";
                            var after = next.ComponentVersions?.GetValueOrDefault(key) ?? "未声明";
                            if (before != after) differences.Add($"内部工具 {key}：{before} → {after}");
                        }
                        if (old.Purpose != next.Purpose) blockers.Add($"组件用途不同：{old.Purpose ?? "原生构建"} → {next.Purpose ?? "原生构建"}。");
                        removed = old.Sha256.Keys.Except(next.Sha256.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                    }
                }
                else differences.Add("原组件未安装；文件差异未比较。可先恢复原版本后重新预览。");
                CheckRequiredMetadata(next, need, project, blockers);
                if (candidate.Version == need.Version && blockers.Count > 0) state = ComponentProjectCompatibility.Incompatible;
                summary = state switch
                {
                    ComponentProjectCompatibility.ExactRequirement => "组件元数据匹配当前工程的精确需求；安装时仍须完整校验文件。",
                    ComponentProjectCompatibility.Incompatible => "此归档不能供当前工程使用；本入口已阻止安装。",
                    _ => "可并存安装新组件；当前工程继续使用原版本，需要迁移验证后才能切换。"
                };
            }
            if (plan.Fingerprint != await FingerprintAsync(root, token))
                throw new StudioXException("TOOLS_PROJECT_CHANGED", "升级预览期间工程配置发生变化，请重试。");
        }
        return new DevelopmentComponentCompatibility(candidate.ArchiveSha256, plan?.ProjectDirectory, plan?.Fingerprint, state,
            summary, differences, blockers, removed.Length,
            removed.OrderByDescending(p => p.EndsWith(".h", StringComparison.OrdinalIgnoreCase)).Take(20).ToArray());
    }, token);

    public async Task InstallWithCompatibilityAsync(ProjectToolPlan? plan, ToolArchivePreview archive,
        DevelopmentComponentCompatibility preview, IProgress<string>? progress = null, CancellationToken token = default)
    {
        if (preview.ArchiveSha256 != archive.ArchiveSha256 || preview.ProjectDirectory != plan?.ProjectDirectory || preview.ProjectFingerprint != plan?.Fingerprint)
            throw new StudioXException("TOOLS_CHANGED", "兼容性预览与当前选择不一致，请重新预览。");
        var current = await PreviewCompatibilityAsync(plan, archive, token);
        if (!current.CanInstall) throw new StudioXException("TOOLS_PROJECT_IDENTITY", current.ToText());
        if (InstallSpacePlan(archive).Any(p => p.RequiredBytes > p.AvailableBytes)) throw new StudioXException("INSTALL_SPACE", "组件安装暂存空间不足。");
        await management.InstallAsync(archive, progress, token);
    }

    private static void CheckRequiredMetadata(ToolsetManifest manifest, ProjectToolRequirement need, ProjectManifest project, List<string> blockers)
    {
        var roles = need.Id switch
        {
            "agm.pin-mapping" => new[] { "python", "converter", "supra" }, "agm.logic" => ["mapper"], "hdl.iverilog" => ["iverilog", "vvp"],
            "stc.sdcc" => ["cmake", "ninja", "sdcc", "sdar", "sdas8051", "sdld", "packihx"],
            "pc.mingw" => ["gcc", "gxx", "ar", "ranlib", "as", "ld", "objcopy", "objdump", "size"],
            _ => ["cmake", "ninja", "gcc", "gxx", "objcopy", "size"]
        };
        if (need.Id == project.ToolsetId && project.Espressif is { } sdk)
        {
            if (manifest.Purpose != sdk.Framework) blockers.Add("框架不匹配：工程要求 " + sdk.Framework + "。");
            var candidateSdk = EspressifSdkIdentity.DeclaredVersion(manifest);
            if (candidateSdk != sdk.SdkVersion) blockers.Add($"SDK 版本不同：工程 {sdk.SdkVersion} → 组件 SDK {candidateSdk}。需要显式迁移 SDK 配置和 API。");
            var suffix = sdk.Target is "esp32c3" or "esp32c5" or "esp32c6" or "esp32p4" ? "riscv" : sdk.Target;
            roles = ["python", "cmake", "ninja", "git", "gcc-" + suffix, "gxx-" + suffix, "size-" + suffix];
            foreach (var resource in new[] { "idf", "tools", "python-env" })
            {
                if (manifest.ResourceDirectories is null || !manifest.ResourceDirectories.TryGetValue(resource, out var relative))
                    blockers.Add("缺少框架资源声明：" + resource);
                else
                {
                    _ = PathBoundary.Resolve(Path.GetTempPath(), relative);
                    var prefix = relative.TrimEnd('/') + "/";
                    if (!manifest.Sha256.Keys.Any(p => p.StartsWith(prefix, StringComparison.Ordinal))) blockers.Add("框架资源未索引：" + resource);
                    if (resource == "idf" && (!manifest.Sha256.ContainsKey(prefix + "tools/idf.py") || !manifest.Sha256.ContainsKey(prefix + "tools/cmake/project.cmake")))
                        blockers.Add("SDK 缺少原生构建入口。");
                }
            }
        }
        foreach (var role in roles)
        {
            if (!manifest.Executables.TryGetValue(role, out var relative) || !manifest.Sha256.ContainsKey(relative)) blockers.Add("缺少或未索引工程工具入口：" + role);
            else _ = PathBoundary.Resolve(Path.GetTempPath(), relative);
        }
    }

    private static async Task<ToolsetManifest> ReadInstalledMetadataAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
        if (stream.Length > 32 * 1024 * 1024) throw new StudioXException("TOOLSET_MANIFEST", "原组件清单过大。");
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, token);
        return Deserialize(memory.ToArray());
    }
    private static ToolsetManifest Deserialize(byte[] bytes)
    {
        var offset = bytes is [0xef, 0xbb, 0xbf, ..] ? 3 : 0;
        var manifest = JsonSerializer.Deserialize<ToolsetManifest>(bytes.AsSpan(offset), JsonStore.Options)
            ?? throw new StudioXException("TOOLSET_MANIFEST", "组件清单为空。");
        _ = manifest.Identity;
        if (manifest.Executables is null || manifest.Sha256 is null) throw new StudioXException("TOOLSET_MANIFEST", "组件清单不完整。");
        return manifest;
    }
}
