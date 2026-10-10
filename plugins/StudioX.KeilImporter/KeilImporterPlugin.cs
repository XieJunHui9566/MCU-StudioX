namespace StudioX.KeilImporter;

using System.Text.Json;
using StudioX.Extensions.Abstractions;

/// <summary>通过现有声明式面板提供迁移向导；不调用或扩展 Desktop 私有接口。</summary>
public sealed class KeilImporterPlugin : IStudioXPlugin
{
    private readonly IPathPicker picker;
    private readonly SemaphoreSlim invocationGate = new(1, 1);
    private readonly Dictionary<string, string> fieldIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> fields = new(StringComparer.Ordinal)
    {
        ["projectFile"] = "",
        ["sourceRoot"] = "",
        ["target"] = "",
        ["projectName"] = "",
        ["parent"] = "",
        ["query"] = "STM32F407",
        ["device"] = "",
        ["template"] = "",
        ["packs"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MCUStudioX", "packs"),
        ["cli"] = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "mcp-host", "StudioX.Cli.exe"))
    };
    private IPluginHost? host;
    private KeilTarget[] targets = [];
    private DeviceChoice[] devices = [];
    private ImportPreview? preview;
    private CreationResult? completed;
    private string confirmationId = "confirm_unavailable";
    private string message = "填写源工程，加载 Target；明确选择 STM32 型号和构建支持，预览自动生成的 CMake 后移植并编译。";

    public KeilImporterPlugin() : this(new WindowsPathPicker()) { }

    public KeilImporterPlugin(IPathPicker picker)
    {
        this.picker = picker ?? throw new ArgumentNullException(nameof(picker));
        foreach (var key in fields.Keys)
        {
            fieldIds.Add(key, key);
        }
    }

    public PluginContribution Describe() => new(
        [new("open", "Keil5 工程移植", "tools"), new("load", "加载 Keil Target"), new("search", "搜索 STM32 型号"),
            new("templates", "加载构建支持"), new("preview", "预览移植"), new("create", "确认并移植现有工程"),
            new("browseProject", "选择 Keil 工程文件"), new("browseSource", "选择源码根目录"), new("browseParent", "选择移植输出位置"),
            new("browsePacks", "选择器件包目录"), new("browseCli", "选择 StudioX CLI")],
        [Panel()], []);

    public async Task ActivateAsync(IPluginHost pluginHost, CancellationToken token)
    {
        host = pluginHost;
        await pluginHost.PublishPanelAsync(Panel(), token);
    }

    public async Task<JsonElement> InvokeAsync(string kind, string id, JsonElement arguments, CancellationToken token)
    {
        // 客户端取消等待后，旧命令仍可能正在保存编译记录；新表单操作必须等待它释放状态。
        await invocationGate.WaitAsync(token);
        try
        {
            return await InvokeCoreAsync(kind, id, arguments, token);
        }
        finally
        {
            invocationGate.Release();
        }
    }

    private async Task<JsonElement> InvokeCoreAsync(string kind, string id, JsonElement arguments, CancellationToken token)
    {
        if (kind != "command")
        {
            throw new InvalidOperationException("迁移只由用户明确点击命令启动。");
        }
        if (id == "open")
        {
            await PublishAsync(token);
            return JsonSerializer.SerializeToElement(new
            {
                opened = true
            });
        }
        var changed = UpdateFields(arguments);
        try
        {
            if (BrowseField(id) is { } field)
            {
                var selected = await picker.PickAsync(PickerRequest(field), token);
                if (selected is null)
                {
                    await PublishAsync(token);
                    return JsonSerializer.SerializeToElement(new
                    {
                        success = true,
                        cancelled = true
                    });
                }
                var path = ValidateSelectedPath(field, selected);
                if (fields[field] != path)
                {
                    SetField(field, path);
                    preview = null;
                    if (field == "projectFile")
                    {
                        targets = [];
                        SetField("target", "");
                    }
                    if (field == "packs")
                    {
                        devices = [];
                        SetField("device", "");
                        SetField("template", "");
                    }
                    message = "已选择：" + path + "\n路径已改变，请完成选择后重新预览。";
                }
                await PublishAsync(token);
                return JsonSerializer.SerializeToElement(new
                {
                    success = true,
                    cancelled = false,
                    field,
                    path
                });
            }
            switch (id)
            {
                case "load":
                    preview = null;
                    targets = KeilProjectReader.Read(fields["projectFile"]);
                    fields["target"] = "";
                    message = "加载了 " + targets.Length + " 个 Target，请明确选择。源码根目录应包含 MDK、Src、Drivers 等实际依赖。";
                    break;
                case "search":
                    preview = null;
                    devices = await DeviceCatalog.SearchAsync(fields["packs"], fields["query"].Trim(), token);
                    fields["device"] = "";
                    fields["template"] = "";
                    message = "找到 " + devices.Length + " 个型号与包版本，请明确选择后加载构建支持。";
                    break;
                case "templates":
                    preview = null;
                    _ = SelectedDevice();
                    fields["template"] = "";
                    message = "请选择与原厂商库匹配的构建支持配置。保留现有 Target 的代码和目录，只补充 GNU 启动、链接与 StudioX 构建配置。";
                    break;
                case "preview":
                    preview = null;
                    preview = await ImportPlanner.PlanAsync(Request(), token);
                    confirmationId = "confirm_" + Guid.NewGuid().ToString("N");
                    message = preview.CanCreate ? "预览就绪。请阅读所有警告、CMake 内容和完整目标路径，勾选确认后移植并自动编译。" : "预览发现无法可靠转换的配置；按错误提示处理后重新预览。";
                    break;
                case "create":
                    // 未勾选属于可恢复的操作提示，不能清除已核对的预览，避免补勾选后仍无法继续。
                    if (changed.Length > 0 || preview is null || !preview.CanCreate || !arguments.TryGetProperty("values", out var values) ||
                        !values.TryGetProperty(confirmationId, out var confirmed) || confirmed.ValueKind != JsonValueKind.True)
                    {
                        message = changed.Length > 0 ? "已修改：" + string.Join("、", changed.Select(FieldLabel)) + "。请点击“4. 预览迁移”，核对后重新勾选确认。" :
                            preview is null ? "请先点击“4. 预览迁移”，预览通过后再勾选确认。" :
                            !preview.CanCreate ? "预览存在错误，请按兼容性提示处理后重新预览。" :
                            "预览仍然有效。请勾选上方“已核对目标路径、型号和警告”，再点击“5. 移植并验证编译”。";
                        await PublishAsync(token);
                        return JsonSerializer.SerializeToElement(new
                        {
                            success = false,
                            message
                        });
                    }
                    message = "正在复制原工程、生成 CMake，并使用 IDE 管理的工具链验证编译……";
                    await PublishAsync(token);
                    var result = await ProjectCreator.CreateAsync(preview, preview.PreviewId, token);
                    completed = result;
                    preview = null;
                    message = "现有 Keil 工程已移植：" + result.Directory + "\n" +
                        (result.CompilationVerified ? "自动编译通过，已生成固件 ELF。" : "自动编译未通过：" + result.BuildError) +
                        "\n点击“打开移植工程”继续开发。原工程未被修改；完整编译日志：" + Path.Combine(result.Directory, result.BuildLog);
                    await PublishAsync(token);
                    return JsonSerializer.SerializeToElement(result, DeviceCatalog.Json);
                default:
                    throw new InvalidOperationException("未知迁移命令：" + id);
            }
            await PublishAsync(token);
            return JsonSerializer.SerializeToElement(new
            {
                success = true,
                canCreate = preview?.CanCreate ?? false,
                message
            });
        }
        catch (ProjectCreationCanceledException error)
        {
            completed = error.Result;
            preview = null;
            message = error.Result.BuildError + "\n" + error.Result.Directory;
            await PublishAsync(CancellationToken.None);
            throw;
        }
        catch (OperationCanceledException)
        {
            preview = null;
            message = "移植已取消；尚未完成的副本未发布。";
            await PublishAsync(CancellationToken.None);
            throw;
        }
        catch (Exception error)
        {
            preview = null;
            message = error.Message;
            await (host ?? throw new InvalidOperationException("插件尚未激活。")).LogAsync("error", error.ToString(), token);
            await PublishAsync(token);
            return JsonSerializer.SerializeToElement(new
            {
                success = false,
                message
            });
        }
    }

    public Task DeactivateAsync(CancellationToken token)
    {
        preview = null;
        completed = null;
        host = null;
        return Task.CompletedTask;
    }

    private string[] UpdateFields(JsonElement arguments)
    {
        var changed = new List<string>();
        if (arguments.TryGetProperty("values", out var values) && values.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in fields.Keys.ToArray())
            {
                if (values.TryGetProperty(fieldIds[key], out var value) && value.ValueKind == JsonValueKind.String)
                {
                    var text = value.GetString()!.Trim();
                    if (fields[key] != text)
                    {
                        changed.Add(key);
                    }
                    fields[key] = text;
                }
            }
        }
        if (changed.Count > 0)
        {
            preview = null;
        }
        return changed.ToArray();
    }

    private static string FieldLabel(string field) => field switch
    {
        "projectFile" => "Keil 工程文件",
        "sourceRoot" => "源码根目录",
        "target" => "Keil Target",
        "projectName" => "工程名",
        "parent" => "输出位置",
        "query" => "型号关键词",
        "device" => "目标型号 / 包版本",
        "template" => "构建支持配置",
        "packs" => "器件包目录",
        "cli" => "StudioX CLI",
        _ => field
    };

    private DeviceChoice SelectedDevice() => devices.SingleOrDefault(device => device.Key == fields["device"])
        ?? throw new InvalidOperationException("请搜索并明确选择一个 STM32 型号和器件包版本。");

    private ImportRequest Request() => new(fields["projectFile"], fields["sourceRoot"], fields["target"],
        fields["projectName"], fields["parent"], fields["cli"], fields["packs"], SelectedDevice(), fields["template"]);

    private void SetField(string field, string value)
    {
        fields[field] = value;
        // 现有宿主按控件 ID 保留用户输入；主动回填需要新 ID，防止旧空值覆盖选择结果。
        fieldIds[field] = field + "_picked_" + Guid.NewGuid().ToString("N");
    }

    private static string? BrowseField(string command) => command switch
    {
        "browseProject" => "projectFile",
        "browseSource" => "sourceRoot",
        "browseParent" => "parent",
        "browsePacks" => "packs",
        "browseCli" => "cli",
        _ => null
    };

    private PathPickerRequest PickerRequest(string field)
    {
        var initial = fields[field];
        if (initial.Length == 0 && field == "sourceRoot")
        {
            initial = Path.GetDirectoryName(fields["projectFile"]) ?? "";
        }
        return field switch
        {
            "projectFile" => new("选择要移植的 Keil5 工程", false, initial, "Keil 工程 (*.uvprojx; *.uvproj)", "*.uvprojx;*.uvproj"),
            "cli" => new("选择已安装 IDE 的 StudioX.Cli.exe", false, initial, "StudioX CLI (StudioX.Cli.exe)", "StudioX.Cli.exe"),
            "sourceRoot" => new("选择包含原工程和全部依赖的源码根目录", true, initial, "", ""),
            "parent" => new("选择移植输出位置", true, initial, "", ""),
            "packs" => new("选择已安装器件包目录", true, initial, "", ""),
            _ => throw new InvalidOperationException("未知路径字段。")
        };
    }

    private static string ValidateSelectedPath(string field, string path)
    {
        path = ImportPaths.Absolute(path);
        if (field is "projectFile" or "cli")
        {
            if (!File.Exists(path))
            {
                throw new InvalidOperationException("选择的文件不存在。");
            }
            if (field == "projectFile" && Path.GetExtension(path).ToLowerInvariant() is not (".uvprojx" or ".uvproj"))
            {
                throw new InvalidOperationException("请选择 Keil 工程 .uvprojx / .uvproj 文件。");
            }
            if (field == "cli" && !Path.GetFileName(path).Equals("StudioX.Cli.exe", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("请选择已安装 IDE 的 StudioX.Cli.exe。");
            }
        }
        else if (!Directory.Exists(path))
        {
            throw new InvalidOperationException("选择的目录不存在。");
        }
        return path;
    }

    private Task PublishAsync(CancellationToken token) => (host ?? throw new InvalidOperationException("插件尚未激活。")).PublishPanelAsync(Panel(), token);

    private PluginPanelDefinition Panel()
    {
        var templateOptions = devices.FirstOrDefault(device => device.Key == fields["device"])?.Device
            .GetProperty("templates").EnumerateArray().Select(template =>
                (Label: template.GetProperty("displayName").GetString()!, Value: template.GetProperty("id").GetString()!)).ToArray() ?? [];
        var widgets = new List<PluginPanelWidget>
        {
            new("scope", "text", "使用范围", JsonSerializer.SerializeToElement(
                "无需打开工程即可移植现有 Keil5 工程。点击浏览按钮选择工程文件、源码目录和输出位置；读取原 Target 自动配置 CMake，保留原目录并验证编译。原工程不动，不执行 Keil 构建事件。")),
            new("form", "form", "移植现有 Keil5 工程", Children:
            [
                Input("projectFile", "Keil 工程文件（.uvprojx / .uvproj）"),
                new("browseProject", "button", "浏览 Keil 工程…", CommandId: "browseProject"),
                Input("sourceRoot", "源码根目录（包含工程和全部依赖）"),
                new("browseSource", "button", "浏览源码目录…", CommandId: "browseSource"),
                new("load", "button", "1. 加载 Target", CommandId: "load"),
                Select("target", "Keil Target", targets.Select(target => (target.Name + " · " + target.Device, target.Name))),
                Input("query", "STM32 型号关键词"),
                new("search", "button", "2. 搜索已安装型号", CommandId: "search"),
                Select("device", "目标单片机 / 包版本", devices.Select(device => (device.Label, device.Key))),
                new("templates", "button", "3. 加载构建支持", CommandId: "templates"),
                Select("template", "构建支持配置（匹配原库，不生成应用模板）", templateOptions),
                Input("projectName", "移植后的工程名"),
                Input("parent", "移植输出位置（已有目录）"),
                new("browseParent", "button", "浏览输出位置…", CommandId: "browseParent"),
                Input("packs", "已安装器件包目录（默认用户数据/packs）"),
                new("browsePacks", "button", "浏览器件包目录…", CommandId: "browsePacks"),
                Input("cli", "当前 IDE 的 runtime/mcp-host/StudioX.Cli.exe"),
                new("browseCli", "button", "浏览 StudioX CLI…", CommandId: "browseCli"),
                new("preview", "button", "4. 预览迁移", CommandId: "preview"),
                preview is { CanCreate: true }
                    ? new(confirmationId, "checkbox", "已核对目标路径、型号和警告，同意移植副本并使用 IDE 工具链验证编译", JsonSerializer.SerializeToElement(false))
                    : new("confirmationHelp", "text", "确认移植", JsonSerializer.SerializeToElement("请先完成第 4 步预览；预览通过后显示确认勾选框。")),
                new("create", "button", "5. 移植并验证编译", CommandId: "create")
            ]),
            new("status", "text", "状态", JsonSerializer.SerializeToElement(message))
        };
        if (completed is { } migrated)
        {
            widgets.Add(new("completedDirectory", "text", "已移植工程", JsonSerializer.SerializeToElement(migrated.Directory)));
            widgets.Add(new("openMigratedProject", "projectLink", "打开移植工程", JsonSerializer.SerializeToElement(new
            {
                formatVersion = 1,
                directory = migrated.Directory,
                buildLog = migrated.BuildLog,
                buildRecord = ".studiox/keil-build.json"
            })));
            widgets.Add(new("buildLog", "text", "完整编译日志", JsonSerializer.SerializeToElement(Path.Combine(migrated.Directory, migrated.BuildLog))));
        }
        if (preview is { } plan)
        {
            widgets.Add(new("summary", "table", "迁移预览", JsonSerializer.SerializeToElement(new[]
            {
                new[] { "移植输出目录", plan.Destination }, new[] { "原 Target / 器件", plan.Target.Name + " / " + plan.Target.Device },
                new[] { "明确选定型号", plan.Request.Device.Id }, new[] { "复制文件数", plan.Files.Length.ToString() },
                new[] { "应用源码数", plan.ApplicationSources.Length.ToString() }, new[] { "排除构建项", string.Join(", ", plan.Target.Excluded) },
                new[] { "状态", plan.CanCreate ? "允许确认移植与编译" : "请先处理错误" }
            }), Columns: ["项目", "内容"]));
            widgets.Add(new("issues", "table", "兼容性和操作提示", JsonSerializer.SerializeToElement(plan.Issues.Select(issue =>
                new[] { issue.Severity, issue.Code, issue.Message })), Columns: ["级别", "代码", "操作提示"]));
            foreach (var (edit, index) in plan.Edits.Select((edit, index) => (edit, index)))
            {
                widgets.Add(new("edit_" + index, "text", "副本源码修改：" + edit.RelativePath,
                    JsonSerializer.SerializeToElement(edit.Reason + "\n修改前：\n" + edit.Before + "\n修改后：\n" + edit.After)));
            }
            for (var offset = 0; offset < plan.CMake.Length; offset += 3800)
            {
                widgets.Add(new("cmake_" + offset, "text", "生成的 CMakeLists.txt（" + (offset / 3800 + 1) + "）",
                    JsonSerializer.SerializeToElement(plan.CMake.Substring(offset, Math.Min(3800, plan.CMake.Length - offset)))));
            }
            widgets.Add(new("filePreview", "table", "文件副本预览（完整清单写入迁移记录）", JsonSerializer.SerializeToElement(plan.Files.Take(80).Select(file =>
                new[] { file.RelativePath, file.Bytes.ToString() })), Columns: ["保留的原相对路径", "字节"]));
        }
        return new("import", "Keil5 工程移植", widgets.ToArray());

        PluginPanelWidget Input(string id, string label) => new(fieldIds[id], "input", label, JsonSerializer.SerializeToElement(fields[id]));
        PluginPanelWidget Select(string id, string label, IEnumerable<(string Label, string Value)> options) => new(fieldIds[id], "select", label,
            JsonSerializer.SerializeToElement(new
            {
                options = new[] { new { label = "请选择", value = "" } }.Concat(options.Select(option => new { label = option.Label, value = option.Value })),
                selected = fields[id]
            }));
    }
}
