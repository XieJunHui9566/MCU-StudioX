namespace StudioX.Application;

using StudioX.Engine;
using StudioX.Packages;

/// <summary>新建页面只展示已选择模板的需求，不查找工具、不扫描 SDK。</summary>
public static class ProjectComponentSummary
{
    public static string ForSelection(InstalledPack pack, string deviceId, string templateId, bool enableAg32Logic)
    {
        var plan = ProjectService.Plan(pack, deviceId, templateId, "component-preview", enableAg32Logic);
        var needs = ProjectDevelopmentComponents.FromSnapshot(plan.Project);
        return needs.Count == 0 ? "此模板不需要原生开发环境组件。"
            : "所需开发环境组件\n" + string.Join("\n", needs.Select(item => $"{item.Id} {item.Version} · {item.Purpose}"))
                + "\n创建后可在“准备工程开发环境组件”导入对应 .mcutoolchain；缺少组件也可以先创建和编辑工程。";
    }
}
