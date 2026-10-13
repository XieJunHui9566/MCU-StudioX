namespace StudioX.Application.BuildConfiguration;

using System.Security.Cryptography;
using StudioX.Application.Editing;
using StudioX.Engine;
using StudioX.Foundation;

/// <summary>源码登记的读取、建议与快照复核；不执行用户 CMake，不写源码或编译参数。</summary>
public sealed class SourceRegistrationService(ProjectFileService files)
{
    public static bool IsSource(string path) => Path.GetExtension(path).ToLowerInvariant() is ".c" or ".cc" or ".cpp" or ".cxx" or ".s";
    public Task<(IReadOnlyList<string> Configurations, IReadOnlyList<string> Sources)> DiscoverAsync(string project, CancellationToken token = default) => Task.Run(() =>
    {
        var configurations = new List<string>();
        var sources = new List<string>();
        var pending = new Stack<string>();
        pending.Push("");
        var visited = 0;
        while (pending.TryPop(out var directory))
        {
            foreach (var entry in files.Enumerate(project, directory, token))
            {
                token.ThrowIfCancellationRequested();
                if (++visited > 20000)
                {
                    throw new StudioXException("SOURCE_DISCOVERY_LIMIT", "源码登记最多核对 20,000 项；请在工程树选择源码所在构建文件并缩小工程范围。");
                }
                if (entry.IsLink || !UserPath(entry.RelativePath))
                {
                    continue;
                }
                if (entry.IsDirectory)
                {
                    pending.Push(entry.RelativePath);
                }
                else if (entry.Name.Equals("CMakeLists.txt", StringComparison.OrdinalIgnoreCase))
                {
                    configurations.Add(entry.RelativePath);
                }
                else if (IsSource(entry.RelativePath))
                {
                    sources.Add(entry.RelativePath);
                }
            }
        }
        return ((IReadOnlyList<string>)configurations.Order(StringComparer.OrdinalIgnoreCase).ToArray(), (IReadOnlyList<string>)sources.Order(StringComparer.OrdinalIgnoreCase).ToArray());
    }, token);

    public async Task<SourceRegistrationContext> ReadAsync(string project, string configuration, IReadOnlyList<WorkspaceBufferSnapshot> buffers, CancellationToken token = default)
    {
        project = Path.TrimEndingDirectorySeparator(Path.GetFullPath(project));
        configuration = CheckPath(project, configuration);
        if (!Path.GetFileName(configuration).Equals("CMakeLists.txt", StringComparison.OrdinalIgnoreCase))
        {
            throw SourceListEditor.Unsupported("请选择用户 CMakeLists.txt");
        }
        var manifest = await ProjectService.ReadAsync(project, token).ConfigureAwait(false);
        if (manifest.Kind is ProjectKind.MicroPython or ProjectKind.Zephyr || manifest.Espressif is { Framework: not "esp-idf" })
        {
            throw SourceListEditor.Unsupported("此工程不使用受支持的原生 CMake 或 ESP-IDF 编译列表");
        }
        var identity = await ProjectHash(project, token).ConfigureAwait(false);
        var disk = await files.ReadAsync(project, configuration, token).ConfigureAwait(false);
        var opened = buffers.FirstOrDefault(buffer => buffer.Source.RelativePath.Equals(configuration, StringComparison.OrdinalIgnoreCase));
        if (disk.IsReadOnly || opened?.Source.IsReadOnly == true)
        {
            throw new StudioXException("SOURCE_READ_ONLY", "构建文件为只读；未修改工程。");
        }
        if (opened is not null && opened.Source.DiskHash != disk.DiskHash)
        {
            throw Stale();
        }
        var snapshot = opened ?? new(disk, disk.Text);
        var idf = manifest.Espressif?.Framework == "esp-idf";
        var parser = new SourceListEditor(snapshot.Text, idf);
        var targets = parser.Describe(value => ResolveSource(project, configuration, value));
        return new(project, identity, snapshot, opened is not null, idf, targets);
    }

