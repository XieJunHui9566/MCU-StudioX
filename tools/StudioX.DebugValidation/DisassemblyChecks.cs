using System.Globalization;
using StudioX.Application;
using StudioX.Engine.Debugging;
using StudioX.Foundation;

internal static class DisassemblyChecks
{
    public static async Task<int> RunStandaloneAsync()
    {
        await RunAdapterAsync(message => Console.WriteLine("PASS " + message));
        Console.WriteLine("PASS — disassembly protocol checks only; no pack, process, USB or socket used");
        return 0;
    }

    public static async Task RunAdapterAsync(Action<string> pass)
    {
        var thumb = "asm_insns=[" + Row(0x08000100, "00 bf", "nop", "main", "0") + "," +
            Row(0x08000102, "4f f0 01 00", "mov.w\tr0, #1", "main", "2") + "," +
            Row(0x08000106, "70 47", "bx\tlr", "main", "6") + "]";
        var transport = new ResponseTransport("^done," + thumb);
        await using (var adapter = new GdbDebugAdapter(transport))
        {
            var rows = await adapter.ReadDisassemblyAsync(0x08000100, 8);
            Check(transport.Commands.Single() == "-data-disassemble -s 0x08000100 -e 0x08000108 -- 2", "Disassembly must request raw opcodes in MI mode 2");
            Check(rows.Length == 3 && rows.Select(row => row.Address).SequenceEqual(new uint[] { 0x08000100, 0x08000102, 0x08000106 }), "Thumb instruction addresses must retain 2/4 byte lengths");
            Check(rows[1].Opcodes == "4f f0 01 00" && rows[1].Instruction == "mov.w\tr0, #1" && rows[1].Function == "main" && rows[1].Offset == "2", "Raw bytes, instruction, function and offset must come from GDB");
        }
        pass("Disassembly uses MI mode 2 and preserves ARM Thumb mixed 2/4 byte instruction addresses");

        transport = new ResponseTransport("^done,asm_insns=[" + Row(0x80000000, "01 00", "c.nop") + "," +
            Row(0x80000002, "13 05 10 00", "addi\ta0,zero,1") + "," + Row(0x80000006, "82 80", "c.jr\tra") + "]");
        await using (var adapter = new GdbDebugAdapter(transport))
        {
            var rows = await adapter.ReadDisassemblyAsync(0x80000000, 8);
            Check(rows.Select(row => row.Address).SequenceEqual(new uint[] { 0x80000000, 0x80000002, 0x80000006 }), "RISC-V high addresses and compressed instructions must not become signed or fixed-width");
            Check(rows.All(row => row.Function == "" && row.Offset == ""), "Code without symbols must not invent function names or offsets");
        }
        pass("Disassembly preserves unsigned RISC-V addresses, compressed instructions and absent symbols");

        var instruction = "ldr\tr0, [r1]\n; \"寄存器\" \\路径";
        var function = "函数\\with\"quote";
        transport = new ResponseTransport("^done,asm_insns=[" + Row(0x08000100, "08 68", instruction, function, "2") + "]");
        await using (var adapter = new GdbDebugAdapter(transport))
        {
            var row = (await adapter.ReadDisassemblyAsync(0x08000100, 2)).Single();
            Check(row.Instruction == instruction && row.Function == function && row.Offset == "2", "MI escaped Unicode, tabs, quotes, newlines and backslashes must survive disassembly mapping");
        }
        pass("Disassembly decodes MI escaped text without damaging Unicode or symbolic offsets");

        const string originalError = "Cannot access memory at address 0xdeadbeef\n原始 GDB 诊断";
        transport = new ResponseTransport("^error,msg=" + MiRecord.Quote(originalError));
        await using (var adapter = new GdbDebugAdapter(transport))
        {
            var error = await RejectStudioAsync(() => adapter.ReadDisassemblyAsync(0xdeadbeef, 2), "GDB_COMMAND");
            Check(error.Message == originalError, "Disassembly must retain the original GDB memory error");
        }
        pass("Disassembly preserves the original ^error diagnostic and GDB_COMMAND code");

        await using (var adapter = new GdbDebugAdapter(new ResponseTransport("^done,asm_insns=[]")))
            Check((await adapter.ReadDisassemblyAsync(0x08000100, 8)).Length == 0, "A valid empty disassembly list must remain empty");
        pass("A valid empty disassembly response does not invent instructions");

        foreach (var response in new[]
        {
            "^done", "^done,asm_insns=\"not a list\"",
            "^done,asm_insns=[{address=\"0x08000100\",opcodes=\"00 bf\"}]",
            "^done,asm_insns=[{address=\"bad\",opcodes=\"00 bf\",inst=\"nop\"}]",
            "^done,asm_insns=[{address=\"0x100000000\",opcodes=\"00 bf\",inst=\"nop\"}]",
            "^done,asm_insns=[{address=\"0x08000100\",address=\"0x08000102\",opcodes=\"00 bf\",inst=\"nop\"}]",
            "^done,asm_insns=[{address=\"0x08000100\",inst=\"nop\"}]",
            "^done,asm_insns=[" + Row(0x08000102, "00 bf", "nop") + "," + Row(0x08000100, "00 bf", "nop") + "]",
            "^done,asm_insns=[" + Row(0x080000fe, "00 bf", "nop") + "]",
            "^done,asm_insns=[" + Row(0x08000108, "00 bf", "nop") + "]"
        })
        {
            await using var adapter = new GdbDebugAdapter(new ResponseTransport(response));
            await RejectStudioAsync(() => adapter.ReadDisassemblyAsync(0x08000100, 8), "GDB_PROTOCOL");
        }
        pass("Missing, malformed and out-of-range disassembly responses are rejected as protocol errors");

        transport = new ResponseTransport("^done," + thumb);
        await using (var adapter = new GdbDebugAdapter(transport))
        {
            foreach (var count in new[] { -1, 0, 513, int.MaxValue })
                await RejectArgumentAsync(() => adapter.ReadDisassemblyAsync(0x08000100, count));
            await RejectArgumentAsync(() => adapter.ReadDisassemblyAsync(uint.MaxValue, 1));
            await RejectArgumentAsync(() => adapter.ReadDisassemblyAsync(uint.MaxValue - 15, 16));
            Check(transport.Commands.Count == 0, "Invalid byte counts and overflowing address ranges must never send MI");
        }
        pass("Invalid byte counts and 32-bit address overflows are rejected before any MI command");

        transport = new ResponseTransport("^done,asm_insns=[" + Row(0x08000100, "00 bf", "nop") + "]");
        await using (var adapter = new GdbDebugAdapter(transport))
        {
            await adapter.ReadDisassemblyAsync(0x08000100, 1);
            await adapter.ReadDisassemblyAsync(0x08000100, 512);
            Check(transport.Commands[0] == "-data-disassemble -s 0x08000100 -e 0x08000101 -- 2" &&
                transport.Commands[1] == "-data-disassemble -s 0x08000100 -e 0x08000300 -- 2", "Both byte-count boundaries must use an exclusive end address");
        }
        pass("Disassembly accepts byte-count boundaries and uses an exclusive end address");
    }

