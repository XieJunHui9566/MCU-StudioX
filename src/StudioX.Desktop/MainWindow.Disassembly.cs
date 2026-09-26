namespace StudioX.Desktop;

using System.Globalization;
using StudioX.Engine.Debugging;
using StudioX.Foundation;

public partial class MainWindow
{
    private int disassemblyRequestId;
    private Task debugDisassemblyTask = Task.CompletedTask;

    private async Task ReadDebugDisassemblyAsync(string? text)
    {
        var request = ++disassemblyRequestId;
        var snapshot = services.Debugger.Snapshot;
        var project = services.Debugger.ProjectDirectory;
        bool Current() => !closed && request == disassemblyRequestId && services.Debugger.State == DebugState.Stopped &&
            ReferenceEquals(snapshot, services.Debugger.Snapshot) && project == services.Debugger.ProjectDirectory;
        if (!Current()) return;
        DebugTools.SetDisassemblyLoading();
        try
        {
            uint? address = null;
            if (text is not null)
            {
                var value = text.Trim();
                if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) value = value[2..];
                if (!uint.TryParse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var parsed))
                    throw new StudioXException("DEBUG_DISASSEMBLY", "请输入合法的十六进制指令地址。");
                address = parsed;
            }
            var result = await services.Debugger.ReadDisassemblyAsync(address);
            // 切换工程、继续运行或新的暂停发生后，迟到的读取不能覆盖新会话界面。
            if (Current()) DebugTools.SetDisassembly(result, services.Debugger.IsHardware);
        }
        catch (Exception ex)
        {
            DebugTools.AppendOutput(ex.ToString());
            if (Current()) DebugTools.SetDisassemblyError(ex.Message);
        }
    }
}
