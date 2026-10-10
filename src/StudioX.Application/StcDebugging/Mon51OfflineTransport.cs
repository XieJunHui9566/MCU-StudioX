namespace StudioX.Application.StcDebugging;

using System.Runtime.CompilerServices;
using System.Threading.Channels;
using StudioX.Devices;

/// <summary>协议测试夹具，只模拟应答与控制状态，不模拟 STC 指令或外设；不接触串口。</summary>
public sealed class Mon51OfflineTransport : IDeviceTransport
{
    private readonly Channel<ReadOnlyMemory<byte>> received = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
    private int initialSynchronizationBytes;
    public string Key => "mon51:offline";
    public bool IsSimulated => true;
    public bool Running
    {
        get; private set;
    }
    public bool CorruptNextData
    {
        get; set;
    }
    public bool FailNextBreakpointWrite
    {
        get; set;
    }
    public bool FailRestore
    {
        get; set;
    }
    public bool Silent
    {
        get; set;
    }
    public bool AllowUserProgramDownload { get; set; }
    public bool FailNextProgramWrite { get; set; }
    public TimeSpan UserEraseReplyDelay { get; set; }
    public ushort Pc { get; private set; }
    public byte[] Code { get; } = new byte[0xdc00];
    public List<byte[]> Commands { get; } = [];
    public bool UseMemoryImage
    {
        get; set;
    }
    public byte[] DataRam { get; } = new byte[256];
    public byte[] XdataRam { get; } = new byte[1024];
    public byte[] RegisterImage { get; } = new byte[19];
    public Queue<ushort> StepReplies { get; } = new();
    public Mon51OfflineTransport(bool running = false, ushort initialPc = 0x4c)
    {
        Running = running;
        Pc = initialPc;
        Code[0xa8] = 0xe5;
        RegisterImage[0] = 0x11;
        RegisterImage[1] = 0x22;
        for (var i = 0; i < 8; i++)
        {
            RegisterImage[2 + i] = (byte)(0x40 + i);
        }
        RegisterImage[10] = 0x12;
        RegisterImage[11] = 0x34;
        RegisterImage[14] = 0x84;
        RegisterImage[15] = 9;
    }
    public ValueTask OpenAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var frame in received.Reader.ReadAllAsync(cancellationToken))
        {
            yield return frame;
        }
    }
    public ValueTask SendAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var request = bytes.ToArray();
        if (request.SequenceEqual(new byte[] { 0x80 }))
        {
            initialSynchronizationBytes++;
            return ValueTask.CompletedTask;
        }
        // 冷启动串口尚未同步时不能先发控制帧；旧的暂停优先顺序必须在夹具中失败。
        if (initialSynchronizationBytes < 2)
        {
            throw new InvalidDataException("Monitor requires initial synchronization before framed commands");
        }
        if (request.Length < 5 || request[0] != 0xff || request[1] != 0x5a || request[2] != request.Length - 3 || request.Sum(b => b) % 256 != 0)
        {
            throw new InvalidDataException("Protocol fixture rejected invalid request frame");
        }
        var command = request[3..^1];
        Commands.Add(command);
        if (Silent)
        {
            return ValueTask.CompletedTask;
        }
        switch (command[0], command[1])
        {
            case (0, 0):
                if (!Running)
                {
                    Boot([0x56, 0x32, 0x2e, 0x35]);
                }
                break;
            case (0, 1):
                var registers = RegisterImage.ToArray();
                registers[12] = (byte)(Pc >> 8);
                registers[13] = (byte)Pc;
                Data(registers);
                break;
            case (0, 2):
                Running = false;
                Emit([0]);
                break;
            case (2, 0):
                Pc = (ushort)(command[2] << 8 | command[3]);
                if (Pc == 0)
                {
                    Pc = 0xdbfd;
                }
                Boot([0, 0, 0, 0]);
                break;
            case (2, 1 or 2 or 4):
                var writeAddress = command[2] << 8 | command[3];
                if (command.Length != 5 + command[4])
                {
                    throw new InvalidDataException("RAM fixture write length mismatch");
                }
                for (var i = 0; i < command[4]; i++)
                {
                    var destination = writeAddress + i;
                    var value = command[5 + i];
                    if (command[1] == 2)
                    {
                        XdataRam[destination] = value;
                    }
                    else if (command[1] == 4 && destination >= 128)
                    {
                        var register = destination switch
                        {
                            0xe0 => 0,
                            0xf0 => 1,
                            0x82 => 11,
                            0x83 => 10,
                            0xd0 => 14,
                            0x81 => 15,
                            _ => throw new InvalidDataException("Unverified context address")
                        };
                        RegisterImage[register] = value;
                    }
                    else
                    {
                        DataRam[destination] = value;
                        var bank = RegisterImage[14] & 0x18;
                        if (destination >= bank && destination < bank + 8)
                        {
                            RegisterImage[2 + destination - bank] = value;
                        }
                    }
                }
                Boot([0, 0, 0, 0]);
                break;
            case (2, 5):
                if (command.Length != 5 + command[4] || command[4] is < 1 or > 40 ||
                    !AllowUserProgramDownload && command[4] != 1)
                {
                    throw new InvalidDataException("Only a single breakpoint byte may be written");
                }
                var address = command[2] << 8 | command[3];
                if (address + command[4] > 0xdbfd || address < 3 && (!AllowUserProgramDownload || address != 0 || command[4] != 3))
                {
                    throw new InvalidDataException("Fixture rejected write outside user code or separate reset vector");
                }
                if (FailRestore && command[5] != 0xa5)
                {
                    throw new IOException("Injected restoration failure");
                }
                command.AsSpan(5).CopyTo(Code.AsSpan(address, command[4]));
                if (address == 0 && command[4] == 3)
                {
                    command.AsSpan(5).CopyTo(Code.AsSpan(0xdbfd, 3));
                }
                if (AllowUserProgramDownload && FailNextProgramWrite)
                {
                    FailNextProgramWrite = false;
                    throw new IOException("Injected user program write uncertainty");
                }
                if (FailNextBreakpointWrite)
                {
                    FailNextBreakpointWrite = false;
                    throw new IOException("Injected uncertainty after write");
                }
                Boot([0, 0, 0, 0]);
                break;
            case (6, 0):
                if (!AllowUserProgramDownload || !command.SequenceEqual(new byte[] { 6, 0, 0, 0, 0 }))
                {
                    throw new InvalidDataException("User erase not enabled for this protocol fixture");
                }
                Code.AsSpan(0, 0xdbfd).Fill(0xff);
                Running = false;
                _ = ReplyToUserEraseAsync(cancellationToken);
                break;
            case (4, _):
                var start = command[2] << 8 | command[3];
                var payload = command[1] == 5 ? Code.Skip(start).Take(command[4]).ToArray() : UseMemoryImage ? (command[1] == 2 ? XdataRam : DataRam).Skip(start).Take(command[4]).ToArray() : Enumerable.Range(0, command[4]).Select(i => (byte)(command[1] * 16 + i)).ToArray();
                Data(payload);
                break;
            case (8, 0):
                Running = true;
                Boot([0, 0, 0, 0]);
                break;
            case (8, 1):
                Pc = StepReplies.TryDequeue(out var nextPc) ? nextPc : (ushort)(Pc + 1);
                Boot([(byte)(Pc >> 8), (byte)Pc, Code[Pc], 0]);
                break;
            default:
                throw new InvalidDataException("Unsupported command in protocol fixture");
        }
        return ValueTask.CompletedTask;
    }
    public void SignalBreakpoint(ushort address)
    {
        if (!Running || Code[address] != 0xa5)
        {
            throw new InvalidOperationException("Breakpoint was not installed while running");
        }
        Pc = address;
        Running = false;
        Emit([0xff]);
    }
    private void Boot(byte[] payload) => Emit(new byte[] { 6, 0 }.Concat(payload).Append((byte)4).ToArray());
    private async Task ReplyToUserEraseAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(UserEraseReplyDelay, token);
            Boot([0, 0, 0, 0]);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
    private void Data(byte[] payload)
    {
        var response = new byte[] { 2 }.Concat(payload).ToList();
        response.Add(unchecked((byte)-response.Sum(b => b)));
        if (CorruptNextData)
        {
            response[^1] ^= 1;
            CorruptNextData = false;
        }
        Emit(response.ToArray());
    }
    private void Emit(byte[] data)
    {
        // 刻意分片，让生产解析器跨 DeviceFrame 拼接，而不是依赖一次串口读取获得完整帧。
        received.Writer.TryWrite(data.AsMemory(0, 1));
        if (data.Length > 1)
        {
            received.Writer.TryWrite(data.AsMemory(1));
        }
    }
    public ValueTask DisposeAsync()
    {
        received.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
