namespace StudioX.Application.Skills;

using StudioX.Foundation;

public sealed partial class AiSkillService
{
    /// <summary>复制已选择的技能目录；有界遍历、拒绝链接、完整校验后原子发布，不覆盖既有技能。</summary>
    public async Task<AgentSkillMetadata> ImportAsync(string selectedPath, CancellationToken token = default)
    {
        var source = Path.GetFullPath(selectedPath);
        if (File.Exists(source))
        {
            if (Path.GetFileName(source) != "SKILL.md")
            {
                throw new StudioXException("SKILL_IMPORT", "请选择 SKILL.md 或包含它的文件夹。");
            }
            source = Path.GetDirectoryName(source)!;
        }
        RejectLinks(source);
        var name = Path.GetFileName(source);
        if (source.Equals(importedSkillsRoot, StringComparison.OrdinalIgnoreCase) ||
            Path.TrimEndingDirectorySeparator(importedSkillsRoot).StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new StudioXException("SKILL_IMPORT", "不能将包含技能保存目录的上级目录导入。");
        }
        Directory.CreateDirectory(importedSkillsRoot);
        RejectLinks(importedSkillsRoot);
        var stage = Path.Combine(importedSkillsRoot, ".import-" + Guid.NewGuid().ToString("N"));
        var stagedSkill = Path.Combine(stage, name);
        try
        {
            var pending = new Stack<(string Path, int Depth)>();
            pending.Push((source, 0));
            var count = 0;
            long bytes = 0;
            while (pending.TryPop(out var directory))
            {
                token.ThrowIfCancellationRequested();
                if (directory.Depth > 8)
                {
                    throw new StudioXException("SKILL_IMPORT", "技能子目录超过 8 层。");
                }
                RejectLinks(directory.Path);
                foreach (var item in Directory.EnumerateFileSystemEntries(directory.Path))
                {
                    if (++count > 256)
                    {
                        throw new StudioXException("SKILL_IMPORT", "技能最多包含 256 个文件或目录。");
                    }
                    RejectLinks(item);
                    if (Directory.Exists(item))
                    {
                        pending.Push((item, directory.Depth + 1));
                        continue;
                    }
                    var size = new FileInfo(item).Length;
                    bytes += size;
                    if (size > 2 * 1024 * 1024 || bytes > 8 * 1024 * 1024)
                    {
                        throw new StudioXException("SKILL_IMPORT", "单文件不能超过 2 MiB，技能总量不能超过 8 MiB。");
                    }
                    var target = Path.Combine(stagedSkill, Path.GetRelativePath(source, item));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    await using var input = new FileStream(item, FileMode.Open, FileAccess.Read, FileShare.Read);
                    await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    await input.CopyToAsync(output, token).ConfigureAwait(false);
                    bytes += output.Length - size;
                    if (output.Length > 2 * 1024 * 1024 || bytes > 8 * 1024 * 1024)
                    {
                        throw new StudioXException("SKILL_IMPORT", "复制期间技能文件超过大小限制。");
                    }
                }
            }
            var discovery = new AgentSkillCatalog(importedSkillsRoot, userSkillsRoot: stage).Discover();
            var metadata = discovery.Skills.SingleOrDefault() ?? throw new StudioXException("SKILL_IMPORT",
                "技能格式无效：" + string.Join("；", discovery.Diagnostics));
            token.ThrowIfCancellationRequested();
            lock (gate)
            {
                var destination = Path.Combine(importedSkillsRoot, metadata.Name);
                if (Directory.Exists(destination))
                {
                    throw new StudioXException("SKILL_EXISTS", "已存在同名导入技能，请修改名称后再导入。");
                }
                Directory.Move(stagedSkill, destination);
            }
            return metadata with
            {
                Scope = "imported"
            };
        }
        finally { if (Directory.Exists(stage)) { Directory.Delete(stage, recursive: true); } }
    }

    private static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new StudioXException("SKILL_LINK", "技能导入不接受符号链接或目录联接。");
            }
        }
    }
}
