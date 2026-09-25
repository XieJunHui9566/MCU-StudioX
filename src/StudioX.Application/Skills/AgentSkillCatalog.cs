namespace StudioX.Application.Skills;

using System.Text;
using System.Text.Json;
using StudioX.Foundation;

public sealed record AgentSkillMetadata(string Name, string Description, string Scope);

public sealed record AgentSkillDiscovery(
    IReadOnlyList<AgentSkillMetadata> Skills,
    IReadOnlyList<string> Diagnostics);

public sealed record AgentSkillContent(string Name, string Scope, string Content);

public sealed record AgentSkillResource(string Name, string Path, string Content);

/// <summary>
/// 只发现和读取 Agent Skills；技能文本不授予工具权限，也不会执行脚本。
/// 每次读取重新验证目录及文件，避免缓存了已经被替换的技能路径。
/// </summary>
public sealed class AgentSkillCatalog
{
    private const int MaxDirectoriesPerScope = 128;
    private const int MaxSkillBytes = 64 * 1024;
    private const int MaxFrontmatterChars = 16 * 1024;
    private const int MaxReferenceBytes = 128 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly string projectSkillsRoot;
    private readonly string userSkillsRoot;
    private readonly string? bundledSkillsRoot;
    private readonly bool includeProjectSkills;

