namespace StudioX.SamplePlugin;

using System.Text.Json;
using StudioX.Extensions.Abstractions;

/// <summary>只解释传入的跨平台 JSON 契约，不引用 WPF、调试器实现或特定 MCU。</summary>
internal static class DebugSnapshotPanel
{
    public static PluginPanelDefinition Create(JsonElement arguments, bool details)
    {
        var request = arguments.Deserialize<PluginDebugSnapshotRequest>(new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("缺少调试快照输入。");
        if (request.FormatVersion != 1) { throw new InvalidOperationException("不支持的快照格式。"); }
        var widgets = new List<PluginPanelWidget>
        {
            new("mode", "text", "数据来源", JsonSerializer.SerializeToElement(request.Hardware ? "实机暂停快照" : "离线模拟 · 未连接芯片")),
            new("reason", "text", "暂停原因", JsonSerializer.SerializeToElement(request.Reason))
        };
        if (request.State != "Stopped")
        {
            widgets.Add(new("state", "text", "状态", JsonSerializer.SerializeToElement("当前没有有效暂停快照：" + request.State)));
            return new("snapshot", "通用调试快照", widgets.ToArray());
        }
        widgets.Add(Table("frames", "调用栈", request.Snapshot, "frames", ["层级", "函数", "文件", "行", "地址"], ["level", "function", "file", "line", "address"]));
        widgets.Add(Table("registers", "寄存器", request.Snapshot, "registers", ["名称", "值", "变化"], ["name", "value", "changed"]));
        if (details)
        {
            widgets.Add(Table("locals", "局部变量", request.Snapshot, "locals", ["名称", "值", "类型", "变化"], ["name", "value", "type", "changed"]));
            widgets.Add(Table("watches", "观察项", request.Snapshot, "watches", ["名称", "值", "类型", "变化"], ["name", "value", "type", "changed"]));
        }
        return new("snapshot", "通用调试快照", widgets.ToArray());
    }

    private static PluginPanelWidget Table(string id, string label, JsonElement snapshot, string property, string[] columns, string[] fields)
    {
        var rows = snapshot.TryGetProperty(property, out var values) && values.ValueKind == JsonValueKind.Array
            ? values.EnumerateArray().Select(row => fields.Select(field => Cell(row, field)).ToArray()).ToArray()
            : [];
        return new(id, "table", label, JsonSerializer.SerializeToElement(rows), Columns: columns);
    }
    private static string Cell(JsonElement row, string field)
    {
        if (!row.TryGetProperty(field, out var value)) { return ""; }
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.True => "已变化",
            JsonValueKind.False => "",
            JsonValueKind.Null => "",
            _ => value.GetRawText()
        };
    }
}
