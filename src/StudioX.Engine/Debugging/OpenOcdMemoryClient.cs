namespace StudioX.Engine.Debugging;

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using StudioX.Foundation;

/// <summary>本次调试进程的只读 Tcl 通道；不提供任意 Tcl 执行入口。</summary>
public sealed class OpenOcdMemoryClient : IAsyncDisposable
{
    private readonly TcpClient client = new();
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool broken;

    public async Task ConnectAsync(int port, CancellationToken token) =>
        await client.ConnectAsync(IPAddress.Loopback, port, token);

    public async Task<byte[]> ReadAsync(uint address, int count, CancellationToken token)
    {
        if (count is < 1 or > 8 || (ulong)address + (uint)count > (ulong)uint.MaxValue + 1)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }
        await gate.WaitAsync(token);
        try
        {
            if (broken) { throw new IOException("OpenOCD 采样通道已关闭，请重新连接调试。"); }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            // 显式绑定当前 GDB 目标。mem2array 兼容厂商旧版；清除数组防止短读复用旧数据。
            // 只读字节，解码由主机按 ELF 字节序完成；绝不发送 halt/resume/write。
            var command = FormattableString.Invariant($"if {{[catch {{unset -nocomplain studiox_plot_bytes; $studiox_plot_target mem2array studiox_plot_bytes 8 0x{address:x8} {count}; set studiox_plot_result {{}}; for {{set i 0}} {{$i < {count}}} {{incr i}} {{lappend studiox_plot_result $studiox_plot_bytes($i)}}; set studiox_plot_result}} studiox_plot_reply]}} {{format {{ERR %s}} $studiox_plot_reply}} else {{format {{OK %s}} $studiox_plot_reply}}\x1a");
            var stream = client.GetStream();
            await stream.WriteAsync(Encoding.UTF8.GetBytes(command), timeout.Token);
            var response = new List<byte>();
            var one = new byte[1];
            while (true)
            {
                if (await stream.ReadAsync(one, timeout.Token) == 0) { throw new IOException("OpenOCD Tcl 连接中断。"); }
                if (one[0] == 0x1a) { break; }
                if (response.Count >= 8192) { throw new IOException("OpenOCD Tcl 响应超过限制。"); }
                response.Add(one[0]);
            }
            return ParseResponse(Encoding.UTF8.GetString(response.ToArray()), count);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException)
        {
            // Tcl 没有请求编号；超时/取消后的响应不可用于下一次采样。
            broken = true;
            client.Dispose();
            throw;
        }
        finally { gate.Release(); }
    }

    public static byte[] ParseResponse(string response, int count)
    {
        if (!response.StartsWith("OK ", StringComparison.Ordinal))
        {
            throw new StudioXException("OPENOCD_PLOT_READ", "OpenOCD 内存读取失败；当前目标可能不支持运行中读取。\n" + response);
        }
        var parts = response[3..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != count) { throw new IOException("OpenOCD 返回字节数不匹配：" + response); }
        return parts.Select(part =>
        {
            var hex = part.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
            return byte.TryParse(hex ? part[2..] : part, hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None,
                CultureInfo.InvariantCulture, out var value) ? value : throw new IOException("OpenOCD 返回非法字节：" + response);
        }).ToArray();
    }

    public ValueTask DisposeAsync()
    {
        broken = true;
        client.Dispose();
        return ValueTask.CompletedTask;
    }
}
