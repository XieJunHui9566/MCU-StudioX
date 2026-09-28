namespace StudioX.Desktop;

using System.Text.Json;
using StudioX.Application;

/// <summary>仅按已执行的工具与操作判断是否复查磁盘，不把读取或 Git 查询误当作文件写入。</summary>
internal static class AiWorkspaceChangePolicy
{
    public static bool MayChangeWorkspace(AiToolCall call)
    {
        if (call.Name is "project_edit_file" or "project_patch_file" or "project_create_file" or
            "project_create_directory" or "external_project_copy" or "ag32_pin_mapping_enable" or "ag32_pin_plan_apply")
        {
            return true;
        }
        if (call.Name is not ("git_branch" or "git_remote"))
        {
            return false;
        }
        try
        {
            using var arguments = JsonDocument.Parse(call.ArgumentsJson);
            if (!arguments.RootElement.TryGetProperty("action", out var action) ||
                action.ValueKind != JsonValueKind.String)
            {
                return false;
            }
            return call.Name == "git_branch" ? action.GetString() is "switch" or "merge"
                : action.GetString() == "pull";
        }
        catch (JsonException) { return false; }
    }

}
