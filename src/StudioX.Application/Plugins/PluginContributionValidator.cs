namespace StudioX.Application.Plugins;

using System.Text.Json;
using System.Text.RegularExpressions;
using StudioX.Extensions;
using StudioX.Extensions.Abstractions;
using StudioX.Foundation;

/// <summary>校验声明式贡献和更新数据，限制内存与控件类型，不执行插件 UI。</summary>
public static partial class PluginContributionValidator
{
    public static void Validate(PluginManifest manifest, PluginContribution contribution)
    {
        if (contribution is null || contribution.Commands is null || contribution.Panels is null ||
            contribution.AgentTools is null || contribution.Commands.Length > 128 ||
            contribution.Panels.Length > 32 || contribution.AgentTools.Length > 128)
        {
            throw Invalid("贡献数组为空或超过数量限制。");
        }
        RequireCapability(manifest, "commands", contribution.Commands.Length);
        RequireCapability(manifest, "panels", contribution.Panels.Length);
        RequireCapability(manifest, "agentTools", contribution.AgentTools.Length);
        ValidateProductivity(manifest, contribution);
        var commands = new HashSet<string>(StringComparer.Ordinal);
        foreach (var command in contribution.Commands)
        {
            if (command is null || !commands.Add(Identifier(command.Id)))
            {
                throw Invalid("命令 ID 为空或重复。");
            }
            Text(command.Title, 256, "命令标题");
            if (manifest.Scope == "application" && command.Placement is "editorContext" or "projectContext" or "editor" or "project")
            {
                throw Invalid("应用级命令不能注册工程或编辑器上下文入口。");
            }
            if (command.Placement is not ("palette" or "toolbar" or "editorContext" or "projectContext" or
                "tools" or "project" or "editor" or "status"))
            {
                throw Invalid("未知命令位置：" + command.Placement);
            }
            if (command.Shortcut is not null)
            {
                Text(command.Shortcut, 64, "快捷键");
            }
        }
        var panels = new HashSet<string>(StringComparer.Ordinal);
        foreach (var panel in contribution.Panels)
        {
            if (panel is null || !panels.Add(Identifier(panel.Id)))
            {
                throw Invalid("面板 ID 为空或重复。");
            }
            ValidatePanel(panel, commands, manifest.Scope == "application");
        }
        var tools = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tool in contribution.AgentTools)
        {
            if (tool is null || !tools.Add(Identifier(tool.Id)))
            {
                throw Invalid("Agent 工具 ID 为空或重复。");
            }
            Text(tool.Description, 8192, "Agent 工具说明");
            ValidateJson(tool.InputSchema, 64 * 1024);
            PluginInputSchema.ValidateDefinition(tool.InputSchema);
        }
    }

    public static void ValidatePanel(PluginPanelDefinition panel, IReadOnlySet<string> commandIds, bool allowProjectLinks = false)
    {
        Identifier(panel.Id);
        Text(panel.Title, 256, "面板标题");
        if (panel.Widgets is null || panel.Widgets.Length > 128)
        {
            throw Invalid("面板控件为空或超过限制。");
        }
        var count = 0;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var widget in panel.Widgets)
        {
            ValidateWidget(widget, commandIds, ids, 0, ref count, allowProjectLinks);
        }
        ValidateJson(JsonSerializer.SerializeToElement(panel), 1024 * 1024);
    }

    public static string Identifier(string id)
    {
        if (id is null || !IdentifierPattern().IsMatch(id))
        {
            throw Invalid("贡献 ID 必须为 1 到 96 个 ASCII 字母、数字、点、连字符或下划线。");
        }
        return id;
    }

    public static void ValidateJson(JsonElement value, int maximumBytes = 1024 * 1024)
    {
        if (value.ValueKind == JsonValueKind.Undefined || System.Text.Encoding.UTF8.GetByteCount(value.GetRawText()) > maximumBytes)
        {
            throw Invalid("JSON 数据未定义或超过字节限制。");
        }
        var nodes = 0;
        WalkJson(value, 0, ref nodes);
    }

    private static void ValidateWidget(PluginPanelWidget widget, IReadOnlySet<string> commands,
        HashSet<string> ids, int depth, ref int count, bool allowProjectLinks)
    {
        if (widget is null || depth > 8 || ++count > 256 || !ids.Add(Identifier(widget.Id)))
        {
            throw Invalid("控件深度、数量或 ID 不合法。");
        }
        Text(widget.Label, 256, "控件标签", allowEmpty: true);
        if (widget.Kind is not ("text" or "table" or "tree" or "form" or "plot" or "button" or
            "input" or "number" or "checkbox" or "select" or "metric" or "chart" or "group" or "projectLink"))
        {
            throw Invalid("不支持的控件类型：" + widget.Kind);
        }
        if (widget.CommandId is not null && !commands.Contains(widget.CommandId))
        {
            throw Invalid("控件引用了未注册命令：" + widget.CommandId);
        }
        if (widget.Kind is "button" && widget.CommandId is null)
        {
            throw Invalid("按钮必须指定已注册命令。");
        }
        if (widget.Kind == "projectLink")
        {
            if (!allowProjectLinks || widget.CommandId is not null || widget.Value is not { } link)
            {
                throw Invalid("工程入口仅允许应用级面板声明目标数据，不执行插件命令。");
            }
            _ = PluginProjectLink.Parse(link);
        }
        if (widget.Columns is not null)
        {
            if (widget.Kind != "table" || widget.Columns.Length is < 1 or > 32)
            {
                throw Invalid("只有表格可以声明 1 到 32 个列。");
            }
            foreach (var column in widget.Columns)
            {
                Text(column, 128, "表格列");
            }
        }
        if (widget.Value is { } value)
        {
            ValidateJson(value, 512 * 1024);
            if (widget.Kind is "table" && (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 2048))
            {
                throw Invalid("表格必须提供最多 2048 行的数组。");
            }
            if (widget.Kind is "number" && value.ValueKind is not (JsonValueKind.Number or JsonValueKind.Null))
            {
                throw Invalid("数字输入必须为有限数值或 null。");
            }
            if (widget.Kind is "checkbox" && value.ValueKind is not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null))
            {
                throw Invalid("复选框必须为布尔值或 null。");
            }
            if (widget.Kind is "text" or "input" && value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            {
                throw Invalid("文字控件必须提供字符串或 null。");
            }
        }
        if (widget.Children is not null)
        {
            if (widget.Kind is not ("tree" or "form" or "group") || widget.Children.Length > 128)
            {
                throw Invalid("只有 tree/form/group 控件接受有界子控件。");
            }
            foreach (var child in widget.Children)
            {
                ValidateWidget(child, commands, ids, depth + 1, ref count, allowProjectLinks);
            }
        }
    }

    private static void WalkJson(JsonElement value, int depth, ref int nodes)
    {
        if (depth > 24 || ++nodes > 20000)
        {
            throw Invalid("JSON 嵌套或元素数量超过限制。");
        }
        if (value.ValueKind == JsonValueKind.Number && (!value.TryGetDouble(out var number) || !double.IsFinite(number)))
        {
            throw Invalid("JSON 数值必须为有限数。");
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in value.EnumerateArray())
            {
                WalkJson(child, depth + 1, ref nodes);
            }
        }
        else if (value.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!keys.Add(property.Name) || property.Name.Length > 256)
                {
                    throw Invalid("JSON 属性重复或名称过长。");
                }
                WalkJson(property.Value, depth + 1, ref nodes);
            }
        }
    }

    private static void Text(string value, int maximum, string name, bool allowEmpty = false)
    {
        if (value is null || value.Length > maximum || (!allowEmpty && string.IsNullOrWhiteSpace(value)) ||
            value.Any(character => character == '\0'))
        {
            throw Invalid(name + "为空、包含 NUL 或超过长度限制。");
        }
    }

    private static void RequireCapability(PluginManifest manifest, string capability, int count)
    {
        if (count > 0 && !manifest.Capabilities.Contains(capability, StringComparer.Ordinal))
        {
            throw Invalid("清单未声明贡献能力：" + capability);
        }
    }

    private static StudioXException Invalid(string message) => new("PLUGIN_CONTRIBUTION", message);

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.-]{0,95}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();
}
