namespace StudioX.Application;

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using StudioX.Engine;
using StudioX.Foundation;

/// <summary>显式解除托管只修改所有权，先清除旧手动下载频率，避免解除后复活旧参数。</summary>
public sealed class GraphicalClockControlService(ProjectClockService clocks, StcIspService isp, ProjectFileService files)
{
    public async Task ReleaseAsync(string root, CancellationToken token = default)
    {
        if (clocks.Ownership is not null && await clocks.Ownership.RequiresManagedClockAsync(root, token))
        {
            throw new StudioXException("PROJECT_CLOCK_OWNER", "请先停用当前工程的配置插件，再明确解除时钟托管。");
        }
        if (clocks.HasPendingChangesAsync is not null && await clocks.HasPendingChangesAsync(root, token))
        {
            throw new StudioXException("PROJECT_CLOCK_DIRTY", "配置仍有未保存修改，未解除托管。");
        }
        var state = await clocks.ReadAsync(root, token);
        if (!state.Managed)
        {
            return;
        }
        var document = await files.ReadAsync(root, "device.mcuconfig", token);
        var json = JsonNode.Parse(document.Text)!.AsObject();
        var policy = json["configuration"]!["profileData"]!["projectClock"]!.AsObject();
        var transport = await isp.LoadSettingsAsync(root, token);
        // 先在托管状态下保存传输项，下载服务会移除旧的手动时钟；失败时仍保留托管。
        await isp.SaveSettingsAsync(root, transport, token);
        policy["managed"] = false;
        policy["valid"] = false;
        policy["inputSha256"] = null;
        await files.SaveAsync(root, document, json.ToJsonString(new JsonSerializerOptions(JsonStore.Options)
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }) + "\n", token);
    }
}
