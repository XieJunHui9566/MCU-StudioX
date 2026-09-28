namespace StudioX.Engine;

using StudioX.Foundation;
using StudioX.Packages;

/// <summary>从已安装的 Zephyr 专用包创建离线工程；此阶段不配置 SDK 或触碰硬件。</summary>
public sealed class ZephyrProjectService(Func<string, CancellationToken, Task>? initializeRepository = null)
{
    internal static StudioXException RuntimeUnavailable() => new("ZEPHYR_RUNTIME_UNAVAILABLE",
        "Zephyr 实验工程运行时尚未准备；当前仅支持离线创建与打开，不能使用裸机后端构建、下载或调试。");

    /// <summary>从创建时的板级模板映射定位工程内的 DTS 源文件，不把未合成的板级 DTS 当作完整设备树。</summary>
    public async Task<string?> FindBoardDevicetreeAsync(string directory, ProjectManifest project,
        CancellationToken cancellationToken = default)
    {
        var manifest = await ZephyrProjectSettings.ReadValidatedManifestAsync(directory, project, cancellationToken);
        var board = manifest.Boards.Single(item => item.Id == project.DeviceId);
        var template = board.Templates.Single(item => item.Id == project.TemplateId);
        if (template.Files is null)
        {
            return null;
        }
        var boardName = project.Zephyr!.BoardTarget.Split('/')[0].Split('@')[0] + ".dts";
        foreach (var relative in template.Files.Keys
            .Where(path => path.StartsWith("boards/", StringComparison.OrdinalIgnoreCase) &&
                path.EndsWith(".dts", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(path => Path.GetFileName(path).Equals(boardName, StringComparison.OrdinalIgnoreCase))
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(PathBoundary.Resolve(directory, relative)))
            {
                return relative;
            }
        }
        return null;
    }

    public async Task<ProjectManifest> CreateAsync(InstalledZephyrPack selected, string boardId, string templateId,
        string name, string destination, CancellationToken cancellationToken = default)
    {
        PackValidator.Token(name);
        var target = Path.GetFullPath(destination);
        if (Directory.Exists(target) || File.Exists(target))
        {
            throw new StudioXException("PROJECT_EXISTS", "目标路径已存在，请选择新的工程目录。");
        }

        // 目录浏览结果只是候选；复制前重新核对安装内容的完整指纹与全部模板文件。
        var pack = await ZephyrPackRepository.VerifyAsync(selected, cancellationToken);
        var board = pack.Manifest.Boards.SingleOrDefault(item => item.Id == boardId)
            ?? throw new StudioXException("PROJECT_ZEPHYR_BOARD", "请选择包中明确的 Zephyr 板级目标。");
        var template = board.Templates.SingleOrDefault(item => item.Id == templateId)
            ?? throw new StudioXException("PROJECT_ZEPHYR_TEMPLATE", "请选择此板卡提供的 Zephyr 工程模板。");
        var project = new ProjectManifest(1, name, pack.Manifest.Id, pack.Manifest.Version, pack.ContentHash,
            board.Id, template.Id, "", "", "", ProjectKind.Zephyr, EntryFile: "src/main.c",
            Zephyr: new(1, board.Id, board.BoardTarget, board.BoardRevision, pack.Manifest.ZephyrVersion,
                pack.Manifest.Experimental));
        ZephyrProjectSettings.Validate(project);

        var parent = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, ".studiox-create-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(staging);
            foreach (var (relative, source) in template.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var output = PathBoundary.Resolve(staging, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                File.Copy(PathBoundary.Resolve(pack.RootDirectory, source), output);
            }
            if (File.Exists(Path.Combine(staging, ".gitignore")))
            {
                throw new StudioXException("PROJECT_RESERVED_FILE", "Zephyr 模板占用了工程生成器路径：.gitignore");
            }
            Directory.CreateDirectory(Path.Combine(staging, ".studiox"));
            File.Copy(PathBoundary.Resolve(pack.RootDirectory, ZephyrPackValidator.ManifestFile),
                Path.Combine(staging, ".studiox", "zephyr-pack.json"));
            await JsonStore.WriteAsync(Path.Combine(staging, ".studiox", "project.json"), project, cancellationToken);
            _ = await ProjectService.ReadAsync(staging, cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(staging, ".gitignore"),
                "build/\n.build/\ncmake-build-*/\n.studiox/debug.json\n.studiox/breakpoints.json\n*.user\n", cancellationToken);
            if (initializeRepository is not null)
            {
                await initializeRepository(staging, cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(staging, target);
            return project;
        }
        finally { if (Directory.Exists(staging)) { Directory.Delete(staging, recursive: true); } }
    }
}
