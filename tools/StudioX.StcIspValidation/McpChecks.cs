using System.Text.Json;
using StudioX.Application;
using StudioX.Application.Mcp;

internal static class McpChecks
{
    public static async Task RunAsync(string project, string runtime, string output,
        Action<bool, string> check)
    {
        var snapshotRoot = Path.Combine(project, ".build");
        var before = Directory.EnumerateDirectories(snapshotRoot, "stc-isp-*").Count();
        await using var services = new WorkbenchService(runtime, Path.Combine(output, "mcp-user-data"));
        var authorizer = new DenyAndRecordAuthorizer();
        await using var session = await StudioXMcpSession.CreateAsync(
            new StudioXMcpTools(services, project, authorizer));
        var names = (await session.ListToolsAsync()).Select(item => item.Name).ToHashSet(StringComparer.Ordinal);
        check(names.Contains("stc_isp_plan") && names.Contains("stc_isp_download"),
            "STC ISP tools are available through the real MCP client/server handshake");

        var planText = await session.CallToolAsync("stc_isp_plan", "{}");
        using var plan = JsonDocument.Parse(planText);
        var values = plan.RootElement;
        var deviceId = values.GetProperty("deviceId").GetString()!;
        var sha = values.GetProperty("imageSha256").GetString()!;
        var port = values.GetProperty("port").GetString()!;
        var baud = values.GetProperty("transferBaud").GetInt32();
        var clockMode = values.GetProperty("clockMode").GetString()!;
        var frequency = values.GetProperty("clockFrequencyHz").GetInt32();
        check(deviceId == "IAP15F2K61S2" && sha.Length == 64 && port == "COM5" &&
            baud == 115200 && clockMode == "internal_rc" && frequency == 11059200 &&
            values.GetProperty("fullErasePossible").GetBoolean() &&
            !values.GetProperty("flashReadbackAvailable").GetBoolean() &&
            Directory.EnumerateDirectories(snapshotRoot, "stc-isp-*").Count() == before &&
            authorizer.Requests.Count == 0,
            "MCP STC plan returns exact build and ISP settings without snapshot or approval");

        string Args(string selectedSha, string selectedPort, string selectedMode, int? selectedFrequency,
            bool erase, bool readback) => JsonSerializer.Serialize(new
        {
            deviceId, imageSha256 = selectedSha, port = selectedPort, transferBaud = baud,
            clockMode = selectedMode, clockFrequencyHz = selectedFrequency,
            acknowledgeFullErase = erase, acknowledgeNoReadback = readback
        });
        var wrongHash = await session.CallToolAsync("stc_isp_download",
            Args(new string('0', 64), port, clockMode, frequency, true, true));
        var wrongPort = await session.CallToolAsync("stc_isp_download",
            Args(sha, "COM6", clockMode, frequency, true, true));
        var wrongClock = await session.CallToolAsync("stc_isp_download",
            Args(sha, port, "preserve", null, true, true));
        var missingErase = await session.CallToolAsync("stc_isp_download",
            Args(sha, port, clockMode, frequency, false, true));
        check(new[] { wrongHash, wrongPort, wrongClock, missingErase }
                .All(result => result.Contains("error", StringComparison.OrdinalIgnoreCase)) &&
            authorizer.Requests.Count == 0 &&
            Directory.EnumerateDirectories(snapshotRoot, "stc-isp-*").Count() == before,
            "MCP refuses mismatched HEX, COM, clock or erase acknowledgement before authorization");

        var denied = await session.CallToolAsync("stc_isp_download",
            Args(sha, port, clockMode, frequency, true, true));
        check(denied.Contains("error", StringComparison.OrdinalIgnoreCase) &&
            authorizer.Requests.Count == 1 &&
            authorizer.Requests[0].Permission == StudioXMcpPermission.FirmwareDownload &&
            authorizer.Requests[0].Summary.Contains(sha, StringComparison.Ordinal) &&
            authorizer.Requests[0].Summary.Contains(port, StringComparison.Ordinal) &&
            authorizer.Requests[0].Summary.Contains("11059200", StringComparison.Ordinal) &&
            Directory.EnumerateDirectories(snapshotRoot, "stc-isp-*").Count() == before,
            "MCP requests one explicit STC download approval and default denial creates no snapshot");
    }

    private sealed class DenyAndRecordAuthorizer : IStudioXMcpAuthorizer
    {
        public List<StudioXMcpApprovalRequest> Requests { get; } = [];
        public Task<bool> ApproveAsync(StudioXMcpApprovalRequest request, CancellationToken token)
        {
            Requests.Add(request);
            return Task.FromResult(false);
        }
    }
}
