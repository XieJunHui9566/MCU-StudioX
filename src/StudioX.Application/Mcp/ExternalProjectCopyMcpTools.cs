namespace StudioX.Application.Mcp;

using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;
using StudioX.Foundation;
using static ExternalProjectPathPolicy;

/// <summary>按内容清单审批并事务复制外部库，仅新建绑定工程文件，失败清理临时内容。</summary>
internal sealed class ExternalProjectCopyMcpTools(McpSessionContext context) : StudioXMcpToolProvider(context)
{
    private const int ExternalCopyFiles = 2_000;
    private const long ExternalCopyBytes = 128L * 1024 * 1024;
    private const long ExternalCopySingleFileBytes = 16L * 1024 * 1024;
    [McpServerTool(Name = "external_project_copy")]
    [Description("把已授权外部目录本身、其中单个文件或通用库文件夹复制进当前绑定工程。会跳过隐藏/构建/凭据项；逐次请求写入授权，禁止覆盖，最多 2000 文件/128 MiB。")]
    public async Task<string> ExternalProjectCopyAsync(
        [Description("external_project_open 返回的 rootId。")]
        string rootId,
        [Description("授权外部目录内的相对文件或文件夹路径；空字符串表示复制获批的整个目录。")]
        string sourcePath,
        [Description("当前绑定工程内的目标相对路径；文件指定文件名，文件夹指定目标文件夹名。")]
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        await RequireWorkspaceProjectAsync(cancellationToken).ConfigureAwait(false);
        var root = await RequireExternalRootAsync(rootId, cancellationToken).ConfigureAwait(false);
        var source = ResolveExternalPath(root, sourcePath, allowRoot: true);
        var destination = ResolveCopyDestination(destinationPath);
        if (File.Exists(destination) || Directory.Exists(destination))
        {
            throw new StudioXException("MCP_EXTERNAL_COPY_EXISTS", "目标已存在；不会覆盖或自动改名。");
        }
        var plan = await Task.Run(() => PlanExternalCopy(root, sourcePath, cancellationToken), cancellationToken).ConfigureAwait(false);
        await RequireSavedDocumentsAsync().ConfigureAwait(false);
        await RequireApprovalAsync("external_project_copy",
            $"从只读示例 {source} 复制到当前工程 {destination}。{plan.Files.Count} 个文件 / {plan.TotalBytes} 字节；" +
            $"内容清单 SHA-256 {plan.Sha256}；跳过 {plan.Skipped} 个隐藏、构建、凭据或链接项。仅新建，不覆盖；不会自动编译或烧录。",
            StudioXMcpPermission.FileWrite, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        root = await RequireExternalRootAsync(rootId, cancellationToken).ConfigureAwait(false);
        source = ResolveExternalPath(root, sourcePath, allowRoot: true);
        var current = await Task.Run(() => PlanExternalCopy(root, sourcePath, cancellationToken), cancellationToken).ConfigureAwait(false);
        if (!current.Sha256.Equals(plan.Sha256, StringComparison.Ordinal) ||
            current.Skipped != plan.Skipped || current.Files.Count != plan.Files.Count ||
            !current.Directories.SequenceEqual(plan.Directories, StringComparer.Ordinal))
        {
            throw new StudioXException("MCP_EXTERNAL_CHANGED", "审批期间外部文件发生变化，请重新检查后复制。");
        }
        destination = ResolveCopyDestination(destinationPath);
        await StageExternalCopyAsync(root, sourcePath, destinationPath, destination, current, cancellationToken)
            .ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            source = sourcePath,
            destination = destinationPath,
            copiedFiles = current.Files.Count,
            bytes = current.TotalBytes,
            sha256 = current.Sha256,
            skippedEntries = current.Skipped,
            created = true
        });
    }

    private string ResolveCopyDestination(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Length > 1_024 ||
            relative.Split('/').Any(IsExcludedExternalName) ||
            relative.Split('/')[0].Equals("device", StringComparison.OrdinalIgnoreCase) ||
            !ProjectFileService.CanCreateIn(relative))
        {
            throw new StudioXException("MCP_EXTERNAL_DESTINATION", "只能复制到当前工程的用户源码目录，不能写入受保护目录。");
        }
        return PathBoundary.Resolve(Project, relative);
    }

    private sealed record ExternalCopyItem(string Source, string Relative, long Bytes, string Sha256);
    private sealed record ExternalCopyPlan(bool Directory, IReadOnlyList<string> Directories,
        IReadOnlyList<ExternalCopyItem> Files, long TotalBytes, int Skipped, string Sha256);

    /// <summary>先生成稳定内容清单，审批后重新比较，避免复制用户未审阅的新内容。</summary>
    private static ExternalCopyPlan PlanExternalCopy(string root, string sourcePath, CancellationToken token)
    {
        var source = ResolveExternalPath(root, sourcePath, allowRoot: true);
        var directory = Directory.Exists(source);
        if (!directory && !File.Exists(source))
        {
            throw new StudioXException("MCP_EXTERNAL_FILE", "外部复制源不存在。");
        }
        var directories = new List<string>();
        var files = new List<ExternalCopyItem>();
        var skipped = 0;
        var visited = 0;
        long bytes = 0;
        if (directory)
        {
            var pending = new Stack<(string Relative, int Depth)>();
            pending.Push(("", 0));
            while (pending.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                var (parent, depth) = pending.Pop();
                if (depth > 32)
                {
                    throw new StudioXException("MCP_EXTERNAL_COPY_LIMIT", "通用库目录层级超过 32 层。");
                }
                var current = ResolveExternalPath(root, CombineExternalPath(sourcePath, parent), allowRoot: true);
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new StudioXException("MCP_EXTERNAL_LINK", "复制源目录在扫描期间变成了链接，请重新选择。");
                }
                foreach (var entry in new DirectoryInfo(current).EnumerateFileSystemInfos())
                {
                    token.ThrowIfCancellationRequested();
                    if (++visited > 10_000)
                    {
                        throw new StudioXException("MCP_EXTERNAL_COPY_LIMIT", "通用库目录项超过 10000 项，请缩小复制范围。");
                    }
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0 || IsExcludedExternalName(entry.Name) ||
                        (entry.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0)
                    {
                        skipped++;
                        continue;
                    }
                    var relative = parent.Length == 0 ? entry.Name : parent + "/" + entry.Name;
                    if ((entry.Attributes & FileAttributes.Directory) != 0)
                    {
                        directories.Add(relative);
                        pending.Push((relative, depth + 1));
                        continue;
                    }
                    if (!IsExternalCopyFile(entry.Name))
                    {
                        skipped++;
                        continue;
                    }
                    AddFile(ResolveExternalPath(root, CombineExternalPath(sourcePath, relative)), relative);
                }
            }
        }
        else
        {
            if (!IsExternalCopyFile(Path.GetFileName(source)))
            {
                throw new StudioXException("MCP_EXTERNAL_FILE_TYPE", "该外部文件属于受保护类型，不能复制。");
            }
            if ((File.GetAttributes(source) & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) != 0)
            {
                throw new StudioXException("MCP_EXTERNAL_FILE_TYPE", "该外部文件是隐藏、系统或链接文件，不能复制。");
            }
            AddFile(source, "");
        }
        if (files.Count == 0)
        {
            throw new StudioXException("MCP_EXTERNAL_COPY_EMPTY", "所选目录没有可复制文件，请选择更具体的源码目录。");
        }
        var manifest = string.Join("\n", directories.OrderBy(item => item, StringComparer.Ordinal).Select(item => "D\0" + item)) +
            "\n" + string.Join("\n", files.OrderBy(item => item.Relative, StringComparer.Ordinal)
            .Select(item => "F\0" + item.Relative + "\0" + item.Bytes + "\0" + item.Sha256));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(manifest)));
        return new ExternalCopyPlan(directory, directories, files, bytes, skipped, hash);

        void AddFile(string path, string relative)
        {
            token.ThrowIfCancellationRequested();
            var length = new FileInfo(path).Length;
            if (length > ExternalCopySingleFileBytes || files.Count >= ExternalCopyFiles ||
                bytes + length > ExternalCopyBytes)
            {
                throw new StudioXException("MCP_EXTERNAL_COPY_LIMIT", "复制最多 2000 个文件、总计 128 MiB、单文件 16 MiB，请缩小范围。");
            }
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var digest = Convert.ToHexString(SHA256.HashData(stream));
            files.Add(new ExternalCopyItem(path, relative, length, digest));
            bytes += length;
        }
    }

    /// <summary>同目录暂存完成后移动提交；失败只撤销本次创建的暂存文件与空父目录。</summary>
    private async Task StageExternalCopyAsync(string root, string sourcePath, string destinationPath,
        string destination, ExternalCopyPlan plan, CancellationToken token)
    {
        var parentRelative = ProjectFileService.ParentDirectory(destinationPath);
        var createdParents = new List<string>();
        string? stage = null;
        var committed = false;
        try
        {
            var parts = parentRelative.Length == 0 ? [] : parentRelative.Split('/');
            for (var index = 1; index <= parts.Length; index++)
            {
                var relative = string.Join('/', parts.Take(index));
                var path = PathBoundary.Resolve(Project, relative);
                if (File.Exists(path))
                {
                    throw new StudioXException("MCP_EXTERNAL_DESTINATION", "复制目标的父路径不是目录。");
                }
                if (!Directory.Exists(path))
                {
                    Directory.CreateDirectory(path);
                    createdParents.Add(path);
                }
            }
            var parent = parentRelative.Length == 0 ? Project : PathBoundary.Resolve(Project, parentRelative);
            stage = Path.Combine(parent, ".studiox-copy-" + Guid.NewGuid().ToString("N"));
            if (plan.Directory)
            {
                Directory.CreateDirectory(stage);
                foreach (var relative in plan.Directories)
                {
                    Directory.CreateDirectory(PathBoundary.Resolve(stage, relative));
                }
                foreach (var item in plan.Files)
                {
                    var staged = PathBoundary.Resolve(stage, item.Relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
                    await CopyExternalItemAsync(root, CombineExternalPath(sourcePath, item.Relative), staged, item, token)
                        .ConfigureAwait(false);
                }
            }
            else
            {
                await CopyExternalItemAsync(root, sourcePath, stage, plan.Files[0], token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            _ = ResolveCopyDestination(destinationPath);
            if (File.Exists(destination) || Directory.Exists(destination))
            {
                throw new StudioXException("MCP_EXTERNAL_COPY_EXISTS", "目标已被其他操作创建，本次未覆盖。");
            }
            if (plan.Directory)
            {
                Directory.Move(stage, destination);
            }
            else
            {
                File.Move(stage, destination);
            }
            committed = true;
        }
        finally
        {
            if (!committed && stage is not null)
            {
                if (File.Exists(stage))
                {
                    File.Delete(stage);
                }
                else if (Directory.Exists(stage))
                {
                    Directory.Delete(stage, recursive: true);
                }
            }
            if (!committed)
            {
                foreach (var parent in createdParents.AsEnumerable().Reverse())
                {
                    if (Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
                    {
                        Directory.Delete(parent);
                    }
                }
            }
        }
    }

    private static async Task CopyExternalItemAsync(string root, string relativeSource, string target,
        ExternalCopyItem planned, CancellationToken token)
    {
        var source = ResolveExternalPath(root, relativeSource);
        await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
        {
            await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await input.CopyToAsync(output, token).ConfigureAwait(false);
            }
        }
        using var copied = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (copied.Length != planned.Bytes ||
            !Convert.ToHexString(SHA256.HashData(copied)).Equals(planned.Sha256, StringComparison.Ordinal))
        {
            throw new StudioXException("MCP_EXTERNAL_CHANGED", "外部源文件在复制期间发生变化，目标未提交。");
        }
    }
}
