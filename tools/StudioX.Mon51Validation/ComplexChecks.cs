using System.Security.Cryptography;
using StudioX.Application.StcDebugging;
using StudioX.Devices;
using StudioX.Engine;

internal static class ComplexChecks
{
    public static async Task RunAsync(string root, List<string> checks)
    {
        void Check(bool value, string name)
        {
            if (!value)
            {
                throw new InvalidOperationException(name);
            }
            checks.Add(name);
        }
        var project = Path.Combine(root, "complex-source");
        Directory.CreateDirectory(project);
        foreach (var file in new[] { "complex.c", "complex.cdb", "complex.ihx" })
        {
            File.Copy(Path.Combine(AppContext.BaseDirectory, "mon51-fixtures", file), Path.Combine(project, file), true);
        }
        var image = await File.ReadAllBytesAsync(Path.Combine(project, "complex.ihx"));
        var cdb = await File.ReadAllTextAsync(Path.Combine(project, "complex.cdb"));
        var (code, present) = StcDebugArtifacts.ParseImage(image);
        var bundle = new StcDebugArtifact(project, Path.Combine(project, "complex.ihx"), Path.Combine(project, "complex.cdb"), cdb, code, present, Convert.ToHexString(SHA256.HashData(image)), "recorded SDCC fixture", "recorded SDCC fixture");
        var symbols = Mon51Symbols.Parse(cdb, project);
        Check(symbols.Structures.Count == 1 && symbols.Structures.Single().Value.Length == 3 && symbols.Symbols.Any(s => s.Scope == "Lcomplex.work" && s.OnStack), "real CDB anonymous structure, DA4d and module-qualified stack locals");
        var fixture = new Mon51OfflineTransport { UseMemoryImage = true };
        code.CopyTo(fixture.Code, 0);
        fixture.DataRam[0x40] = 0xfd;
        fixture.DataRam[0x52] = 7;
        fixture.DataRam[0x60] = 0x80;
        fixture.DataRam[0x61] = 0;
        fixture.XdataRam[0x80] = 0xfe;
        fixture.XdataRam[0x81] = 0x34;
        fixture.XdataRam[0x82] = 0x12;
        fixture.XdataRam[0x84] = 9;
        BitConverter.GetBytes(1.25f).CopyTo(fixture.XdataRam, 0x90);
        await using var hub = new DeviceHub();
        await using var session = new Mon51DebugSession(Path.Combine(root, "complex-data"));
        await session.ConnectAsync(hub, fixture, project);
        await session.LoadSymbolsAsync(bundle);
        await session.SetPcAsync(symbols.Functions.Single(f => f.Name == "main").Start);
        Check(await session.EvaluateAsync("signed_value") == "-3" && await session.EvaluateAsync("array[2]") == "7", "signed char extension and real DA4d array element");
        Check(await session.EvaluateAsync("sample.word") == "4660" && await session.EvaluateAsync("sample.delta") == "-2" && await session.EvaluateAsync("sample.samples[1]") == "9", "module-qualified structure offsets, signed member and nested array");
        Check(await session.EvaluateAsync("pointer[2]") == "18", "XDATA pointer element uses pointee size and memory space");
        await session.ChangeWatchAsync("real", false);
        Check(session.Snapshot.Watches.Single(w => w.Name == "real").Value == "1.25", "SDCC 32-bit little-endian float observation");
        var work = symbols.Functions.Single(f => f.Name == "work");
        var caller = symbols.Functions.Single(f => f.Name == "main");
        Mcs51Instruction? call = null;
        for (var address = caller.Start; address <= caller.End;)
        {
            var instruction = Mcs51Decoder.Decode(address, code.AsSpan(address));
            if (instruction.IsCall && instruction.Target == work.Start)
            {
                call = instruction;
                break;
            }
            address += (ushort)instruction.Length;
        }
        if (call is null)
        {
            throw new InvalidOperationException("Real compiler fixture has no work call");
        }
        var returnAddress = call.Address + call.Length;
        fixture.RegisterImage[15] = 12;
        fixture.DataRam[8] = 11;
        fixture.DataRam[9] = (byte)returnAddress;
        fixture.DataRam[10] = (byte)(returnAddress >> 8);
        fixture.DataRam[12] = 5;
        await session.SetPcAsync(symbols.Lines.Single(l => l.Line == 16).Address);
        Check(session.Snapshot.Frames.Length == 2 && session.Snapshot.Locals.Any(l => l.Name == "local" && l.Value.StartsWith("5 (")), "reentrant bp prologue and SP delta prove stack local and caller");
        Check(session.Snapshot.Locals.Any(l => l.Name == "input" && l.Type.Contains("寄存器位置")), "CDB register locals disclose possible optimizer reuse");
        await session.SelectFrameAsync(1);
        await session.ChangeWatchAsync("local", false);
        Check(session.Snapshot.Watches.Single(w => w.Name == "local").Value.StartsWith("不可用"), "caller selection never reuses callee local context");
        fixture.StepReplies.Enqueue(symbols.Lines.Single(l => l.Line == 16).Address);
        await session.StepInstructionAsync();
        Check(session.Snapshot.SelectedFrame == 0 && session.Snapshot.Watches.Single(w => w.Name == "local").Value.StartsWith("5 ("), "stepping returns focus and variable scope to execution frame after browsing caller");
        fixture.RegisterImage[15] = 10;
        await session.SetPcAsync(work.End);
        Check(session.Snapshot.Locals.Single(l => l.Name == "local").Value.StartsWith("不可用"), "released stack local unavailable at function epilogue");
        await session.StopAsync();
    }
}