    public AgentSkillCatalog(string projectPath, string? userSkillsRoot = null,
        string? projectSkillsRoot = null, bool includeProjectSkills = false,
        string? bundledSkillsRoot = null)
    {
        if (string.IsNullOrWhiteSpace(projectPath) || !Path.IsPathFullyQualified(projectPath))
            throw new StudioXException("SKILL_PROJECT", "技能目录需要绑定绝对路径的工程。");

        var project = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectPath));
        var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(userHome) && userSkillsRoot is null)
            throw new StudioXException("SKILL_HOME", "无法找到当前用户的主目录。");

        this.projectSkillsRoot = projectSkillsRoot is null
            ? Path.Combine(project, ".agents", "skills")
            : RequireAbsoluteRoot(projectSkillsRoot);
        this.userSkillsRoot = userSkillsRoot is null
            ? Path.Combine(userHome, ".agents", "skills")
            : RequireAbsoluteRoot(userSkillsRoot);
        this.bundledSkillsRoot = bundledSkillsRoot is null ? null : RequireAbsoluteRoot(bundledSkillsRoot);
        this.includeProjectSkills = includeProjectSkills;
    }

    /// <summary>只返回名称和简介；完整 SKILL.md 在模型选择技能后才读取。</summary>
    public AgentSkillDiscovery Discover()
    {
        var diagnostics = new List<string>();
        var entries = DiscoverEntries(diagnostics);
        return new(entries.Values
            .OrderBy(entry => entry.Metadata.Name, StringComparer.Ordinal)
            .Select(entry => entry.Metadata)
            .ToArray(), diagnostics.ToArray());
    }

    public AgentSkillContent ReadSkill(string name)
    {
        var entry = FindEntry(name);
        var path = SafeSkillFile(entry.Directory);
        var content = ReadUtf8(path, MaxSkillBytes);
        var metadata = ParseMetadata(content, entry.Directory);
        if (metadata.Name != name)
            throw new StudioXException("SKILL_CHANGED", "技能元数据已变化，请重新发现技能。");
        return new(name, entry.Metadata.Scope, content);
    }

    /// <summary>只允许读取 references 下的 UTF-8 文本，不暴露脚本和任意工程文件。</summary>
    public AgentSkillResource ReadReference(string name, string relativePath)
    {
        var entry = FindEntry(name);
        if (string.IsNullOrWhiteSpace(relativePath) ||
            !relativePath.StartsWith("references/", StringComparison.Ordinal) ||
            relativePath.Length <= "references/".Length)
            throw new StudioXException("SKILL_REFERENCE", "技能资源路径必须位于 references/ 目录。");

        EnsureNoReparse(entry.Directory);
        var path = PathBoundary.Resolve(entry.Directory, relativePath);
        if (!File.Exists(path))
            throw new StudioXException("SKILL_REFERENCE_MISSING", "技能引用文件不存在。");
        var content = ReadUtf8(path, MaxReferenceBytes);
        return new(name, relativePath, content);
    }

    private SkillEntry FindEntry(string name)
    {
        if (!ValidName(name))
            throw new StudioXException("SKILL_NAME", "技能名称不符合 Agent Skills 规范。");
        var entries = DiscoverEntries(new List<string>());
        if (!entries.TryGetValue(name, out var entry))
            throw new StudioXException("SKILL_MISSING", $"未发现技能：{name}");
        return entry;
    }

    private Dictionary<string, SkillEntry> DiscoverEntries(List<string> diagnostics)
    {
        var entries = new Dictionary<string, SkillEntry>(StringComparer.Ordinal);
        // 随程序发布的技能是默认值；用户技能与已启用的工程技能可按名称覆盖它们。
        if (bundledSkillsRoot is not null) ReadScope(bundledSkillsRoot, "bundled", entries, diagnostics);
        ReadScope(userSkillsRoot, "user", entries, diagnostics);
        // 同名项目技能覆盖用户技能，符合 Agent Skills 的工程级优先规则。
        // 工程内文件可来自不可信仓库；只有宿主显式信任后才纳入发现和读取。
        if (includeProjectSkills) ReadScope(projectSkillsRoot, "project", entries, diagnostics);
        return entries;
    }

    private static void ReadScope(string root, string scope,
        Dictionary<string, SkillEntry> entries, List<string> diagnostics)
    {
        try
        {
            if (!Directory.Exists(root)) return;
            EnsureNoReparse(root);
            var directories = Directory.EnumerateDirectories(root)
                .Take(MaxDirectoriesPerScope + 1).ToArray();
            if (directories.Length > MaxDirectoriesPerScope)
            {
                diagnostics.Add($"{scope} 技能目录超过 {MaxDirectoriesPerScope} 个，此范围未加载。");
                return;
            }
            foreach (var directory in directories.OrderBy(Path.GetFileName, StringComparer.Ordinal))
            {
                var folderName = Path.GetFileName(directory);
                if (!ValidName(folderName))
                {
                    diagnostics.Add($"跳过名称不合规的 {scope} 技能目录：{folderName}");
                    continue;
                }
                try
                {
                    EnsureNoReparse(directory);
                    var manifest = SafeSkillFile(directory);
                    if (!File.Exists(manifest)) continue;
                    var content = ReadUtf8(manifest, MaxSkillBytes);
                    var metadata = ParseMetadata(content, directory);
                    entries[metadata.Name] = new(new(metadata.Name, metadata.Description, scope), directory);
                }
                catch (Exception error) when (IsSkillReadError(error))
                {
                    diagnostics.Add($"跳过 {scope} 技能 {folderName}：{error.Message}");
                }
            }
        }
        catch (Exception error) when (IsSkillReadError(error))
        {
            diagnostics.Add($"无法读取 {scope} 技能目录：{error.Message}");
        }
    }

    private static AgentSkillMetadata ParseMetadata(string content, string directory)
    {
        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n').Split('\n');
        if (lines.Length < 3 || lines[0] != "---")
            throw new StudioXException("SKILL_FRONTMATTER", "SKILL.md 必须以 YAML frontmatter 开始。");

        var closing = -1;
        var characters = 4;
        for (var index = 1; index < lines.Length; index++)
        {
            characters += lines[index].Length + 1;
            if (characters > MaxFrontmatterChars)
                throw new StudioXException("SKILL_FRONTMATTER", "技能 frontmatter 超过长度限制。");
            if (lines[index] == "---")
            {
                closing = index;
                break;
            }
        }
        if (closing < 0)
            throw new StudioXException("SKILL_FRONTMATTER", "技能 frontmatter 没有结束标记。");

        string? name = null;
        string? description = null;
        for (var index = 1; index < closing; index++)
        {
            var line = lines[index];
            if (line.Length == 0 || char.IsWhiteSpace(line[0]) || line.StartsWith('#')) continue;
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var key = line[..colon].Trim();
            if (key is not ("name" or "description")) continue;
            var scalar = line[(colon + 1)..].Trim();
            string value;
            if (scalar is "|" or "|-" or "|+" or ">" or ">-" or ">+")
            {
                value = ReadBlockScalar(lines, ref index, closing, scalar[0] == '>');
            }
            else
            {
                value = ReadScalar(scalar);
            }
            if (key == "name")
            {
                if (name is not null)
                    throw new StudioXException("SKILL_FRONTMATTER", "技能名称重复定义。");
                name = value;
            }
            else
            {
                if (description is not null)
                    throw new StudioXException("SKILL_FRONTMATTER", "技能简介重复定义。");
                description = value;
            }
        }

        if (!ValidName(name) || !string.Equals(name, Path.GetFileName(directory), StringComparison.Ordinal))
            throw new StudioXException("SKILL_NAME", "技能名称无效，或与目录名称不一致。");
        if (string.IsNullOrWhiteSpace(description) || description.Length > 1024)
            throw new StudioXException("SKILL_DESCRIPTION", "技能简介必须为 1 至 1024 个字符。");
        return new(name!, description, "");
    }

    private static string ReadBlockScalar(string[] lines, ref int index, int closing, bool folded)
    {
        var start = index + 1;
        var end = start;
        var indent = int.MaxValue;
        while (end < closing)
        {
            var line = lines[end];
            var whitespace = line.Length - line.TrimStart(' ', '\t').Length;
            if (line.Length > whitespace && whitespace == 0) break;
            if (line.Length > whitespace) indent = Math.Min(indent, whitespace);
            end++;
        }
        index = end - 1;
        if (indent == int.MaxValue) return "";
        var values = new List<string>();
        for (var cursor = start; cursor < end; cursor++)
        {
            var line = lines[cursor];
            values.Add(string.IsNullOrWhiteSpace(line) ? "" : line[Math.Min(indent, line.Length)..]);
        }
        while (values.Count > 0 && values[^1].Length == 0) values.RemoveAt(values.Count - 1);
        if (!folded) return string.Join("\n", values);

        var builder = new StringBuilder();
        for (var cursor = 0; cursor < values.Count; cursor++)
        {
            if (cursor > 0) builder.Append(values[cursor - 1].Length == 0 || values[cursor].Length == 0 ? '\n' : ' ');
            builder.Append(values[cursor]);
        }
        return builder.ToString();
    }

    private static string ReadScalar(string scalar)
    {
        if (scalar.StartsWith('"'))
        {
            var escaped = false;
            for (var index = 1; index < scalar.Length; index++)
            {
                if (escaped)
                {
                    escaped = false;
                    continue;
                }
                if (scalar[index] == '\\')
                {
                    escaped = true;
                    continue;
                }
                if (scalar[index] != '"') continue;
                RequireCommentOrEnd(scalar[(index + 1)..]);
                try { return JsonSerializer.Deserialize<string>(scalar[..(index + 1)]) ?? ""; }
                catch (JsonException)
                {
                    throw new StudioXException("SKILL_FRONTMATTER", "技能元数据的双引号字符串格式无效。");
                }
            }
            throw new StudioXException("SKILL_FRONTMATTER", "技能元数据的双引号字符串没有结束标记。");
        }
        if (scalar.StartsWith('\''))
        {
            for (var index = 1; index < scalar.Length; index++)
            {
                if (scalar[index] != '\'') continue;
                if (index + 1 < scalar.Length && scalar[index + 1] == '\'')
                {
                    index++;
                    continue;
                }
                RequireCommentOrEnd(scalar[(index + 1)..]);
                return scalar[1..index].Replace("''", "'", StringComparison.Ordinal);
            }
            throw new StudioXException("SKILL_FRONTMATTER", "技能元数据的单引号字符串没有结束标记。");
        }
        if (scalar.StartsWith('[') || scalar.StartsWith('{') || scalar.StartsWith('!'))
            throw new StudioXException("SKILL_FRONTMATTER", "技能名称和简介必须是 YAML 字符串。");
        var comment = scalar.IndexOf(" #", StringComparison.Ordinal);
        return (comment >= 0 ? scalar[..comment] : scalar).Trim();
    }

    private static void RequireCommentOrEnd(string tail)
    {
        var trimmed = tail.TrimStart();
        if (trimmed.Length > 0 && !trimmed.StartsWith('#'))
            throw new StudioXException("SKILL_FRONTMATTER", "技能元数据的引号字符串后有多余内容。");
    }

    private static string SafeSkillFile(string directory)
    {
        EnsureNoReparse(directory);
        return PathBoundary.Resolve(directory, "SKILL.md");
    }

    private static string ReadUtf8(string path, int limit)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new StudioXException("SKILL_LINK", "技能文件不能是符号链接或重解析点。");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > limit)
            throw new StudioXException("SKILL_SIZE", $"技能文件超过 {limit / 1024} KiB 限制。");
        var bytes = new byte[limit + 1];
        var total = 0;
        int read;
        while (total < bytes.Length && (read = stream.Read(bytes, total, bytes.Length - total)) > 0)
            total += read;
        if (total > limit)
            throw new StudioXException("SKILL_SIZE", $"技能文件超过 {limit / 1024} KiB 限制。");
        try
        {
            var content = StrictUtf8.GetString(bytes, 0, total);
            return content.TrimStart('\uFEFF');
        }
        catch (DecoderFallbackException)
        {
            throw new StudioXException("SKILL_ENCODING", "技能文件必须是 UTF-8 文本。");
        }
    }

    private static void EnsureNoReparse(string path)
    {
        var current = Path.GetFullPath(path);
        while (true)
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new StudioXException("SKILL_LINK", "技能目录不能包含符号链接或重解析点。");
            var parent = Path.GetDirectoryName(current);
            if (parent is null || parent == current) break;
            current = parent;
        }
    }

    private static bool ValidName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 64 || name[0] == '-' || name[^1] == '-')
            return false;
        var previousHyphen = false;
        foreach (var character in name)
        {
            if (character is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-')) return false;
            if (character == '-' && previousHyphen) return false;
            previousHyphen = character == '-';
        }
        return true;
    }

    private static string RequireAbsoluteRoot(string root) =>
        Path.IsPathFullyQualified(root)
            ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(root))
            : throw new StudioXException("SKILL_ROOT", "技能目录必须是绝对路径。");

    private static bool IsSkillReadError(Exception error) =>
        error is IOException or UnauthorizedAccessException or StudioXException or DecoderFallbackException;

    private sealed record SkillEntry(AgentSkillMetadata Metadata, string Directory);
}
