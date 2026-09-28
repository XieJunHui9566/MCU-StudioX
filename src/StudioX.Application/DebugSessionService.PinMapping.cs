namespace StudioX.Application;

using StudioX.Engine;
using StudioX.Engine.Debugging;

public sealed partial class DebugSessionService
{
    private static string PinMappingVerifyCommand(DownloadImageSnapshot image)
    {
        // 文件只来自本次已校验快照；仍分别转义 Tcl 与 MI，项目路径可以含空格或中文。
        var path = Path.GetFullPath(image.Path).Replace('\\', '/');
        var quoted = "\"" + path.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("$", "\\$")
            .Replace("[", "\\[").Replace("]", "\\]") + "\"";
        return "-interpreter-exec console " + MiRecord.Quote($"monitor verify_image {quoted} 0x{image.Preview.Address:x8} bin");
    }
}
