namespace StudioX.PluginRuntimeValidation;

using System.Text.Json;
using StudioX.Extensions.Abstractions;

/// <summary>验证欢迎页会话状态、在途命令及未授权工程工具拒绝。</summary>
public sealed class ApplicationValidationPlugin : IStudioXPlugin
{
    private IPluginHost? host;
    private int count;
    public PluginContribution Describe() => new(
        [new("increment", "累计", "tools", "Ctrl+Alt+Shift+U"), new("delay", "延迟累计"), new("forbidden", "请求工程工具"), new("link", "工程入口")],
        [Panel(), new("navigation", "工程入口", [])], []);
    private PluginPanelDefinition Panel() => new("status", "应用会话验收",
        [new("count", "text", "Count", JsonSerializer.SerializeToElement(count.ToString(System.Globalization.CultureInfo.InvariantCulture))),
         new("name", "input", "保留输入", JsonSerializer.SerializeToElement("initial"))]);
    public async Task ActivateAsync(IPluginHost pluginHost, CancellationToken token)
    {
        host = pluginHost;
        await host.PublishPanelAsync(Panel(), token);
    }
    public async Task<JsonElement> InvokeAsync(string kind, string id, JsonElement arguments, CancellationToken token)
    {
        if (id == "link")
        {
            await host!.PublishPanelAsync(new("navigation", "工程入口",
                [new("project", "projectLink", "打开工程", arguments.Clone())]), token);
            return JsonSerializer.SerializeToElement(new
            {
                linked = true
            });
        }
        if (id == "forbidden")
        {
            return await host!.CallAsync("project_info", arguments, token);
        }
        if (id == "delay")
        {
            await Task.Delay(1500, token);
        }
        count++;
        await host!.PublishPanelAsync(Panel(), token);
        return JsonSerializer.SerializeToElement(new
        {
            count
        });
    }
    public Task DeactivateAsync(CancellationToken token)
    {
        host = null;
        return Task.CompletedTask;
    }
}