    public static async Task CheckStoppedSessionAsync(DebugSessionService session, Action<string> pass)
    {
        var pc = ProgramCounter(session);
        var current = await session.ReadDisassemblyAsync();
        Check(current.StartAddress == pc && current.FocusAddress == pc && current.ProgramCounter == pc && current.EndAddress == pc + 128,
            "Default disassembly must focus on the current PC with the default byte range");
        Check(current.Instructions.Any(row => row.Address == pc), "Current PC must be represented by an instruction");
        var explicitAddress = pc + 16;
        var manual = await session.ReadDisassemblyAsync(explicitAddress, 16);
        Check(manual.StartAddress == explicitAddress && manual.EndAddress == explicitAddress + 16 && manual.FocusAddress == explicitAddress && manual.ProgramCounter == pc,
            "Manual disassembly address must change the range and focus while retaining the actual current PC");
        pass("Paused session disassembly defaults to current PC and supports an independent manual address");
    }

    public static async Task CheckCallerSelectionAsync(DebugSessionService session, Action<string> pass)
    {
        Check(session.Snapshot.SelectedFrame == 1, "Caller validation requires frame 1 to be selected");
        var pc = ProgramCounter(session);
        var caller = ParseAddress(session.Snapshot.Frames.Single(frame => frame.Level == 1).Address);
        Check(pc != caller, "The model must provide distinct current and caller addresses for this check");
        var current = await session.ReadDisassemblyAsync();
        Check(current.StartAddress == pc && current.FocusAddress == pc && current.ProgramCounter == pc && current.StartAddress != caller,
            "Selecting a caller must not turn its saved return address into the current PC");
        Check(session.Snapshot.SelectedFrame == 1, "Reading disassembly must retain the selected caller frame");
        pass("Caller stack selection retains the actual frame-0 PC for default disassembly");
    }

    private static uint ProgramCounter(DebugSessionService session) => ParseAddress(session.Snapshot.Registers.Single(row => row.Name == "pc").Value);
    private static uint ParseAddress(string value) => uint.Parse(value.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    private static string Row(uint address, string opcodes, string instruction, string? function = null, string? offset = null) =>
        "{address=" + MiRecord.Quote("0x" + address.ToString("x8", CultureInfo.InvariantCulture)) + ",opcodes=" + MiRecord.Quote(opcodes) + ",inst=" + MiRecord.Quote(instruction) +
        (function is null ? "" : ",func-name=" + MiRecord.Quote(function)) + (offset is null ? "" : ",offset=" + MiRecord.Quote(offset)) + "}";
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task<StudioXException> RejectStudioAsync(Func<Task> action, string code)
    {
        try { await action(); }
        catch (StudioXException error) when (error.Code == code) { return error; }
        throw new InvalidOperationException("Expected " + code);
    }
    private static async Task RejectArgumentAsync(Func<Task> action)
    {
        try { await action(); }
        catch (ArgumentOutOfRangeException) { return; }
        throw new InvalidOperationException("Expected invalid disassembly range to be rejected.");
    }

    // 只回复提供的 MI 文本；记录实际发送命令，确保输入拒绝发生在传输边界之前。
    private sealed class ResponseTransport(string response) : IGdbMiTransport
    {
        public List<string> Commands { get; } = [];
        public event Action<string>? RecordReceived { add { } remove { } }
        public Task<string> ExecuteAsync(string command, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            var index = command.IndexOf('-');
            Commands.Add(command[index..]);
            return Task.FromResult(command[..index] + response);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
