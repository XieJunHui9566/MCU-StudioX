namespace StudioX.Application.Mcp;

/// <summary>工程操作绑定当前工程；外部示例只读与所有副作用均须由宿主授权。</summary>
public enum StudioXMcpPermission
{
    ExternalRead,
    FileWrite,
    Build,
    FirmwareDownload,
    GitWrite,
    GitRemote,
    DebugControl,
    HardwareConnect,
    SerialConnect,
    SerialSend,
    PlotConnect
}
