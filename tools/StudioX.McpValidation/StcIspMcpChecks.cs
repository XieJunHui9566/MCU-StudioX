using System.Text.Json;
using StudioX.Application;
using StudioX.Application.Mcp;
using StudioX.Engine;
using StudioX.Foundation;

internal static class StcIspMcpChecks
{
    public static async Task RunUnsupportedProjectAsync(StudioXMcpSession session,
        WorkbenchService services, SwitchingAuthorizer authorizer, string project, Action<bool, string> check)
    {
        var names = (await session.ListToolsAsync()).Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);
        check(names.Contains("stc_isp_plan") && names.Contains("stc_isp_download"),
            "real MCP handshake exposes STC ISP read-only plan and approved download");
        var sessions = Path.Combine(project, ".build");
        var before = Directory.EnumerateDirectories(sessions, "stc-isp-*").Count();
        var plan = await session.CallToolAsync("stc_isp_plan", "{}");
        var engineRejected = false;
        try { await services.StcIsp.PreviewAsync(project, new StcIspSettings(Port: "COM5")); }
        catch (StudioXException ex) when (ex.Code == "STC_ISP_DEVICE") { engineRejected = true; }
        check(plan.Contains("error", StringComparison.OrdinalIgnoreCase) && engineRejected &&
            Directory.EnumerateDirectories(sessions, "stc-isp-*").Count() == before &&
            !authorizer.Requests.Any(request => request.Tool == "stc_isp_plan"),
            "STC plan rejects non-STC project without creating a snapshot or requesting approval");
        var omittedErase = await session.CallToolAsync("stc_isp_download", JsonSerializer.Serialize(new
        {
            deviceId = "IAP15F2K61S2", imageSha256 = new string('0', 64), port = "COM5",
            transferBaud = 115200, clockMode = "preserve", clockFrequencyHz = (int?)null,
            acknowledgeFullErase = false, acknowledgeNoReadback = true
        }));
        check(omittedErase.Contains("error", StringComparison.OrdinalIgnoreCase) &&
            Directory.EnumerateDirectories(sessions, "stc-isp-*").Count() == before &&
            !authorizer.Requests.Any(request => request.Tool == "stc_isp_download"),
            "STC download rejects missing erase acknowledgement before authorization or hardware access");
    }
}
