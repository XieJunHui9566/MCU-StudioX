namespace StudioX.Application.Skills;

using System.Text.Json;
using StudioX.Foundation;

/// <summary>保存工程技能的显式启用状态；用户级技能始终可发现。</summary>
public sealed class AiSkillService
{
    private const int MaxSettingsBytes = 128 * 1024;
    private const int MaxTrustedProjects = 256;
    private readonly string settingsPath;
    private readonly string? bundledSkillsRoot;
    private readonly object gate = new();

    public AiSkillService(string dataDirectory, string? runtimeDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
            throw new ArgumentException("用户数据目录不能为空。", nameof(dataDirectory));
        settingsPath = Path.Combine(Path.GetFullPath(dataDirectory), "ai-skills.json");
        bundledSkillsRoot = runtimeDirectory is null ? null :
            Path.Combine(Path.GetFullPath(runtimeDirectory), "skills");
    }

    public bool IsProjectEnabled(string project)
    {
        var full = NormalizeProject(project);
        lock (gate) return ReadSettings().TrustedProjects.Contains(full, StringComparer.OrdinalIgnoreCase);
    }

    public void SetProjectEnabled(string project, bool enabled)
    {
        var full = NormalizeProject(project);
        lock (gate)
        {
            var projects = ReadSettings().TrustedProjects.ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (enabled) projects.Add(full);
            else projects.Remove(full);
            if (projects.Count > MaxTrustedProjects)
                throw new StudioXException("SKILL_SETTINGS", "已启用工程技能的工程数量达到上限。");
            var settings = new AiSkillSettings(1, projects.Order(StringComparer.OrdinalIgnoreCase).ToArray());
            WriteSettings(settings);
        }
    }

    public AgentSkillDiscovery Discover(string project) => CreateCatalog(project).Discover();

    public AgentSkillContent ReadSkill(string project, string name) => CreateCatalog(project).ReadSkill(name);

    public AgentSkillResource ReadReference(string project, string name, string relativePath) =>
        CreateCatalog(project).ReadReference(name, relativePath);

    private AgentSkillCatalog CreateCatalog(string project) =>
        new(NormalizeProject(project), includeProjectSkills: IsProjectEnabled(project),
            bundledSkillsRoot: bundledSkillsRoot);

    private AiSkillSettings ReadSettings()
    {
        if (!File.Exists(settingsPath)) return new(1, []);
        var info = new FileInfo(settingsPath);
        if (info.Length > MaxSettingsBytes)
            throw new StudioXException("SKILL_SETTINGS", "AI 技能设置文件过大。");
        try
        {
            var settings = JsonSerializer.Deserialize<AiSkillSettings>(File.ReadAllText(settingsPath), JsonStore.Options)
                ?? throw new StudioXException("SKILL_SETTINGS", "AI 技能设置文件为空。");
            if (settings.FormatVersion != 1 || settings.TrustedProjects is null ||
                settings.TrustedProjects.Length > MaxTrustedProjects ||
                settings.TrustedProjects.Any(path => string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)))
                throw new StudioXException("SKILL_SETTINGS", "AI 技能设置文件格式无效。");
            return settings;
        }
        catch (JsonException ex)
        {
            throw new StudioXException("SKILL_SETTINGS", "AI 技能设置文件不是有效 JSON：" + ex.Message);
        }
    }

    private void WriteSettings(AiSkillSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        var temporary = settingsPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, JsonStore.Options));
            File.Move(temporary, settingsPath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string NormalizeProject(string project)
    {
        if (string.IsNullOrWhiteSpace(project) || !Path.IsPathFullyQualified(project))
            throw new StudioXException("SKILL_PROJECT", "技能设置需要绝对路径的工程目录。");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(project));
    }

    private sealed record AiSkillSettings(int FormatVersion, string[] TrustedProjects);
}