    public IReadOnlyList<SourceRegistrationOperation> Suggest(SourceRegistrationContext context, string target, IReadOnlyList<string> discovered, IReadOnlyList<ProjectFileChange> moves)
    {
        var registered = context.Targets.Single(item => item.Name == target).Sources;
        var configDirectory = ProjectFileService.ParentDirectory(context.Configuration.Source.RelativePath);
        var result = new List<SourceRegistrationOperation>();
        var already = new HashSet<string>(registered, StringComparer.OrdinalIgnoreCase);
        foreach (var path in registered.Where(IsSource).Where(UserPath))
        {
            var next = path;
            foreach (var move in moves.Where(move => move.Kind == ProjectFileChangeKind.Renamed && move.PreviousPath is not null))
            {
                if (ProjectFileService.ContainsPath(move.PreviousPath!, next) &&
                    (!ProjectFileService.ContainsPath(move.Path, next) || move.PreviousPath!.Equals(move.Path, StringComparison.OrdinalIgnoreCase) && !move.PreviousPath.Equals(move.Path, StringComparison.Ordinal)))
                {
                    next = move.Path + next[move.PreviousPath!.Length..];
                }
            }
            if (next != path && (!File.Exists(PathBoundary.Resolve(context.ProjectDirectory, path)) || next.Equals(path, StringComparison.OrdinalIgnoreCase)) &&
                IsSource(next) && UserPath(next) && File.Exists(PathBoundary.Resolve(context.ProjectDirectory, next)) &&
                (!context.EspIdf || ProjectFileService.ContainsPath(configDirectory, next) && !HasNestedConfiguration(context.ProjectDirectory, configDirectory, next)) &&
                (!already.Contains(next) || next.Equals(path, StringComparison.OrdinalIgnoreCase)))
            {
                result.Add(new(SourceRegistrationKind.Rename, path, next));
                already.Add(next);
            }
            else if (!File.Exists(PathBoundary.Resolve(context.ProjectDirectory, path)))
            {
                result.Add(new(SourceRegistrationKind.Remove, path));
            }
        }
        foreach (var path in discovered.Where(IsSource).Where(UserPath).Where(path => !already.Contains(path)))
        {
            // IDF 只向当前组件建议源码；子目录内其它组件的文件不能被当作本组件的候选。
            if (context.EspIdf && (!ProjectFileService.ContainsPath(configDirectory, path) ||
                HasNestedConfiguration(context.ProjectDirectory, configDirectory, path)))
            {
                continue;
            }
            result.Add(new(SourceRegistrationKind.Add, path));
        }
        return result.Distinct().OrderBy(operation => operation.Kind == SourceRegistrationKind.Add).ThenBy(operation => operation.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public SourceRegistrationPlan Prepare(SourceRegistrationContext context, string target, IReadOnlyList<SourceRegistrationOperation> operations)
    {
        if (operations.Count is < 1 or > 1000)
        {
            throw new StudioXException("SOURCE_SELECTION", "请选择 1–1,000 项源码登记操作。");
        }
        foreach (var operation in operations)
        {
            ValidateOperation(context, operation);
        }
        var configuration = context.Configuration.Source.RelativePath;
        var parser = new SourceListEditor(context.Configuration.Text, context.EspIdf);
        var after = parser.Change(target, operations, value => ResolveSource(context.ProjectDirectory, configuration, value),
            value => Path.GetRelativePath(Path.GetDirectoryName(PathBoundary.Resolve(context.ProjectDirectory, configuration))!, PathBoundary.Resolve(context.ProjectDirectory, value)).Replace('\\', '/'));
        if (context.Configuration.Source.Encoding.GetByteCount(after) + context.Configuration.Source.Encoding.GetPreamble().Length > 4 * 1024 * 1024)
        {
            throw new StudioXException("SOURCE_SIZE", "登记后的构建文件超过编辑器读取范围。");
        }
        var before = context.Configuration.Text;
        var start = 0;
        var suffix = 0;
        while (start < before.Length && start < after.Length && before[start] == after[start])
        {
            start++;
        }
        while (suffix < before.Length - start && suffix < after.Length - start && before[^(suffix + 1)] == after[^(suffix + 1)])
        {
            suffix++;
        }
        var change = new WorkspaceFileChange(context.Configuration.Source, before, after,
            before == after ? [] : [new(start, before.Length - start - suffix, after.Substring(start, after.Length - start - suffix))], context.WasOpen);
        return new(context, target, operations.ToArray(), change);
    }

    public async Task ValidateAsync(SourceRegistrationPlan plan, IReadOnlyList<WorkspaceBufferSnapshot> buffers, CancellationToken token = default)
    {
        if (await ProjectHash(plan.Context.ProjectDirectory, token).ConfigureAwait(false) != plan.Context.ProjectHash)
        {
            throw Stale();
        }
        foreach (var operation in plan.Operations)
        {
            token.ThrowIfCancellationRequested();
            ValidateOperation(plan.Context, operation);
        }
        await new WorkspaceEditService(files).ValidateAsync(plan.Context.ProjectDirectory, [plan.Change], buffers, token).ConfigureAwait(false);
        // 对公开计划重新生成同一文本，不能通过伪造 Change 绕过目标、路径或语法检查。
        var expected = Prepare(plan.Context, plan.Target, plan.Operations).Change;
        if (expected.Source != plan.Change.Source || expected.Before != plan.Change.Before || expected.After != plan.Change.After ||
            expected.WasOpen != plan.Change.WasOpen || !expected.Matches.SequenceEqual(plan.Change.Matches))
        {
            throw Stale();
        }
    }

    private static void ValidateOperation(SourceRegistrationContext context, SourceRegistrationOperation operation)
    {
        var old = CheckPath(context.ProjectDirectory, operation.Path);
        if (old != operation.Path || !IsSource(old))
        {
            throw new StudioXException("SOURCE_PATH", "请选择规范相对路径的 C/C++ 或 GCC 汇编源文件。");
        }
        var next = operation.Kind == SourceRegistrationKind.Rename ? operation.NewPath : operation.Kind == SourceRegistrationKind.Add ? operation.Path : null;
        if (operation.Kind != SourceRegistrationKind.Rename && operation.NewPath is not null)
        {
            throw SourceListEditor.Unsupported("只有改名操作可以指定新路径");
        }
        if (operation.Kind == SourceRegistrationKind.Rename && string.IsNullOrEmpty(next))
        {
            throw Stale();
        }
        if (next is not null)
        {
            if (CheckPath(context.ProjectDirectory, next) != next || !IsSource(next) || !File.Exists(PathBoundary.Resolve(context.ProjectDirectory, next)))
            {
                throw Stale();
            }
            if (context.EspIdf)
            {
                var directory = ProjectFileService.ParentDirectory(context.Configuration.Source.RelativePath);
                if (!ProjectFileService.ContainsPath(directory, next) || HasNestedConfiguration(context.ProjectDirectory, directory, next))
                {
                    throw SourceListEditor.Unsupported("新路径不属于当前 ESP-IDF 组件，请分别核对两个组件");
                }
            }
            if (operation.Kind == SourceRegistrationKind.Rename && !old.Equals(next, StringComparison.OrdinalIgnoreCase) && File.Exists(PathBoundary.Resolve(context.ProjectDirectory, old)))
            {
                throw SourceListEditor.Unsupported("原路径仍然存在，不能将复制猜测为改名");
            }
        }
    }
    private static bool HasNestedConfiguration(string project, string component, string path)
    {
        for (var directory = ProjectFileService.ParentDirectory(path); directory != component && directory.Length > 0; directory = ProjectFileService.ParentDirectory(directory))
        {
            if (File.Exists(PathBoundary.Resolve(project, directory + "/CMakeLists.txt")))
            {
                return true;
            }
        }
        return false;
    }
    private static string ResolveSource(string project, string configuration, string value)
    {
        if (Path.IsPathRooted(value) || value.IndexOfAny(['$', '\\', '"', '*', '?']) >= 0)
        {
            throw SourceListEditor.Unsupported("登记路径必须是明确的项目内相对路径");
        }
        var full = Path.GetFullPath(value, Path.GetDirectoryName(PathBoundary.Resolve(project, configuration))!);
        var relative = Path.GetRelativePath(project, full).Replace('\\', '/');
        _ = PathBoundary.Resolve(project, relative);
        return relative;
    }
    private static string CheckPath(string project, string relative)
    {
        if (!UserPath(relative) || relative.IndexOfAny(['$', ';', '\\', '"', '*', '?', '[', ']']) >= 0)
        {
            throw new StudioXException("SOURCE_PATH", "构建登记只操作用户源码和构建文件；路径不能包含变量、列表、链接或生成目录。");
        }
        return Path.GetRelativePath(project, PathBoundary.Resolve(project, relative)).Replace('\\', '/');
    }
    private static bool UserPath(string relative) => relative.Length > 0 && ProjectChangePolicy.IsDiscoverable(relative) &&
        !relative.Split('/').Any(part => part.Equals("device", StringComparison.OrdinalIgnoreCase) || part.Equals("managed_components", StringComparison.OrdinalIgnoreCase));
    private static async Task<string> ProjectHash(string project, CancellationToken token)
    {
        var path = PathBoundary.Resolve(project, ".studiox/project.json");
        if (new FileInfo(path).Length > 1024 * 1024)
        {
            throw Stale();
        }
        return Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path, token).ConfigureAwait(false)));
    }
    private static StudioXException Stale() => new("SOURCE_REGISTRATION_STALE", "工程身份、源码路径或构建文件在预览后变化，请重新读取编译列表；未修改工程。");
}
