namespace StudioX.StcConfigurator;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StudioX.Extensions.Abstractions;

/// <summary>准确 STC15 目标的离线配置插件；通过宿主保存配置，不持有任何设备。</summary>
public sealed class StcConfiguratorPlugin : IStudioXPlugin
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private IPluginHost? host;
    private StcConfigurationModel model = new();
    private string diskHash = "";
    private bool bound;
    private string status = "请打开配套 IAP15L2K61S2 / LQFP44 模板工程，再重新读取。";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public PluginContribution Describe() => new(
        [new("open", "打开STC图形化配置", "tools"), new("interact", "配置交互")], [Panel()], []);

    public async Task ActivateAsync(IPluginHost pluginHost, CancellationToken token)
    {
        host = pluginHost;
        try
        {
            await ReloadAsync(token);
        }
        catch (Exception error)
        {
            status = error.Message;
            await pluginHost.LogAsync("error", error.ToString(), token);
        }
        await pluginHost.PublishPanelAsync(Panel(), token);
    }

    public async Task<JsonElement> InvokeAsync(string kind, string id, JsonElement arguments, CancellationToken token)
    {
        if (kind != "command" || id is not ("open" or "interact"))
        {
            throw new ArgumentException("未知配置命令。");
        }
        await gate.WaitAsync(token);
        try
        {
            var action = id == "open" ? "reload" : arguments.GetProperty("action").GetString();
            if (action == "reload")
            {
                if (model.Dirty)
                {
                    throw new InvalidOperationException("当前配置尚未保存；请先保存，避免重新读取丢失修改。");
                }
                await ReloadAsync(token);
            }
            else if (action == "generate")
            {
                if (!bound)
                {
                    throw new InvalidOperationException("没有可生成的准确工程配置。");
                }
                model.PrepareClockValidation();
                return JsonSerializer.SerializeToElement(new PluginMcuGenerationPlan(1, diskHash,
                    JsonSerializer.SerializeToElement(model.Document, Json), model.GenerateFiles()), Json);
            }
            else if (action is "generationApplied" or "saveApplied")
            {
                var expected = arguments.GetProperty("inputSha256").GetString();
                if (expected != model.GenerationInputHash())
                {
                    throw new InvalidOperationException("生成期间配置已变化，请保留草稿后重新读取。");
                }
                await ReloadAsync(token);
                status = action == "saveApplied" ? "配置已保存。" : "已按外设生成初始化模块，main.c 已接入，源码已加入构建。";
            }
            else if (action == "save")
            {
                if (!bound || diskHash.Length != 64)
                {
                    throw new InvalidOperationException("没有可保存的准确工程配置。");
                }
                model.PrepareClockValidation();
                return JsonSerializer.SerializeToElement(new PluginMcuSavePlan(1, diskHash,
                    JsonSerializer.SerializeToElement(model.Document, Json)), Json);
            }
            else
            {
                if (!bound)
                {
                    throw new InvalidOperationException("请先打开准确型号的配套图形配置工程。");
                }
                model.Apply(arguments);
                status = model.Dirty ? "配置已修改，尚未保存；源码未变化。" : "配置检查完成；没有连接硬件。";
            }
            var panel = Panel();
            await (host ?? throw new InvalidOperationException("插件未激活。")).PublishPanelAsync(panel, token);
            return JsonSerializer.SerializeToElement(model.View(status, bound), Json);
        }
        finally { gate.Release(); }
    }

    private async Task ReloadAsync(CancellationToken token)
    {
        bound = false;
        var active = host ?? throw new InvalidOperationException("插件未激活。");
        var project = await active.CallAsync("project_info", JsonSerializer.SerializeToElement(new
        {
        }), token);
        RequireSuccess(project);
        if (String(project, "DeviceId") != "IAP15L2K61S2" || String(project, "PackId") != "stc.iap15l2k61s2.config-preview" ||
            String(project, "PackVersion") is not ("0.1.3" or "0.1.4") || String(project, "TemplateId") != "bare" ||
            String(project, "CompilerId") != "sdcc-4.5.0-15242" ||
            String(Property(project, "graphicalConfiguration"), "path") != "device.mcuconfig")
        {
            bound = false;
            throw new InvalidOperationException("当前工程不属于首版支持组合：IAP15L2K61S2 / LQFP44 / 配套配置预览包 / SDCC 4.5.0。");
        }
        var text = new StringBuilder();
        string? originalContentHash = null;
        string? originalDiskHash = null;
        var offset = 0;
        do
        {
            var page = await active.CallAsync("editor_read", JsonSerializer.SerializeToElement(new
            {
                path = "device.mcuconfig",
                offset
            }), token);
            RequireSuccess(page);
            if (Boolean(page, "IsDirty"))
            {
                throw new InvalidOperationException("配置文件在文本编辑器中有未保存修改，请先保存后重新读取。");
            }
            var contentHash = String(page, "contentHash");
            var disk = String(page, "DiskHash");
            if (originalContentHash is not null && (contentHash != originalContentHash || disk != originalDiskHash))
            {
                throw new InvalidOperationException("读取期间配置变化，请重试。");
            }
            originalContentHash = contentHash;
            originalDiskHash = disk;
            text.Append(String(page, "text"));
            if (text.Length > 256000)
            {
                throw new InvalidOperationException("首版配置文件超过安全保存容量。");
            }
            var next = Property(page, "nextOffset");
            if (next.ValueKind == JsonValueKind.Null)
            {
                break;
            }
            var following = next.GetInt32();
            if (following <= offset)
            {
                throw new InvalidOperationException("无效配置续读位置。");
            }
            offset = following;
        } while (true);
        var assembledHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
        if (!assembledHash.Equals(originalContentHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("配置分页内容与完整快照摘要不符。");
        }
        var nextModel = new StcConfigurationModel(text.ToString());
        var origin = nextModel.Document["origin"]!;
        foreach (var pair in new[] { ("packId", "PackId"), ("packVersion", "PackVersion"), ("templateId", "TemplateId"), ("deviceId", "DeviceId"), ("packContentHash", "PackContentHash") })
        {
            if (origin[pair.Item1]?.GetValue<string>() != String(project, pair.Item2))
            {
                throw new InvalidOperationException("图形配置来源与工程身份不符：" + pair.Item1);
            }
        }
        model = nextModel;
        diskHash = originalDiskHash!;
        bound = true;
        status = "已读取准确目标的工程配置；时钟频率需由用户明确填写，未读取设备。";
    }

    private PluginPanelDefinition Panel() => new("configuration", "STC图形化配置", [
        new("configuration", "mcuConfiguration", "图形配置", JsonSerializer.SerializeToElement(model.View(status, bound), Json), CommandId: "interact")]);

    private static JsonElement Property(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        ? value.EnumerateObject().FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value : default;
    private static string String(JsonElement value, string name) => Property(value, name).ValueKind == JsonValueKind.String ? Property(value, name).GetString()! : "";
    private static bool Boolean(JsonElement value, string name) => Property(value, name).ValueKind == JsonValueKind.True;
    private static void RequireSuccess(JsonElement value)
    {
        if (Property(value, "error").ValueKind != JsonValueKind.Undefined)
        {
            throw new InvalidOperationException("宿主操作失败：" + value.GetRawText());
        }
    }
    public Task DeactivateAsync(CancellationToken token)
    {
        host = null;
        return Task.CompletedTask;
    }
}
