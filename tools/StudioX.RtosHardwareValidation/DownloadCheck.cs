namespace StudioX.RtosHardwareValidation;

using System.Text.Json;
using StudioX.Application;
using StudioX.Application.Mcp;
using StudioX.Engine.Debugging;
using StudioX.Foundation;

/// <summary>本次测试固件的显式下载入口；授权范围仅存在于验收程序，不进入产品通用下载服务。</summary>
internal sealed class DownloadCheck
{
    private const string AuthorizedDevice = "CH32V307VCT6";
    private const string AuthorizedProbe = "wch-link";
    private const long AuthorizedBytes = 7984;
    private const string AuthorizedSha256 = "407286CB1791CA1F78E8EDC46526A9CD6EA316C36C345FFF672B53E2A00FBF6D";
    private readonly string project;
    private readonly string runtime;
    private readonly string output;
    private readonly string expectedSha256;

    public DownloadCheck(string project, string runtime, string output, string expectedSha256)
    {
        this.project = FullPath(project);
        this.runtime = FullPath(runtime);
        this.output = FullPath(output);
        if (Within(this.output, this.project) || Within(this.output, this.runtime))
            throw new ArgumentException("Validation output must be outside the firmware project and tool runtime.");
        if (expectedSha256.Length != 64 || !expectedSha256.All(Uri.IsHexDigit) ||
            !expectedSha256.Equals(AuthorizedSha256, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The expected SHA-256 must identify the explicitly authorized CH32V307VCT6 test BIN.", nameof(expectedSha256));
        this.expectedSha256 = expectedSha256.ToUpperInvariant();
    }

    public async Task<int> RunAsync()
    {
        Directory.CreateDirectory(output);
        await using var services = new WorkbenchService(runtime, Path.Combine(output, "download-user-data"));
        var authorizer = new DownloadAuthorizer(project, expectedSha256);
        await using var session = await StudioXMcpSession.CreateAsync(new StudioXMcpTools(services, project, authorizer));
        var observations = new Dictionary<string, object?>();
        var errors = new List<Exception>();
        var success = false;
        string? logPath = null;
        try
        {
            var configuration = await services.Downloads.ConfigurationAsync(project)
                ?? throw new StudioXException("RTOS_DOWNLOAD_TARGET", "当前工程没有下载配置。");
            if (configuration.Device.Id != AuthorizedDevice || configuration.Options.ProbeId != AuthorizedProbe ||
                WchDebugTarget.Find(configuration.Device) is null)
                throw new StudioXException("RTOS_DOWNLOAD_TARGET", "本次下载仅授权 CH32V307VCT6 / WCH-Link 的准确匹配配置。");
            _ = OpenOcdDebugPlanner.ResolveProbe(configuration);
            var tools = await session.ListToolsAsync();
            if (!tools.Any(tool => tool.Name == "firmware_download_plan") || !tools.Any(tool => tool.Name == "firmware_download"))
                throw new StudioXException("RTOS_DOWNLOAD_MCP", "真实 MCP 握手缺少下载预检或下载工具。");
            var plan = await CallAsync(session, "firmware_download_plan", new { });
            var device = plan.GetProperty("deviceId").GetString();
            var probe = plan.GetProperty("probeId").GetString();
            var hash = plan.GetProperty("imageSha256").GetString();
            var bytes = plan.GetProperty("imageBytes").GetInt64();
            var format = plan.GetProperty("imageFormat").GetString();
            var speed = plan.GetProperty("speedKhz").GetInt32();
            var serial = plan.GetProperty("serial").GetString();
            observations["plan"] = new
            {
                deviceId = device, probeId = probe, imageSha256 = hash, imageBytes = bytes, imageFormat = format,
                image = plan.GetProperty("image").GetString(), speedKhz = speed, serialConfigured = !string.IsNullOrEmpty(serial)
            };
            if (device != AuthorizedDevice || probe != AuthorizedProbe || format != "bin" || bytes != AuthorizedBytes ||
                !string.Equals(hash, expectedSha256, StringComparison.OrdinalIgnoreCase) ||
                speed != configuration.Options.SpeedKhz || serial != configuration.Options.Serial)
                throw new StudioXException("RTOS_DOWNLOAD_APPROVAL", "下载计划的芯片、探针、BIN 字节数、SHA-256 或探针选项与本次授权不一致。");
            authorizer.SetCheckedPlan(speed);
            // 授权器只在准确预检后启用一次，不能顺便授权附加、编译、其它固件或其它板型。
            var report = await CallAsync(session, "firmware_download", new
            {
                deviceId = device, imageSha256 = hash, probeId = probe, speedKhz = speed, serial
            });
            success = report.GetProperty("Success").GetBoolean();
            logPath = report.GetProperty("LogPath").GetString();
            observations["download"] = new
            {
                success, logPath,
                exitCode = report.GetProperty("ExitCode").GetInt32(),
                timedOut = report.GetProperty("TimedOut").GetBoolean(),
                message = report.GetProperty("message").GetString()
            };
            if (!success) errors.Add(new StudioXException("RTOS_DOWNLOAD_FAILED", "本次 MCP 下载没有通过产品校验；请查看记录中的原始日志路径。"));
        }
        catch (Exception ex) { errors.Add(ex); }
        finally
        {
            await JsonStore.WriteAsync(Path.Combine(output, "download-result.json"), new
            {
                project, hardware = true, simulated = false,
                success = success && errors.Count == 0,
                authorizedDevice = AuthorizedDevice, authorizedProbe = AuthorizedProbe,
                authorizedBytes = AuthorizedBytes, expectedSha256,
                logPath, observations,
                authorizationRequests = authorizer.Requests,
                errors = errors.Select(ex => new { type = ex.GetType().Name, ex.Message }).ToArray(),
                evidence = "用户明确授权本次测试 BIN 烧录且不要求原固件备份/恢复。仅通过既有 MCP 下载工具擦写当前映像、校验并复位运行；没有自动编译、附加调试、整片擦除或选项字节操作。"
            });
        }
        if (errors.Count > 0) throw new AggregateException("Authorized test download failed; inspect download-result.json and the original download log.", errors);
        Console.WriteLine("Authorized test download recorded: " + Path.Combine(output, "download-result.json"));
        return success ? 0 : 2;
    }

    private static async Task<JsonElement> CallAsync(StudioXMcpSession session, string tool, object arguments)
    {
        var result = JsonSerializer.Deserialize<JsonElement>(await session.CallToolAsync(tool, JsonSerializer.Serialize(arguments)));
        if (result.TryGetProperty("error", out var error))
            throw new StudioXException(result.TryGetProperty("code", out var code) ? code.GetString() ?? "RTOS_DOWNLOAD_MCP" : "RTOS_DOWNLOAD_MCP", error.GetString() ?? "MCP tool failed.");
        return result;
    }

    private static string FullPath(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    private static bool Within(string candidate, string root) => candidate.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private sealed class DownloadAuthorizer(string project, string expectedSha256) : IStudioXMcpAuthorizer
    {
        private int checkedSpeed;
        private int remainingApproval;
        public List<object> Requests { get; } = [];
        public void SetCheckedPlan(int speed)
        {
            checkedSpeed = speed;
            Volatile.Write(ref remainingApproval, 1);
        }
        public Task<bool> ApproveAsync(StudioXMcpApprovalRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var bound = FullPath(request.Project).Equals(project, StringComparison.OrdinalIgnoreCase);
            var matched = bound && request.Tool == "firmware_download" && request.Permission == StudioXMcpPermission.FirmwareDownload &&
                request.Summary.Contains("SHA-256 " + expectedSha256, StringComparison.OrdinalIgnoreCase) &&
                request.Summary.Contains(AuthorizedBytes + " 字节", StringComparison.Ordinal) &&
                request.Summary.Contains("写入 " + AuthorizedDevice, StringComparison.Ordinal) &&
                request.Summary.Contains(" / " + checkedSpeed + " kHz", StringComparison.Ordinal);
            var allowed = matched && Interlocked.CompareExchange(ref remainingApproval, 0, 1) == 1;
            Requests.Add(new { request.Tool, permission = request.Permission.ToString(), approved = allowed,
                device = AuthorizedDevice, bytes = AuthorizedBytes, sha256 = expectedSha256, speedKhz = checkedSpeed });
            return Task.FromResult(allowed);
        }
    }
}
