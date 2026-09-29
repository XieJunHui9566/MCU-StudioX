namespace StudioX.Application;

using System.Text.RegularExpressions;
using StudioX.Application.OpenOcdPlot;
using StudioX.Engine.Debugging;

public sealed partial class DebugSessionService : IOpenOcdPlotSource
{
    private GdbProcessTransport? plotTransport;
    private uint plotRamOrigin, plotRamBytes;
    private bool plotLittleEndian;
    public Guid PlotSessionId { get; private set; }
    public bool CanPlot => IsHardware && plotTransport is not null && State is DebugState.Stopped or DebugState.Running;
    public string PlotTarget => HardwareTargetName ?? "未连接实机";

    public async Task<OpenOcdPlotChannel> ResolvePlotChannelAsync(string expression, PlotScalar type, CancellationToken token = default)
    {
        expression = expression.Trim();
        // 限制为固定存储期对象；不允许局部变量、指针链、函数调用或赋值。
        if (expression.Length > 160 || !Regex.IsMatch(expression, @"^[A-Za-z_][A-Za-z_0-9]*(?:(?:\.[A-Za-z_][A-Za-z_0-9]*)|(?:\[[0-9]{1,8}\]))*$", RegexOptions.NonBacktracking))
        { throw Error("请输入全局变量、成员或固定数组下标，例如 app_counter、sensor.value、samples[0]。"); }
        await gate.WaitAsync(token);
        try
        {
            RequireStopped();
            if (!CanPlot) { throw Error("OpenOCD 绘图需要已校验固件的实机调试会话。"); }
            return await OpenOcdPlotResolver.ResolveAsync(adapter!, expression, type, PlotSessionId, plotRamOrigin, plotRamBytes, plotLittleEndian, token);
        }
        finally { gate.Release(); }
    }

    private void CheckPlotRam(uint address, int count)
    {
        // 限定器件包已核实的主 SRAM，避免轮询带读副作用的外设寄存器。
        if (address < plotRamOrigin || (ulong)address + (uint)count > (ulong)plotRamOrigin + plotRamBytes)
        { throw Error("变量不在当前器件包声明的主 SRAM 中，暂不支持此存储区的连续采样。"); }
    }

    public async Task<OpenOcdPlotReading> ReadPlotAsync(IReadOnlyList<OpenOcdPlotChannel> channels, CancellationToken token = default)
    {
        if (channels.Count is < 1 or > 8) { throw new ArgumentOutOfRangeException(nameof(channels)); }
        await gate.WaitAsync(token);
        try
        {
            if (!CanPlot || channels.Any(channel => channel.SessionId != PlotSessionId))
            { throw Error("调试会话已结束或更换；采集停止，请重新添加变量。"); }
            var values = new double[channels.Count];
            for (var index = 0; index < channels.Count; index++)
            {
                var channel = channels[index];
                var size = PlotScalarCodec.Size(channel.Type);
                CheckPlotRam(channel.Address, size);
                values[index] = PlotScalarCodec.Decode(await plotTransport!.ReadPlotMemoryAsync(channel.Address, size, token), channel.Type, channel.LittleEndian);
            }
            return new(DateTimeOffset.Now, State == DebugState.Running, values);
        }
        catch (Exception ex) { Trace("OpenOCD 绘图：" + ex); throw; }
        finally { gate.Release(); }
    }
}
