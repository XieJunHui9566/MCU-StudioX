namespace StudioX.LabPlugins;

using System.Text.Json;
using StudioX.Extensions.Abstractions;

/// <summary>纯离线实验面板；计算结果只留在插件进程，由用户复制到自己的工程。</summary>
public abstract class LabPlugin : IStudioXPlugin
{
    private IPluginHost? host;
    private JsonElement input = LabPanel.Json(new { });
    private LabResult? result;
    private long revision;

    public abstract string Title
    {
        get;
    }
    protected abstract string Introduction
    {
        get;
    }
    protected abstract JsonElement Schema
    {
        get;
    }
    protected abstract PluginPanelWidget[] Inputs(JsonElement values);
    public abstract LabResult Calculate(JsonElement values);

    public virtual PluginContribution Describe() => new(
        [new("open", "打开实验面板", "tools"), new("calculate", "计算 / 生成", "palette")],
        [Panel()], [new("calculate", Introduction + " 纯离线计算；返回结果与可复制数据，不操作文件或设备。省略字段时使用面板示例默认值。", Schema)]);

    public virtual async Task ActivateAsync(IPluginHost pluginHost, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        host = pluginHost;
        result = Calculate(input);
        await host.PublishPanelAsync(Panel(), cancellationToken);
    }

    public virtual async Task<JsonElement> InvokeAsync(string kind, string id, JsonElement arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var active = host ?? throw new InvalidOperationException("插件尚未激活。");
        if (kind == "command" && id == "open")
        {
            await active.PublishPanelAsync(Panel(), cancellationToken);
            return LabPanel.Json(new
            {
                ok = true
            });
        }
        if (kind is not ("command" or "agentTool") || id != "calculate")
        {
            throw new ArgumentException("未知插件命令。");
        }
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("参数必须是对象。");
        }
        var values = arguments.TryGetProperty("values", out var submitted) ? submitted : arguments;
        // 从命令面板调用时没有表单参数，重复计算上次输入，避免意外切回默认示例。
        if (kind == "command" && !arguments.TryGetProperty("values", out _))
        {
            values = input;
        }
        try
        {
            if (values.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException("表单参数必须是对象。");
            }
            var computed = Calculate(values);
            cancellationToken.ThrowIfCancellationRequested();
            input = values.Clone();
            result = computed;
            await active.PublishPanelAsync(Panel(), cancellationToken);
            return LabPanel.Json(new
            {
                ok = true,
                data = computed.Data,
                copyText = computed.CopyText
            });
        }
        catch (ArgumentException error) when (kind == "command")
        {
            await active.PublishPanelAsync(Panel(error.Message), cancellationToken);
            return LabPanel.Json(new
            {
                ok = false,
                error = error.Message
            });
        }
    }

    public virtual Task DeactivateAsync(CancellationToken cancellationToken)
    {
        host = null;
        return Task.CompletedTask;
    }

    private PluginPanelDefinition Panel(string? error = null)
    {
        var widgets = new List<PluginPanelWidget> { LabPanel.Text("intro", "", Introduction) };
        widgets.Add(new("controls", "form", "试一试", Children:
            [.. Inputs(input), new("calculate", "button", "计算 / 生成", CommandId: "calculate")]));
        if (error is not null)
        {
            widgets.Add(LabPanel.Text("error", "输入错误 / Error", error + " 下方保留上次成功结果。"));
        }
        if (result is { } current)
        {
            widgets.AddRange(current.Widgets);
            // 旧宿主保留 input 值；只更换结果字段 ID，使重算更新结果且不打断用户正在输入的参数。
            widgets.Add(LabPanel.Input("output_" + ++revision, "复制结果：点入此框后 Ctrl+A、Ctrl+C（编辑此框不影响计算）", current.CopyText));
        }
        return new("lab", Title, widgets.ToArray());
    }
}
