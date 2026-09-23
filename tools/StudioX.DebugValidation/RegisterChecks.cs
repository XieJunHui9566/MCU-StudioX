using StudioX.Engine.Debugging;

internal static class RegisterChecks
{
    public static async Task RunAsync()
    {
        foreach (var fpu in new[] { false, true })
        {
            await using var adapter = new GdbDebugAdapter(new RegisterTransport(fpu));
            var snapshot = await adapter.ReadAsync(["counter"], 0, default);
            Check(snapshot.Registers.Single(r => r.Name == "pc").Value == "0x08000100", "PC uses returned register number");
            Check(snapshot.Registers.Single(r => r.Name == "xpsr").Value == "0x01000000", "Sparse register numbering");
            Check(snapshot.Registers.All(r => r.Name.Length > 0), "Unnamed slots ignored");
            Check(snapshot.Registers.Any(r => r.Name == "s0") == fpu && snapshot.Registers.Any(r => r.Name == "fpscr") == fpu, "No invented FPU on M3");
            Check(snapshot.Frames[0].Function == "main" && snapshot.Locals[0].Name == "counter" && snapshot.Watches[0].Value == "7", "Common stack, locals and watch mapping");
        }
    }
    public static async Task RunRiscVAsync()
    {
        await using var adapter = new GdbDebugAdapter(new RegisterTransport(true, true));
        var snapshot = await adapter.ReadAsync(["counter"], 0, default);
        Check(snapshot.Registers.Single(r => r.Name == "pc").Value == "0x80000100", "RISC-V PC mapping");
        Check(snapshot.Registers.Single(r => r.Name == "mstatus").Value == "0x01000000", "RISC-V CSR mapping");
        Check(snapshot.Registers.Single(r => r.Name == "ft0").Value == "0x3f800000", "RISC-V FPU mapping");
        Check(snapshot.Registers.Any(r => r.Name == "sp") && snapshot.Registers.Any(r => r.Name == "zero"), "RISC-V integer registers");
        Check(snapshot.Registers.All(r => r.Name is not ("xpsr" or "r0" or "fpscr" or "s0") && r.Name.Length > 0), "No ARM or unnamed registers invented");
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    // 稀疏、非连续的寄存器编号，防止未来按 M4 固定索引映射 M3 的寄存器。
    private sealed class RegisterTransport(bool fpu, bool riscv = false) : IGdbMiTransport
    {
        public event Action<string>? RecordReceived { add { } remove { } }
        public Task<string> ExecuteAsync(string command, CancellationToken token = default)
        {
            var index = command.IndexOf('-');
            var number = command[..index]; var body = command[index..];
            var response = body switch
            {
                "-stack-list-frames 0 31" => "stack=[frame={level=\"0\",func=\"main\",file=\"main.c\",line=\"12\",addr=\"0x08000100\"}]",
                "-stack-select-frame 0" => "",
                "-data-list-register-names" => "register-names=[\"r0\",\"\",\"pc\",\"\",\"xpsr\"" + (fpu ? ",\"s0\",\"fpscr\"" : "") + "]",
                "-data-list-register-values x" => "register-values=[{number=\"4\",value=\"0x01000000\"},{number=\"2\",value=\"0x08000100\"},{number=\"0\",value=\"7\"},{number=\"1\",value=\"0\"},{number=\"99\",value=\"0\"}" +
                    (fpu ? ",{number=\"6\",value=\"0\"},{number=\"5\",value=\"0x3f800000\"}" : "") + "]",
                "-stack-list-variables --simple-values" => "variables=[{name=\"counter\",type=\"int\",value=\"7\"}]",
                "-data-evaluate-expression \"counter\"" => "value=\"7\"",
                _ => throw new InvalidOperationException("Unexpected MI: " + body)
            };
            if (riscv)
                response = response.Replace("0x08000100", "0x80000100", StringComparison.Ordinal)
                    .Replace("\"r0\"", "\"zero\"", StringComparison.Ordinal)
                    .Replace("\"xpsr\"", "\"mstatus\"", StringComparison.Ordinal)
                    .Replace("\"s0\"", "\"ft0\"", StringComparison.Ordinal)
                    .Replace("\"fpscr\"", "\"sp\"", StringComparison.Ordinal);
            return Task.FromResult(number + "^done" + (response.Length > 0 ? "," + response : ""));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
