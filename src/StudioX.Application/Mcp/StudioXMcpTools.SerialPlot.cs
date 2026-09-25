namespace StudioX.Application.Mcp;

using System.ComponentModel;
using System.IO.Ports;
using System.Security.Cryptography;
using System.Text.Json;
using ModelContextProtocol.Server;
using StudioX.Application.Serial;
using StudioX.Application.SerialPlot;
using StudioX.Devices;

/// <summary>每个 MCP 工具实例独立持有设备会话，共用 DeviceHub 的端口独占规则。</summary>
public sealed partial class StudioXMcpTools : IAsyncDisposable
{
    private readonly SemaphoreSlim mcpSerialGate = new(1, 1);
    private readonly SemaphoreSlim mcpPlotGate = new(1, 1);
    private SerialTerminalService? mcpSerial;
    private SerialPlotService? mcpPlot;
    private string? mcpSerialPort;
    private string? mcpPlotPort;
    private int mcpDevicesDisposed;

    [McpServerTool(Name = "serial_list_ports")]
    [Description("列出本机可见的 COM 串口；仅枚举，不连接设备。")]
    public async Task<string> SerialListPortsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(mcpDevicesDisposed != 0, this);
        var ports = await SerialTerminalService.ListPortsAsync().ConfigureAwait(false);
        return JsonSerializer.Serialize(new { ports });
    }

    [McpServerTool(Name = "serial_status")]
    [Description("读取本 Agent 所有的串口会话状态；不读取或控制 IDE 串口终端的会话。")]
    public async Task<string> SerialStatusAsync(CancellationToken cancellationToken = default)
    {
        await mcpSerialGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(mcpDevicesDisposed != 0, this);
            var status = mcpSerial?.Status;
            return JsonSerializer.Serialize(new
            {
                ownedByAgent = mcpSerial is not null,
                port = mcpSerialPort,
                connected = status?.Connected ?? false,
                message = status?.Message ?? "Agent 未建立串口会话。",
                receivedBytes = status?.Received ?? 0,
                submittedBytes = status?.Sent ?? 0,
                droppedFrames = status?.DroppedFrames ?? 0,
                discardedHistoryBytes = status?.DiscardedHistoryBytes ?? 0,
                errors = status?.Errors.TakeLast(5).ToArray() ?? []
            });
        }
        finally { mcpSerialGate.Release(); }
    }

    [McpServerTool(Name = "serial_connect")]
    [Description("经用户逐次批准后建立 Agent 独占串口会话；不会断开 IDE 已有连接，DTR 和 RTS 默认保持低电平。")]
    public async Task<string> SerialConnectAsync(
        [Description("COM 端口名，例如 COM3。")]
        string portName,
        [Description("波特率，默认 115200。")]
        int baudRate = 115200,
        [Description("数据位 5–8，默认 8。")]
        int dataBits = 8,
        [Description("校验：None、Odd、Even、Mark 或 Space。")]
        string parity = "None",
        [Description("停止位：1、1.5 或 2。")]
        string stopBits = "1",
        [Description("流控：None、RequestToSend、XOnXOff 或 RequestToSendXOnXOff。")]
        string flowControl = "None",
        CancellationToken cancellationToken = default)
    {
        var settings = McpSerialSettings(portName, baudRate, dataBits, parity, stopBits, flowControl);
        await mcpSerialGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(mcpDevicesDisposed != 0, this);
            if (mcpSerial?.Status.Connected == true)
                throw new InvalidOperationException("Agent 已有串口会话；请先断开，再连接其他端口。");
            await RequireApprovalAsync("serial_connect",
                $"连接 {settings.PortName}，{settings.BaudRate} baud，{settings.DataBits} 数据位，{settings.Parity} 校验，{settings.StopBits} 停止位；DTR/RTS 为低电平。",
                StudioXMcpPermission.SerialConnect, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            mcpSerial ??= new SerialTerminalService(Services.Devices, Services.DataDirectory,
                scriptHostExecutable: Path.Combine(Services.RuntimeDirectory, "plugin-host", "StudioX.PluginHost.exe"));
            await mcpSerial.ConnectAsync(settings).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                await mcpSerial.DisconnectAsync().ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
            mcpSerialPort = settings.PortName;
            return JsonSerializer.Serialize(new { connected = true, port = mcpSerialPort, status = mcpSerial.Status.Message,
                clock = "host-utc", sessionOwner = "agent" });
        }
        finally { mcpSerialGate.Release(); }
    }

    [McpServerTool(Name = "serial_send")]
    [Description("经用户逐次批准后向本 Agent 已连接的串口提交数据；可用 UTF-8、GB2312 或 HEX 原始字节。超时可能已发送部分字节，不得盲目重试。")]
    public async Task<string> SerialSendAsync(
        [Description("待发送文字；mode=Hex 时为十六进制字节，例如 01 A5 FF。")]
        string text,
        [Description("Utf8、Gb2312 或 Hex，默认 Utf8。")]
        string mode = "Utf8",
        [Description("None、CrLf、Lf 或 Cr，默认 None。")]
        string lineEnding = "None",
        [Description("文字模式下是否解释 \\r、\\n、\\t、\\xHH 等转义。")]
        bool escapes = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > 8192) throw new ArgumentOutOfRangeException(nameof(text), "单次串口发送文字不能超过 8192 字符。");
        if (!Enum.TryParse<SerialTextMode>(mode, true, out var parsedMode) || !Enum.IsDefined(parsedMode))
            throw new ArgumentException("mode 必须是 Utf8、Gb2312 或 Hex。", nameof(mode));
        if (!Enum.TryParse<SerialLineEnding>(lineEnding, true, out var parsedEnding) || !Enum.IsDefined(parsedEnding))
            throw new ArgumentException("lineEnding 必须是 None、CrLf、Lf 或 Cr。", nameof(lineEnding));
        var bytes = SerialCodec.Encode(text, parsedMode, parsedEnding, escapes);
        if (bytes.Length == 0) throw new ArgumentException("发送内容不能为空。", nameof(text));
        if (bytes.Length > 8192) throw new ArgumentOutOfRangeException(nameof(text), "单次串口发送不能超过 8192 字节。");
        await mcpSerialGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(mcpDevicesDisposed != 0, this);
            if (mcpSerial?.Status.Connected != true)
                throw new InvalidOperationException("Agent 尚未连接串口，不能发送。请先调用 serial_connect。");
            var preview = Convert.ToHexString(bytes.AsSpan(0, Math.Min(bytes.Length, 32)));
            var checksum = Convert.ToHexString(SHA256.HashData(bytes));
            await RequireApprovalAsync("serial_send",
                $"向 {mcpSerialPort} 发送 {bytes.Length} 字节（{parsedMode}，行结束符 {parsedEnding}，转义 {escapes}）。前 32 字节 HEX：{preview}{(bytes.Length > 32 ? "…" : "")}；完整 SHA-256：{checksum}。",
                StudioXMcpPermission.SerialSend, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!mcpSerial.Status.Connected)
                throw new InvalidOperationException("串口会话已断开，未执行发送。");
            await mcpSerial.SendAsync(bytes).ConfigureAwait(false);
            return JsonSerializer.Serialize(new { port = mcpSerialPort, submittedBytes = bytes.Length,
                totalSubmittedBytes = mcpSerial.Status.Sent,
                note = "串口写入已完成；不代表目标设备已接收或处理。" });
        }
        finally { mcpSerialGate.Release(); }
    }

    [McpServerTool(Name = "serial_read")]
    [Description("读取本 Agent 串口会话的有界终端显示快照，可按版本轮询。该文本已经编码与 ANSI 显示处理，不是原始 RX 字节；二进制协议不可据此还原。")]
    public async Task<string> SerialReadAsync(
        [Description("上次结果的 version；传 -1 强制取得当前显示快照。")]
        long previousVersion = -1,
        [Description("最多返回多少字符，范围 1–8000；超过时保留最近内容。")]
        int maxCharacters = 4000,
        [Description("是否在显示文本中加入主机时间戳。")]
        bool timestamps = false,
        CancellationToken cancellationToken = default)
    {
        if (maxCharacters is < 1 or > 8000) throw new ArgumentOutOfRangeException(nameof(maxCharacters), "范围为 1–8000。");
        await mcpSerialGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(mcpDevicesDisposed != 0, this);
            if (mcpSerial is null)
                return JsonSerializer.Serialize(new { connected = false, changed = false, message = "Agent 尚未建立串口会话。" });
            var status = mcpSerial.Status;
            var snapshot = mcpSerial.ReadDisplay(previousVersion, timestamps);
            if (snapshot is null)
                return JsonSerializer.Serialize(new { connected = status.Connected, changed = false,
                    receivedBytes = status.Received, droppedFrames = status.DroppedFrames,
                    discardedHistoryBytes = status.DiscardedHistoryBytes });
            var omittedCharacters = Math.Max(0, snapshot.Text.Length - maxCharacters);
            var text = snapshot.Text[omittedCharacters..];
            return JsonSerializer.Serialize(new { connected = status.Connected, changed = true, version = snapshot.Version,
                displayText = text, omittedCharacters, trimmedLines = snapshot.TrimmedLines,
                receivedBytes = status.Received, droppedFrames = status.DroppedFrames,
                discardedHistoryBytes = status.DiscardedHistoryBytes, clock = "host-utc",
                representation = "rendered-terminal-text" });
        }
        finally { mcpSerialGate.Release(); }
    }

    [McpServerTool(Name = "serial_read_raw")]
    [Description("按接收字节偏移读取本 Agent 串口会话的原始 RX 帧，Base64 保留二进制内容；返回主机 UTC 接收时间、历史缺口与丢帧统计。")]
    public async Task<string> SerialReadRawAsync(
        [Description("上次结果的 nextReceivedByteOffset；首次读取传 0。")]
        long afterReceivedBytes = 0,
        [Description("本次最多返回的原始字节数，范围 1–8192。")]
        int maxBytes = 4096,
        CancellationToken cancellationToken = default)
    {
        await mcpSerialGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(mcpDevicesDisposed != 0, this);
            if (mcpSerial is null)
                return JsonSerializer.Serialize(new { connected = false, message = "Agent 尚未建立串口会话。" });
            return JsonSerializer.Serialize(new
            {
                connected = mcpSerial.Status.Connected,
                port = mcpSerialPort,
                clock = "host-utc",
                snapshot = mcpSerial.ReadRaw(afterReceivedBytes, maxBytes)
            });
        }
        finally { mcpSerialGate.Release(); }
    }

    [McpServerTool(Name = "serial_disconnect")]
    [Description("仅断开本 Agent 创建的串口会话，不影响 IDE 串口终端。")]
    public async Task<string> SerialDisconnectAsync(CancellationToken cancellationToken = default)
    {
        await mcpSerialGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(mcpDevicesDisposed != 0, this);
            if (mcpSerial is null) return JsonSerializer.Serialize(new { connected = false, message = "Agent 无串口会话。" });
            await mcpSerial.DisconnectAsync().ConfigureAwait(false);
            return JsonSerializer.Serialize(new { connected = false, port = mcpSerialPort,
                message = "Agent 串口会话已断开；显示历史仍可读取。" });
        }
        finally { mcpSerialGate.Release(); }
    }

    [McpServerTool(Name = "plot_start")]
    [Description("经用户逐次批准后开始本 Agent 的串口绘图采集；demo=true 使用本地演示数据，不连接硬件。不会断开 IDE 绘图窗口。")]
    public async Task<string> PlotStartAsync(
        [Description("真实采集所用 COM 端口；demo=true 时可留空。")]
        string portName = "",
        [Description("是否使用本地演示数据。")]
        bool demo = false,
        [Description("波特率，默认 115200。")]
        int baudRate = 115200,
        [Description("数据字段分隔符，默认逗号，可用 \\t 等转义。")]
        string fieldSeparator = ",",
        [Description("一组数据的结束符，默认 \\n，可用 \\r\\n。")]
        string recordSeparator = "\\n",
        [Description("采样周期毫秒；0 使用主机接收时钟。")]
        double sampleIntervalMs = 0,
        [Description("数据位 5–8，默认 8。")]
        int dataBits = 8,
        [Description("校验：None、Odd、Even、Mark 或 Space。")]
        string parity = "None",
        [Description("停止位：1、1.5 或 2。")]
        string stopBits = "1",
        [Description("流控：None、RequestToSend、XOnXOff 或 RequestToSendXOnXOff。")]
        string flowControl = "None",
        CancellationToken cancellationToken = default)
    {
        var format = new PlotFormat(fieldSeparator, recordSeparator, sampleIntervalMs);
        format.Validate();
        var settings = demo ? new SerialSettings() : McpSerialSettings(portName, baudRate, dataBits, parity, stopBits, flowControl);
        await mcpPlotGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(mcpDevicesDisposed != 0, this);
            if (mcpPlot?.Snapshot().Connected == true)
                throw new InvalidOperationException("Agent 已有绘图采集；请先停止，再启动新的采集。");
            await RequireApprovalAsync("plot_start",
                demo ? "启动本地演示绘图数据，不连接硬件。" : $"连接 {settings.PortName} 并解析最多 16 通道的串口绘图数据；DTR/RTS 为低电平。",
                StudioXMcpPermission.PlotConnect, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            mcpPlot ??= new SerialPlotService(Services.Devices, Services.DataDirectory);
            await mcpPlot.ConnectAsync(settings, format, demo).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                await mcpPlot.DisconnectAsync().ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
            mcpPlotPort = demo ? null : settings.PortName;
            var snapshot = mcpPlot.Snapshot();
            return JsonSerializer.Serialize(new { connected = snapshot.Connected, simulated = snapshot.Simulated,
                port = mcpPlotPort, format = snapshot.Format,
                timeSource = snapshot.Format.SampleIntervalMs > 0 ? "configured-sample-interval" : "host-monotonic-receive" });
        }
        finally { mcpPlotGate.Release(); }
    }

    [McpServerTool(Name = "plot_snapshot")]
    [Description("读取本 Agent 绘图的有界样本尾部和采集统计；每次最多 200 组，不影响 IDE 绘图会话。")]
    public async Task<string> PlotSnapshotAsync(
        [Description("尾部样本数，范围 1–200。")]
        int maxSamples = 100,
        CancellationToken cancellationToken = default)
    {
        if (maxSamples is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(maxSamples), "范围为 1–200。");
        await mcpPlotGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(mcpDevicesDisposed != 0, this);
            if (mcpPlot is null)
                return JsonSerializer.Serialize(new { connected = false, message = "Agent 尚未启动串口绘图。" });
            var snapshot = mcpPlot.Snapshot();
            return JsonSerializer.Serialize(new
            {
                snapshot.Version, snapshot.Connected, snapshot.Simulated, snapshot.Message, snapshot.LastError,
                port = mcpPlotPort, snapshot.ReceivedBytes, snapshot.ValidRecords, snapshot.InvalidRecords,
                snapshot.DroppedFrames, snapshot.EvictedRecords, snapshot.Channels, snapshot.Format,
                timeSource = snapshot.Format.SampleIntervalMs > 0 ? "configured-sample-interval" : "host-monotonic-receive",
                totalBufferedSamples = snapshot.Samples.Length,
                omittedSamples = Math.Max(0, snapshot.Samples.Length - maxSamples),
                samples = snapshot.Samples.TakeLast(maxSamples).ToArray()
            });
        }
        finally { mcpPlotGate.Release(); }
    }

    [McpServerTool(Name = "plot_stop")]
    [Description("仅停止本 Agent 创建的绘图采集，保留其有界样本历史，不影响 IDE 绘图会话。")]
    public async Task<string> PlotStopAsync(CancellationToken cancellationToken = default)
    {
        await mcpPlotGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(mcpDevicesDisposed != 0, this);
            if (mcpPlot is null) return JsonSerializer.Serialize(new { connected = false, message = "Agent 无绘图会话。" });
            await mcpPlot.DisconnectAsync().ConfigureAwait(false);
            var snapshot = mcpPlot.Snapshot();
            return JsonSerializer.Serialize(new { connected = false, simulated = snapshot.Simulated,
                port = mcpPlotPort, bufferedSamples = snapshot.Samples.Length, message = snapshot.Message });
        }
        finally { mcpPlotGate.Release(); }
    }

    private static SerialSettings McpSerialSettings(string portName, int baudRate, int dataBits,
        string parity, string stopBits, string flowControl)
    {
        if (!Enum.TryParse<Parity>(parity, true, out var parsedParity) || !Enum.IsDefined(parsedParity))
            throw new ArgumentException("parity 必须是 None、Odd、Even、Mark 或 Space。", nameof(parity));
        var parsedStopBits = stopBits switch
        {
            "1" => StopBits.One,
            "1.5" => StopBits.OnePointFive,
            "2" => StopBits.Two,
            _ when Enum.TryParse<StopBits>(stopBits, true, out var value) && Enum.IsDefined(value) => value,
            _ => throw new ArgumentException("stopBits 必须是 1、1.5 或 2。", nameof(stopBits))
        };
        if (!Enum.TryParse<Handshake>(flowControl, true, out var parsedFlow) || !Enum.IsDefined(parsedFlow))
            throw new ArgumentException("flowControl 必须是 None、RequestToSend、XOnXOff 或 RequestToSendXOnXOff。", nameof(flowControl));
        var settings = new SerialSettings(portName, baudRate, dataBits, parsedParity, parsedStopBits, parsedFlow);
        settings.Validate();
        return settings;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref mcpDevicesDisposed, 1) != 0) return;
        await mcpSerialGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (mcpSerial is not null) await mcpSerial.DisposeAsync().ConfigureAwait(false);
            mcpSerial = null;
            mcpSerialPort = null;
        }
        finally { mcpSerialGate.Release(); }
        await mcpPlotGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (mcpPlot is not null) await mcpPlot.DisposeAsync().ConfigureAwait(false);
            mcpPlot = null;
            mcpPlotPort = null;
        }
        finally { mcpPlotGate.Release(); }
        await DisposeExternalCursorsAsync().ConfigureAwait(false);
        await DisposeMcpDebugAsync().ConfigureAwait(false);
    }
}
