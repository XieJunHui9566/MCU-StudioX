namespace StudioX.Application.PeripheralDevelopment;

using StudioX.Application.Editing;
using StudioX.Foundation;

public sealed partial class PeripheralDevelopmentService
{
    private readonly ProjectFileService files = new();

    public async Task<PeripheralComponentContext> ReadComponentAsync(PeripheralDevelopmentContext context, string sourcePath,
        IReadOnlyList<WorkspaceBufferSnapshot> buffers, CancellationToken token = default)
    {
        if (context.Project.Espressif?.Framework != "esp-idf" || !Path.GetExtension(sourcePath).Equals(".c", StringComparison.OrdinalIgnoreCase))
        {
            throw new StudioXException("PERIPHERAL_COMPONENT", "请打开此 ESP-IDF 工程内已参与编译的 C 源文件，再添加外设。");
        }
        CheckUserPath(context.ProjectDirectory, sourcePath);
        var source = await Read(sourcePath);
        var directory = Path.GetDirectoryName(sourcePath)?.Replace('\\', '/') ?? "";
        while (directory.Length > 0)
        {
            var relative = directory + "/CMakeLists.txt";
            CheckUserPath(context.ProjectDirectory, relative);
            if (File.Exists(PathBoundary.Resolve(context.ProjectDirectory, relative)))
            {
                var cmake = await Read(relative);
                // 最近的注册入口才可归属当前源码；不能跳过不认识的入口继续猜测上层组件。
                _ = EspIdfComponentDependencies.Prepare(cmake.Text, Path.GetRelativePath(directory, sourcePath).Replace('\\', '/'), []);
                return new(source, cmake, IsOpen(sourcePath), IsOpen(relative));
            }
            directory = Path.GetDirectoryName(directory)?.Replace('\\', '/') ?? "";
        }
        throw new StudioXException("PERIPHERAL_COMPONENT", "未找到当前源码的 ESP-IDF 组件入口。请打开 main、src 或用户组件内已登记的 C 源文件；未修改工程。");

        bool IsOpen(string relative) => buffers.Any(buffer => buffer.Source.RelativePath.Equals(relative, StringComparison.OrdinalIgnoreCase));
        async Task<WorkspaceBufferSnapshot> Read(string relative)
        {
            var disk = await files.ReadAsync(context.ProjectDirectory, relative, token).ConfigureAwait(false);
            var open = buffers.FirstOrDefault(buffer => buffer.Source.RelativePath.Equals(relative, StringComparison.OrdinalIgnoreCase));
            if (disk.IsReadOnly || open?.Source.IsReadOnly == true)
            {
                throw new StudioXException("PERIPHERAL_READ_ONLY", relative + " 为只读，请解除只读或选择可编辑组件；未修改工程。");
            }
            if (open is not null && open.Source.DiskHash != disk.DiskHash)
            {
                throw new StudioXException("PERIPHERAL_ADDITION_STALE", relative + " 的磁盘内容已变化，请重新读取后再添加外设。");
            }
            return open ?? new(disk, disk.Text);
        }
    }

    public PeripheralProjectAddition PrepareAddition(PeripheralDevelopmentContext context, PeripheralComponentContext component, PeripheralCodePreview preview)
    {
        CheckUserPath(context.ProjectDirectory, component.Source.Source.RelativePath);
        CheckUserPath(context.ProjectDirectory, component.CMake.Source.RelativePath);
        var directory = Path.GetDirectoryName(component.CMake.Source.RelativePath)!;
        var relative = Path.GetRelativePath(directory, component.Source.Source.RelativePath).Replace('\\', '/');
        var (after, added) = EspIdfComponentDependencies.Prepare(component.CMake.Text, relative, preview.RequiredComponents);
        var code = PrepareInsertion(preview, component.Source.Text);
        return new(context, [Change(component.Source, code + component.Source.Text, component.SourceWasOpen),
            Change(component.CMake, after, component.CMakeWasOpen)], added);

        static WorkspaceFileChange Change(WorkspaceBufferSnapshot before, string after, bool opened)
        {
            if (before.Source.Encoding.GetByteCount(after) + before.Source.Encoding.GetPreamble().Length > 4 * 1024 * 1024)
            {
                throw new StudioXException("PERIPHERAL_ADDITION_SIZE", "添加后文件将超过编辑器支持的 4 MiB，请拆分源码后再添加；未修改工程。");
            }
            var start = 0;
            while (start < before.Text.Length && start < after.Length && before.Text[start] == after[start])
            {
                start++;
            }
            var suffix = 0;
            while (suffix < before.Text.Length - start && suffix < after.Length - start && before.Text[^(suffix + 1)] == after[^(suffix + 1)])
            {
                suffix++;
            }
            return new(before.Source, before.Text, after,
                before.Text == after ? [] : [new(start, before.Text.Length - start - suffix, after.Substring(start, after.Length - start - suffix))], opened);
        }
    }

    public async Task ValidateAdditionAsync(PeripheralProjectAddition addition, IReadOnlyList<WorkspaceBufferSnapshot> buffers,
        CancellationToken token = default)
    {
        await ValidateAsync(addition.Context, token).ConfigureAwait(false);
        foreach (var file in addition.Files)
        {
            CheckUserPath(addition.Context.ProjectDirectory, file.Path);
            var disk = await files.ReadAsync(addition.Context.ProjectDirectory, file.Path, token).ConfigureAwait(false);
            var open = buffers.FirstOrDefault(buffer => buffer.Source.RelativePath.Equals(file.Path, StringComparison.OrdinalIgnoreCase));
            if (disk.IsReadOnly || open?.Source.IsReadOnly == true || disk.DiskHash != file.Source.DiskHash ||
                (open?.Text ?? disk.Text) != file.Before || (file.WasOpen && open is null))
            {
                throw new StudioXException("PERIPHERAL_ADDITION_STALE", file.Path + " 在预览后已变化、关闭或变为只读。请重新打开外设辅助；源码和依赖均未修改。");
            }
        }
    }

    private static void CheckUserPath(string project, string relative)
    {
        var path = PathBoundary.Resolve(project, relative);
        var normalized = Path.GetRelativePath(project, path).Replace('\\', '/');
        string[] excluded = [".studiox", ".git", ".build", "build", "device", "managed_components"];
        if (normalized.Split('/').Any(part => excluded.Contains(part, StringComparer.OrdinalIgnoreCase)))
        {
            throw new StudioXException("PERIPHERAL_COMPONENT", "请选择用户源码组件；器件资源、托管下载组件和构建目录不可通过外设辅助修改。");
        }
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new StudioXException("PERIPHERAL_COMPONENT", "外设自动添加不修改符号链接或联接中的文件，请打开工程内的实际用户源码。");
            }
            if (current.Equals(Path.GetFullPath(project).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
        }
    }
}
