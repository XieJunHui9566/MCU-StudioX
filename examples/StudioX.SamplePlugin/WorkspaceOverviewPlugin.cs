namespace StudioX.SamplePlugin;

using System.Diagnostics;
using System.Text.Json;
using StudioX.Extensions.Abstractions;

/// <summary>使用宿主只读工具展示工程和串口快照；不拥有设备连接，不自动执行写入。</summary>
public sealed class WorkspaceOverviewPlugin : IStudioXPlugin
{
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private readonly Stopwatch clock = new();
    private readonly Queue<ReceivedPoint> received = [];
    private IPluginHost? host;
    private JsonElement? formResult;

    public PluginContribution Describe() => new(
        [new("refresh", "刷新工程与串口概览", "tools"), new("echo_form", "显示表单值")],
        [InitialPanel()],
        [new("overview", "读取当前工程、当前插件 MCP 会话串口状态与有界串口日志；不连接、不发送、不下载。",
            JsonSerializer.SerializeToElement(new { type = "object", properties = new { }, additionalProperties = false }))]);

    public async Task ActivateAsync(IPluginHost pluginHost, CancellationToken cancellationToken)
    {
        host = pluginHost;
        clock.Start();
        await host.LogAsync("info", "工程概览插件已激活；串口数据来自该插件宿主的 MCP 会话。", cancellationToken);
        await RefreshAsync(cancellationToken);
    }

    public Task<JsonElement> InvokeAsync(string kind, string id, JsonElement arguments, CancellationToken cancellationToken)
    {
        if ((kind == "command" && id == "refresh") || (kind == "agentTool" && id == "overview"))
        {
            return RefreshAsync(cancellationToken);
        }
        if (kind == "command" && id == "echo_form")
        {
            formResult = arguments.Clone();
            return RefreshAsync(cancellationToken);
        }
        throw new InvalidOperationException("未注册的插件调用：" + kind + "/" + id);
    }

    public Task DeactivateAsync(CancellationToken cancellationToken)
    {
        clock.Stop();
        host = null;
        return Task.CompletedTask;
    }

    private async Task<JsonElement> RefreshAsync(CancellationToken cancellationToken)
    {
        await refreshGate.WaitAsync(cancellationToken);
        try
        {
            var current = host ?? throw new InvalidOperationException("插件尚未激活。");
            var empty = JsonSerializer.SerializeToElement(new { });
            var project = await current.CallAsync("project_info", empty, cancellationToken);
            var serial = await current.CallAsync("serial_status", empty, cancellationToken);
            var log = await current.CallAsync("serial_read",
                JsonSerializer.SerializeToElement(new { previousVersion = -1, maxCharacters = 2000, timestamps = false }),
                cancellationToken);
            if (serial.TryGetProperty("receivedBytes", out var bytes) && bytes.TryGetDouble(out var value) && double.IsFinite(value))
            {
                received.Enqueue(new(clock.Elapsed.TotalSeconds, value));
                while (received.Count > 100)
                {
                    received.Dequeue();
                }
            }
            var rows = project.EnumerateObject().Select(property =>
                new[] { property.Name, property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() ?? "" : property.Value.GetRawText() }).ToArray();
            var display = log.TryGetProperty("displayText", out var text) && text.ValueKind == JsonValueKind.String
                ? text.GetString() ?? "" : "当前插件 MCP 会话尚未建立串口连接。";
            var panel = new PluginPanelDefinition("overview", "工程与串口概览",
            [
                new("source", "text", "数据来源", JsonSerializer.SerializeToElement(
                    "工程来自 project_info；串口状态与日志来自宿主 MCP 会话。曲线 X 为插件激活后的宿主单调秒，Y 为累计接收字节。")),
                new("project", "table", "工程信息", JsonSerializer.SerializeToElement(rows), Columns: ["字段", "值"]),
                new("serial", "text", "串口状态", JsonSerializer.SerializeToElement(serial.GetRawText())),
                new("log", "text", "处理后的终端文本", JsonSerializer.SerializeToElement(display)),
                new("received", "plot", "接收字节 / 宿主单调秒", JsonSerializer.SerializeToElement(received.Select(point => new { x = point.Time, y = point.Bytes }))),
                new("sources", "tree", "只读数据来源", Children:
                [
                    new("project_source", "text", "工程", JsonSerializer.SerializeToElement("project_info")),
                    new("serial_source", "text", "串口", JsonSerializer.SerializeToElement("serial_status / serial_read"))
                ]),
                new("form", "form", "表单参数演示（只在插件进程内回显）", Children:
                [
                    new("note", "input", "备注", JsonSerializer.SerializeToElement("只读观察")),
                    new("count", "number", "显示数量", JsonSerializer.SerializeToElement(10)),
                    new("timestamps", "checkbox", "显示时间戳", JsonSerializer.SerializeToElement(false)),
                    new("mode", "select", "显示模式", JsonSerializer.SerializeToElement(new
                    {
                        options = new[] { new { label = "概览", value = "overview" }, new { label = "日志", value = "log" } },
                        selected = "overview"
                    })),
                    new("echo", "button", "回显参数", CommandId: "echo_form")
                ]),
                new("form_result", "text", "表单回显", JsonSerializer.SerializeToElement(formResult?.GetRawText() ?? "尚未提交表单。")),
                new("refresh", "button", "刷新", CommandId: "refresh")
            ]);
            await current.PublishPanelAsync(panel, cancellationToken);
            return JsonSerializer.SerializeToElement(new { project, serial, log, timeSource = "plugin-host-monotonic" });
        }
        finally
        {
            refreshGate.Release();
        }
    }

    private static PluginPanelDefinition InitialPanel() => new("overview", "工程与串口概览",
    [
        new("message", "text", "状态", JsonSerializer.SerializeToElement("等待宿主只读工具快照。")),
        new("refresh", "button", "刷新", CommandId: "refresh")
    ]);

    private sealed record ReceivedPoint(double Time, double Bytes);
}
