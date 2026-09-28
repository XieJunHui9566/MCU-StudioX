using System.Text.Json;
using StudioX.Application.Espressif;
using StudioX.EspressifRegistryValidation;

var output = Path.GetFullPath(args.Length > 1 ? args[1] :
    "artifacts/validation/espressif-docs-mcp-current/component-registry");
Directory.CreateDirectory(output);
var checks = new List<string>();
void Check(bool condition, string description)
{
    if (!condition)
    {
        throw new InvalidOperationException(description);
    }
    checks.Add(description);
    Console.WriteLine("PASS " + description);
}
JsonElement Parse(string value) => JsonSerializer.Deserialize<JsonElement>(value);

if (args.FirstOrDefault() == "--live")
{
    await using var service = new EspressifComponentRegistryService(new RegistryLiveTrace(output));
    var search = Parse(await service.SearchAsync("button"));
    var detail = Parse(await service.GetInformationAsync("espressif", "button"));
    await File.WriteAllTextAsync(Path.Combine(output, "search-button.json"), search.GetRawText());
    await File.WriteAllTextAsync(Path.Combine(output, "detail-espressif-button.json"), detail.GetRawText());
    Check(search.GetProperty("status").GetString() == "ok", "真实官方目录检索 button 成功");
    Check(detail.GetProperty("status").GetString() == "ok", "真实官方目录获取 espressif/button 文档成功");
    Check(search.GetProperty("endpoint").GetString() == "https://components.espressif.com/mcp/",
        "真实查询固定已核验官方 MCP 地址");
    Check(detail.GetProperty("sourceUrl").GetString() == "https://components.espressif.com/components/espressif/button",
        "真实详情保留官方组件来源");
    var cached = Parse(await service.GetInformationAsync("espressif", "button"));
    Check(cached.GetProperty("fromCache").GetBoolean() &&
        cached.GetProperty("retrievedAtUtc").GetString() == detail.GetProperty("retrievedAtUtc").GetString(),
        "真实重复查询缓存命中且不修改检索时间");
}
else
{
    var clock = new RegistryClock();
    var handler = new RegistryHttpFixture();
    var service = new EspressifComponentRegistryService(handler, clock);
    await using (service)
    {
        var first = Parse(await service.SearchAsync("蓝牙 button"));
        Check(first.GetProperty("status").GetString() == "ok", "匿名目录可正常返回官方检索结果");
        Check(handler.LastTool == "search_components" && handler.LastArguments.GetProperty("query").GetString() == "蓝牙 button",
            "中文公开关键词按官方 query schema 传入");
        Check(handler.Initializations == 1 && handler.ProtocolVersion == "2025-06-18",
            "固定已核验的初始化协议版本");
        Check(handler.AllRequestsAnonymous && handler.AllRequestsOfficial,
            "客户端不发送 Authorization 并固定官方端点");
        Check(first.GetProperty("components")[0].GetProperty("sourceUrl").GetString() ==
            "https://components.espressif.com/components/espressif/button", "检索结果具有可核实官方来源 URL");
        var cached = Parse(await service.SearchAsync("蓝牙 button"));
        Check(handler.Calls == 1 && cached.GetProperty("fromCache").GetBoolean() &&
            first.GetProperty("retrievedAtUtc").GetString() == cached.GetProperty("retrievedAtUtc").GetString(),
            "重复公开查询命中短缓存并保留原检索时间");
        clock.Advance(TimeSpan.FromMinutes(6));
        var expired = Parse(await service.SearchAsync("蓝牙 button"));
        Check(handler.Calls == 2 && !expired.GetProperty("fromCache").GetBoolean(), "过期缓存重新请求官方服务");
        var detail = Parse(await service.GetInformationAsync("espressif", "button"));
        Check(handler.Initializations == 1 && handler.LastTool == "fetch_component_detailed_information" &&
            handler.LastArguments.GetProperty("namespace_name").GetString() == "espressif" &&
            handler.LastArguments.GetProperty("component_name").GetString() == "button", "详情按官方 schema 传参且复用同一连接");
        Check(detail.GetProperty("responseExcerpt").GetString()!.Contains("button") &&
            detail.GetProperty("sourceUrls").GetArrayLength() >= 1 &&
            detail.GetProperty("note").GetString()!.Contains("5.5.4"), "原始文档摘要及版本兼容提醒均保留");
        foreach (var invalid in new string?[] { null, "", "\nbutton", "E:\\MCU\\src\\main.c", "/home/user/main.c",
            "int main() { return 0; }", "api_key=abcdef", "sk-examplecredentialthatmustnotleave", "Bearer privatecredential", new('x', 161) })
        {
            var before = handler.Calls;
            try
            {
                await service.SearchAsync(invalid!);
                throw new InvalidOperationException("隐私查询未被拒绝。");
            }
            catch (ArgumentException)
            {
                Check(handler.Calls == before, "无网络请求拒绝路径/源码/密钥/非法查询 " + checks.Count);
            }
        }
        Check(Parse(await service.SearchAsync("password manager")).GetProperty("status").GetString() == "ok" &&
            Parse(await service.SearchAsync("secret storage")).GetProperty("status").GetString() == "ok",
            "公开组件关键词 password manager 和 secret storage 不被误当作密钥");
        try
        {
            await service.GetInformationAsync("../private", "button");
            throw new InvalidOperationException("非法命名空间未被拒绝。");
        }
        catch (ArgumentException)
        {
            Check(true, "拒绝目录穿越形式的组件标识");
        }
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            await service.SearchAsync("蓝牙 button", cancelled.Token);
            throw new InvalidOperationException("缓存掩盖调用者取消。");
        }
        catch (OperationCanceledException)
        {
            Check(true, "缓存命中前仍遵循调用者取消");
        }
        handler.LargeResponse = true;
        var large = Parse(await service.SearchAsync("many"));
        Check(large.GetProperty("totalResults").GetInt32() == 30 &&
            large.GetProperty("returnedResults").GetInt32() == 12 && large.GetProperty("truncated").GetBoolean(),
            "大量结果给出数量和明确摘要标记，提示缩小关键词");
        var longDetail = Parse(await service.GetInformationAsync("espressif", "large"));
        Check(longDetail.GetProperty("responseExcerpt").GetString()!.Length == 12_000 &&
            longDetail.GetProperty("truncated").GetBoolean(), "长组件文档摘要有界且明确标注截断");
        handler.LargeResponse = false;
        var start = handler.Calls;
        for (var index = 0; index < 105; index++)
        {
            Check(Parse(await service.SearchAsync("lookup" + index)).GetProperty("status").GetString() == "ok",
                "持续公开组件调用不设 Agent 总次数上限 " + index);
        }
        Check(handler.Calls == start + 105, "105 次不同查询均实际执行");
    }
    Check(handler.Disposed, "释放服务时释放所拥有的 HTTP 客户端");
    foreach (var status in new[] { 302, 401, 403, 429 })
    {
        var failing = new RegistryHttpFixture { FailureStatus = status };
        await using var failingService = new EspressifComponentRegistryService(failing);
        var result = Parse(await failingService.SearchAsync("button"));
        var expected = status switch
        {
            401 => "authentication_required",
            403 => "forbidden",
            429 => "rate_limited",
            _ => "unavailable"
        };
        Check(result.GetProperty("status").GetString() == expected && result.GetProperty("httpStatus").GetInt32() == status,
            "HTTP " + status + " 保留明确失败状态，不伪装为成功");
        if (status == 429)
        {
            Check(result.GetProperty("retryAfterSeconds").GetInt32() == 17, "限流 Retry-After 保留为诊断");
        }
    }
    await using (var oversizedService = new EspressifComponentRegistryService(new RegistryHttpFixture { OversizedResponse = true }))
    {
        var oversized = Parse(await oversizedService.GetInformationAsync("espressif", "large"));
        Check(oversized.GetProperty("status").GetString() == "unavailable" &&
            !oversized.TryGetProperty("responseExcerpt", out _), "超过 1 MiB 的远端正文被拒绝，不形成无界结果或缓存");
    }
    var remoteError = new RegistryHttpFixture { ToolError = true };
    await using (var errorService = new EspressifComponentRegistryService(remoteError))
    {
        Check(Parse(await errorService.SearchAsync("button")).GetProperty("status").GetString() == "remote_error",
            "官方工具错误作为 remote_error 返回");
        await errorService.SearchAsync("button");
        Check(remoteError.Calls == 2, "官方错误不进入成功缓存");
    }
    await using (var timeoutService = new EspressifComponentRegistryService(new RegistryHttpFixture { WaitForCancellation = true },
        requestTimeout: TimeSpan.FromMilliseconds(150)))
    {
        Check(Parse(await timeoutService.SearchAsync("button")).GetProperty("status").GetString() == "timeout",
            "网络无响应以有限等待返回超时诊断");
    }
    var cancelFixture = new RegistryHttpFixture { WaitForCancellation = true };
    await using (var cancelService = new EspressifComponentRegistryService(cancelFixture))
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        try
        {
            await cancelService.SearchAsync("button", cancel.Token);
            throw new InvalidOperationException("网络取消被隐藏。");
        }
        catch (OperationCanceledException)
        {
            Check(true, "网络执行中保留调用者取消语义");
        }
        cancelFixture.WaitForCancellation = false;
        Check(Parse(await cancelService.SearchAsync("button")).GetProperty("status").GetString() == "ok" &&
            cancelFixture.Initializations == 2, "取消后的新查询使用重新连接的会话");
    }
}
await File.WriteAllTextAsync(Path.Combine(output, args.FirstOrDefault() == "--live" ? "live-result.json" : "offline-result.json"),
    JsonSerializer.Serialize(new
    {
        status = "passed",
        checks = checks.Count,
        descriptions = checks
    },
        new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine("Verified " + checks.Count + " checks.");
