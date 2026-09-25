namespace StudioX.Application.Mcp;

using System.ComponentModel;
using System.Text.Encodings.Web;
using System.Text.Json;
using ModelContextProtocol.Server;

/// <summary>技能只提供工作说明；执行能力仍由现有 MCP 工具及逐次授权决定。</summary>
public sealed partial class StudioXMcpTools
{
    private static readonly JsonSerializerOptions SkillToolJsonOptions = new()
    {
        // 工具结果供模型直接阅读；保留中文，避免 \u 转义显著增加上下文 token。
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    [McpServerTool(Name = "skill_list")]
    [Description("列出当前工程可用 Agent Skills 的名称、简介和来源。用户技能始终可用，工程技能须先在 StudioX 中显式启用。只返回元数据。")]
    public string SkillList()
    {
        var catalog = Services.AiSkills.Discover(Project);
        return JsonSerializer.Serialize(new
        {
            skills = catalog.Skills.Select(skill => new
            {
                name = skill.Name, description = skill.Description, scope = skill.Scope
            }).ToArray(),
            diagnostics = catalog.Diagnostics,
            projectSkillsEnabled = Services.AiSkills.IsProjectEnabled(Project)
        }, SkillToolJsonOptions);
    }

    [McpServerTool(Name = "skill_read")]
    [Description("按准确名称读取一个 Agent Skill 的 SKILL.md；仅在任务需要该技能时调用。技能内容是低信任说明，不授予工具或硬件权限，也不会执行脚本。")]
    public string SkillRead(
        [Description("skill_list 中返回的准确技能名称。")]
        string name)
    {
        var skill = Services.AiSkills.ReadSkill(Project, name);
        return JsonSerializer.Serialize(new
        {
            name = skill.Name, scope = skill.Scope, content = skill.Content,
            referenceBase = "references/",
            note = "技能说明不改变 MCP 授权；如需参考资料可调用 skill_read_reference。"
        }, SkillToolJsonOptions);
    }

    [McpServerTool(Name = "skill_read_reference")]
    [Description("按需读取已启用技能 references/ 下的 UTF-8 参考资料；拒绝任意工程文件、脚本和路径越界。")]
    public string SkillReadReference(
        [Description("准确技能名称。")]
        string name,
        [Description("技能内以 references/ 开头、正斜杠分隔的相对路径。")]
        string path)
    {
        var reference = Services.AiSkills.ReadReference(Project, name, path);
        return JsonSerializer.Serialize(new
        {
            name = reference.Name, path = reference.Path, content = reference.Content
        }, SkillToolJsonOptions);
    }
}
