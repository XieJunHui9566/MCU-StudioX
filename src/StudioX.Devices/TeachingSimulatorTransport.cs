namespace StudioX.Devices;

using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;

/// <summary>同用户的本机示教平台连接，不枚举、不打开串口，不触发实物监控制作。</summary>
[SupportedOSPlatform("windows")]
public sealed class TeachingSimulatorTransport : IDeviceTransport
{
    private NamedPipeClientStream? pipe;
    public string Key => "teaching:STC89C52RC";
    public bool IsSimulated => true;
    public async ValueTask OpenAsync(CancellationToken cancellationToken)
    {
        var name = "C51A.TeachingDebug.v1." + WindowsIdentity.GetCurrent().User!.Value;
        var opened = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await opened.ConnectAsync(2000, cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            var greeting = new List<byte>();
            var one = new byte[1];
            while (greeting.Count < 128)
            {
                if (await opened.ReadAsync(one, timeout.Token) == 0)
                {
                    throw new IOException("示教平台已断开。");
                }
                if (one[0] == 10)
                {
                    break;
                }
                greeting.Add(one[0]);
            }
            var identity = Encoding.ASCII.GetString(greeting.ToArray());
            if (identity == "BUSY")
            {
                throw new IOException("示教平台正由另一个调试器控制，请先结束已有仿真调试。");
            }
            if (identity != "C51A-DEBUG/1 STC89C52RC CODE=8192 XDATA=256")
            {
                throw new IOException("示教平台版本或模拟型号不匹配：" + identity);
            }
            pipe = opened;
        }
        catch (TimeoutException error)
        {
            opened.Dispose();
            throw new IOException("请先在本电脑运行支持联动调试的“单片机示教平台”，然后重试。", error);
        }
        catch
        {
            opened.Dispose();
            throw;
        }
    }
    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[4096];
        while (true)
        {
            int count = await pipe!.ReadAsync(buffer, cancellationToken);
            if (count == 0)
            {
                throw new IOException("示教平台已关闭，调试连接中断。");
            }
            yield return buffer.AsMemory(0, count).ToArray();
        }
    }
    public ValueTask SendAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken) => pipe!.WriteAsync(bytes, cancellationToken);
    public ValueTask DisposeAsync()
    {
        pipe?.Dispose();
        pipe = null;
        return ValueTask.CompletedTask;
    }
}
