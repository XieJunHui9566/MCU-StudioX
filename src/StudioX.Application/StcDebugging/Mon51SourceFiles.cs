namespace StudioX.Application.StcDebugging;

using StudioX.Foundation;

/// <summary>SDCC 可能只记录文件名；只接受工程内唯一来源，排除产物和目录链接。</summary>
internal sealed class Mon51SourceFiles(string project, IReadOnlyList<string>? compiledSources)
{
    private Dictionary<string, List<string>>? names;
    public string Resolve(string file)
    {
        if (Path.IsPathFullyQualified(file) || file.IndexOfAny(['/', '\\']) >= 0)
        {
            var full = Path.GetFullPath(file, project);
            _ = PathBoundary.Resolve(project, Path.GetRelativePath(project, full).Replace('\\', '/'));
            return File.Exists(full) ? full : throw new StudioXException("MON51_CDB", "CDB 来源文件不存在：" + file);
        }
        var compiled = compiledSources?.Where(path => Path.GetFileName(path).Equals(file, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (compiled is { Length: 1 })
        {
            return PathBoundary.Resolve(project, Path.GetRelativePath(project, compiled[0]).Replace('\\', '/'));
        }
        if (compiled is { Length: > 1 })
        {
            throw new StudioXException("MON51_CDB", "实际编译来源包含同名文件，CDB 不能唯一确定映射：" + file);
        }
        if (names is null)
        {
            names = new(StringComparer.OrdinalIgnoreCase);
            var pending = new Stack<string>();
            pending.Push(project);
            var count = 0;
            while (pending.TryPop(out var directory))
            {
                foreach (var item in new DirectoryInfo(directory).EnumerateFileSystemInfos())
                {
                    if (++count > 100000)
                    {
                        throw new StudioXException("MON51_CDB", "源码索引超过 100000 项，请精简工程目录。");
                    }
                    if ((item.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }
                    if (item is DirectoryInfo)
                    {
                        if (item.Name is not (".build" or ".studiox" or ".git" or "build" or "Build" or "Debug" or "Release"))
                        {
                            pending.Push(item.FullName);
                        }
                    }
                    else
                    {
                        if (!names.TryGetValue(item.Name, out var paths))
                        {
                            names[item.Name] = paths = [];
                        }
                        paths.Add(item.FullName);
                    }
                }
            }
        }
        if (!names.TryGetValue(file, out var matches) || matches.Count == 0)
        {
            throw new StudioXException("MON51_CDB", "工程内找不到 CDB 来源文件：" + file);
        }
        if (matches.Count != 1)
        {
            throw new StudioXException("MON51_CDB", "CDB 只记录文件名，工程内存在多个同名来源，不能确定映射：" + file);
        }
        return matches[0];
    }
}
