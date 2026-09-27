namespace StudioX.Application.Mcp;

using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using StudioX.Application.Lvgl;
using StudioX.Engine.Lvgl;
using StudioX.Foundation;
using static ExternalProjectPathPolicy;

/// <summary>配置和操作 PC 上的 LVGL 预览，外部库沿用会话只读授权。</summary>
internal sealed class LvglMcpTools(McpSessionContext context) : StudioXMcpToolProvider(context)
{

    [McpServerTool(Name = "lvgl_preview_start")]
    [Description("批准后编译并运行当前工程的 PC LVGL 窗口预览。使用 .studiox/lvgl-preview.json，默认内置 PC GCC；执行选中的自定义 UI C/C++ 代码，不连接、烧录或调试 MCU。尚无配置时先使用 lvgl_project_discover、lvgl_ui_inspect、lvgl_preview_configure。要求先保存编辑器改动。")]
    public async Task<string> LvglPreviewStartAsync(CancellationToken cancellationToken = default)
    {
        await RequireWorkspaceProjectAsync(cancellationToken).ConfigureAwait(false);
        await RequireSavedDocumentsAsync().ConfigureAwait(false);
        await RequireApprovalAsync("lvgl_preview_start",
            "使用选中的 PC 编译链（默认内置）编译并运行当前工程的 LVGL UI，打开 PC 预览窗口；该程序具有普通本机进程权限。",
            StudioXMcpPermission.Build, cancellationToken).ConfigureAwait(false);
        var snapshot = await Services.LvglPreview.StartAsync(Project, cancellationToken).ConfigureAwait(false);
        return SerializeLvglPreview(snapshot);
    }

    [McpServerTool(Name = "lvgl_preview_stop")]
    [Description("停止当前工程由 StudioX 启动的 LVGL 预览进程，不影响 MCU 或其他应用。")]
    public async Task<string> LvglPreviewStopAsync(CancellationToken cancellationToken = default)
    {
        await Services.LvglPreview.StopAsync(Project, cancellationToken).ConfigureAwait(false);
        return SerializeLvglPreview(Services.LvglPreview.GetSnapshot(Project));
    }

    [McpServerTool(Name = "lvgl_preview_status")]
    [Description("读取当前工程 PC LVGL 预览状态、编译/运行日志与宿主统计。PC FPS 和堆用量不能当作 MCU 实测；IsStale 表示窗口来自更早的成功构建。")]
    public Task<string> LvglPreviewStatusAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(SerializeLvglPreview(Services.LvglPreview.GetSnapshot(Project)));
    }

    [McpServerTool(Name = "lvgl_preview_screenshot")]
    [Description("读取当前工程运行中的 LVGL 预览实际帧缓冲，返回 PNG 图像和尺寸供视觉检查。只捕获本工程预览，不截取其他窗口或桌面。图像来源为 PC，不能证明 MCU 显示正常。")]
    public async Task<IEnumerable<ContentBlock>> LvglPreviewScreenshotAsync(
        CancellationToken cancellationToken = default)
    {
        var capture = await Services.LvglPreview.CaptureAsync(Project, cancellationToken).ConfigureAwait(false);
        var png = await File.ReadAllBytesAsync(capture.Path, cancellationToken).ConfigureAwait(false);
        return [new TextContentBlock { Text = JsonSerializer.Serialize(new
        {
            source = "pc-lvgl-preview", hardware = false, capture.Path, capture.Width, capture.Height,
            mimeType = capture.MimeType,
            note = "当前 PC LVGL 帧缓冲；不包含其他窗口。"
        }) }, ImageContentBlock.FromBytes(png, capture.MimeType)];
    }

    [McpServerTool(Name = "lvgl_preview_input")]
    [Description("通过本工程预览 IPC 发送鼠标/触摸、滚轮或键盘输入，检查 LVGL 控件交互。坐标为未缩放的 LVGL 像素；不向系统或其他窗口注入输入。")]
    public async Task<string> LvglPreviewInputAsync(
        [Description("pointer、wheel 或 key。")]
        string type,
        [Description("pointer 的 LVGL X 坐标，从 0 开始。")]
        int x = 0,
        [Description("pointer 的 LVGL Y 坐标，从 0 开始。")]
        int y = 0,
        [Description("pointer/key 是否按下；释放时传 false。")]
        bool pressed = false,
        [Description("wheel 滚动量，范围 -100 到 100。")]
        int delta = 0,
        [Description("key 的 LVGL 键值；例如 ENTER=10、ESC=27、NEXT=9、PREV=11、LEFT=20、RIGHT=19、UP=17、DOWN=18。")]
        int key = 0,
        CancellationToken cancellationToken = default)
    {
        if (type is not ("pointer" or "wheel" or "key"))
        {
            throw new StudioXException("LVGL_INPUT", "预览输入类型只能是 pointer、wheel 或 key。");
        }
        if ((type == "pointer" && (x < 0 || y < 0)) || (type == "wheel" && delta is < -100 or > 100) ||
            (type == "key" && key is < 1 or > 255))
        {
            throw new StudioXException("LVGL_INPUT", "预览输入坐标、滚动量或键值无效。");
        }
        await Services.LvglPreview.SendInputAsync(Project,
            new LvglPreviewInput(type, x, y, pressed, delta, key), cancellationToken).ConfigureAwait(false);
        return SerializeLvglPreview(Services.LvglPreview.GetSnapshot(Project));
    }

    [McpServerTool(Name = "lvgl_resource_report")]
    [Description("汇总上次成功目标 ELF/MAP 的真实 Flash/RAM 构建用量与 PC LVGL 堆、绘制缓冲、宿主展示面用量。保留 ABI、采样峰值和 PC FPS 的限制；不重新编译或连接硬件，不将预留堆池重复加到静态 RAM。")]
    public async Task<string> LvglResourceReportAsync(CancellationToken cancellationToken = default)
    {
        var report = await Services.LvglPreview.ReadResourcesAsync(Project, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(report);
    }

    private static string SerializeLvglPreview(LvglPreviewSnapshot snapshot)
    {
        // 编译器错误通常在构建日志末尾；返回尾部并保留原日志，模型才能看到可修复诊断。
        var log = snapshot.Log.Length <= 12_000 ? snapshot.Log
            : "[输出已截断，以下是日志末尾；完整日志见工程 .build/pc-preview/]\n" + snapshot.Log[^12_000..];
        return JsonSerializer.Serialize(snapshot with
        {
            Log = log
        });
    }

    [McpServerTool(Name = "lvgl_project_discover")]
    [Description("只读扫描当前工程中的 LVGL 实际根目录，返回路径、精确版本、完整度与重复库诊断。可以从多层外包装目录定位；不复制或移动文件。工程外扫描须先 external_project_open，并传 externalRootId。")]
    public async Task<string> LvglProjectDiscoverAsync(
        [Description("工程内或已授权外部根内的相对目录，留空扫描工程。")]
        string directory = "",
        [Description("external_project_open 返回的 rootId；留空只扫描当前工程。")]
        string externalRootId = "", CancellationToken cancellationToken = default)
    {
        await RequireWorkspaceProjectAsync(cancellationToken).ConfigureAwait(false);
        string[] search;
        if (externalRootId.Length > 0)
        {
            var root = await RequireExternalRootAsync(externalRootId, cancellationToken).ConfigureAwait(false);
            var selected = ResolveExternalPath(root, directory, allowRoot: true);
            search = [Path.GetRelativePath(Project, selected).Replace('\\', '/')];
        }
        else
        {
            if (Path.IsPathRooted(directory) || directory.Split('/').Contains("..", StringComparer.Ordinal))
            {
                throw new StudioXException("MCP_EXTERNAL_GRANT", "工程外 LVGL 扫描需要先 external_project_open 授权，再使用 externalRootId。");
            }
            var selected = directory.Length == 0 || directory == "." ? Project : PathBoundary.Resolve(Project, directory);
            search = [Path.GetRelativePath(Project, selected).Replace('\\', '/')];
        }
        return JsonSerializer.Serialize(await Services.LvglPreview.DiscoverAsync(Project, search, cancellationToken).ConfigureAwait(false));
    }

    [McpServerTool(Name = "lvgl_ui_inspect")]
    [Description("只读检查用户 UI 目录，列出源码、字体图片源、入口函数、配置头、资源目录与硬件依赖诊断。返回相对路径，不写配置、不运行代码。新外部库目录会先请求只读授权。")]
    public async Task<string> LvglUiInspectAsync(
        [Description("选中真实 LVGL 根目录，相对工程的正斜杠路径；共享库可用 ../。")]
        string libraryDirectory,
        [Description("当前工程中的 UI 相对目录，不能是工程外目录。")]
        string uiDirectory, CancellationToken cancellationToken = default)
    {
        await RequireWorkspaceProjectAsync(cancellationToken).ConfigureAwait(false);
        await EnsureLvglLibraryReadAsync(libraryDirectory, cancellationToken).ConfigureAwait(false);
        var ui = ResolveLvglRelative(uiDirectory);
        if (!LvglContained(Project, ui))
        {
            throw new StudioXException("MCP_LVGL_UI_PATH", "UI 目录必须位于当前工程内。");
        }
        return JsonSerializer.Serialize(await Services.LvglPreview.InspectUiAsync(Project, libraryDirectory, uiDirectory,
            cancellationToken).ConfigureAwait(false));
    }

    [McpServerTool(Name = "lvgl_preview_configure")]
    [Description("校验并保存自定义 UI 的 .studiox/lvgl-preview.json。configurationJson 包含明确选中的库、lv_conf.h、入口和源码/资源目录；路径均相对工程。要求已保存编辑器和 FileWrite 授权；不改目标 CMake、不编译。")]
    public async Task<string> LvglPreviewConfigureAsync(
        [Description("LvglPreviewConfiguration 的 JSON；至少含 formatVersion、lvglDirectory、configurationHeader、sourceFiles、includeDirectories、entryPoint。")]
        string configurationJson, CancellationToken cancellationToken = default)
    {
        await RequireWorkspaceProjectAsync(cancellationToken).ConfigureAwait(false);
        await RequireSavedDocumentsAsync().ConfigureAwait(false);
        if (configurationJson.Length > 128 * 1024)
        {
            throw new StudioXException("LVGL_CONFIGURATION_SIZE", "预览配置过大。");
        }
        var configuration = JsonSerializer.Deserialize<LvglPreviewConfiguration>(configurationJson, JsonStore.Options)
            ?? throw new StudioXException("LVGL_CONFIGURATION", "预览配置不能为空。");
        await EnsureLvglLibraryReadAsync(configuration.LvglDirectory, cancellationToken).ConfigureAwait(false);
        var validation = await Services.LvglPreview.ValidateSetupAsync(Project, configuration, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return JsonSerializer.Serialize(new
            {
                saved = false,
                validation
            });
        }
        await RequireApprovalAsync("lvgl_preview_configure",
            $"保存自定义 UI 预览配置；库 {configuration.LvglDirectory}，入口 {configuration.EntryPoint}，{configuration.SourceFiles.Count} 个源码；仅写入 .studiox/lvgl-preview.json。",
            StudioXMcpPermission.FileWrite, cancellationToken).ConfigureAwait(false);
        await Services.LvglPreview.SaveConfigurationAsync(Project, configuration, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            saved = true,
            configuration,
            validation
        });
    }

    private async Task EnsureLvglLibraryReadAsync(string libraryDirectory, CancellationToken token)
    {
        var selected = ResolveLvglRelative(libraryDirectory);
        if (LvglContained(Project, selected) || Context.ExternalProjects.ContainsGrantedPath(selected))
        {
            return;
        }
        var existing = PathBoundary.Resolve(Project, LvglPreviewBuilder.ConfigurationPath);
        if (File.Exists(existing))
        {
            try
            {
                var previous = await JsonStore.ReadAsync<LvglPreviewConfiguration>(existing, token).ConfigureAwait(false);
                if (ResolveLvglRelative(previous.LvglDirectory).Equals(selected, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or StudioXException) { /* 无效旧配置不能成为新读取范围。 */ }
        }
        _ = await Context.ExternalProjects.OpenAsync(selected, token).ConfigureAwait(false);
    }

    private string ResolveLvglRelative(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains('\\') ||
            relative.Any(character => character < 32 || ":\"|?*;$[]".Contains(character)))
        {
            throw new StudioXException("MCP_LVGL_PATH", "LVGL 路径必须是工程相对路径，以正斜杠分隔。");
        }
        var absolute = Path.GetFullPath(relative, Project);
        if (LvglContained(Project, absolute))
        {
            // 工程可能合法地位于验收 artifacts 下；工程内使用本身的边界，不能套用外部目录祖先过滤。
            var local = Path.GetRelativePath(Project, absolute).Replace('\\', '/');
            var selected = local == "." ? Project : PathBoundary.Resolve(Project, local);
            if (!Directory.Exists(selected))
            {
                throw new StudioXException("MCP_LVGL_PATH", $"目录不存在：{relative}");
            }
            if ((File.GetAttributes(Project) & FileAttributes.ReparsePoint) != 0)
            {
                throw new StudioXException("PATH_LINK", "工程根目录不能是重解析点。");
            }
            return selected;
        }
        // 外部共享库沿用只读外部工程的祖先与重解析点约束。
        return ValidateExternalRoot(absolute);
    }

    private static bool LvglContained(string root, string path) => path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
