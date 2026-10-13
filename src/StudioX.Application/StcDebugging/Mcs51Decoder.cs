namespace StudioX.Application.StcDebugging;

using System.Text.RegularExpressions;
using StudioX.Foundation;

/// <summary>按 Intel MCS-51 操作码表解码全部 256 个编码，长度与跳转地址独立于源码映射。</summary>
public static class Mcs51Decoder
{
    private static readonly string[] Table = CreateTable();
    private static string[] CreateTable()
    {
        var rows = new[]
        {
            "NOP|AJMP {j}|LJMP {a}|RR A|INC A|INC {d}|INC @R0|INC @R1",
            "JBC {b},{r}|ACALL {j}|LCALL {a}|RRC A|DEC A|DEC {d}|DEC @R0|DEC @R1",
            "JB {b},{r}|AJMP {j}|RET|RL A|ADD A,#{i}|ADD A,{d}|ADD A,@R0|ADD A,@R1",
            "JNB {b},{r}|ACALL {j}|RETI|RLC A|ADDC A,#{i}|ADDC A,{d}|ADDC A,@R0|ADDC A,@R1",
            "JC {r}|AJMP {j}|ORL {d},A|ORL {d},#{i}|ORL A,#{i}|ORL A,{d}|ORL A,@R0|ORL A,@R1",
            "JNC {r}|ACALL {j}|ANL {d},A|ANL {d},#{i}|ANL A,#{i}|ANL A,{d}|ANL A,@R0|ANL A,@R1",
            "JZ {r}|AJMP {j}|XRL {d},A|XRL {d},#{i}|XRL A,#{i}|XRL A,{d}|XRL A,@R0|XRL A,@R1",
            "JNZ {r}|ACALL {j}|ORL C,{b}|JMP @A+DPTR|MOV A,#{i}|MOV {d},#{i}|MOV @R0,#{i}|MOV @R1,#{i}",
            "SJMP {r}|AJMP {j}|ANL C,{b}|MOVC A,@A+PC|DIV AB|MOV {d2},{d1}|MOV {d},@R0|MOV {d},@R1",
            "MOV DPTR,#{a}|ACALL {j}|MOV {b},C|MOVC A,@A+DPTR|SUBB A,#{i}|SUBB A,{d}|SUBB A,@R0|SUBB A,@R1",
            "ORL C,/{b}|AJMP {j}|MOV C,{b}|INC DPTR|MUL AB|MON51 BREAK (A5)|MOV @R0,{d}|MOV @R1,{d}",
            "ANL C,/{b}|ACALL {j}|CPL {b}|CPL C|CJNE A,#{i},{r}|CJNE A,{d},{r}|CJNE @R0,#{i},{r}|CJNE @R1,#{i},{r}",
            "PUSH {d}|AJMP {j}|CLR {b}|CLR C|SWAP A|XCH A,{d}|XCH A,@R0|XCH A,@R1",
            "POP {d}|ACALL {j}|SETB {b}|SETB C|DA A|DJNZ {d},{r}|XCHD A,@R0|XCHD A,@R1",
            "MOVX A,@DPTR|AJMP {j}|MOVX A,@R0|MOVX A,@R1|CLR A|MOV A,{d}|MOV A,@R0|MOV A,@R1",
            "MOVX @DPTR,A|ACALL {j}|MOVX @R0,A|MOVX @R1,A|CPL A|MOV {d},A|MOV @R0,A|MOV @R1,A"
        };
        var tails = new[] { "INC R{n}", "DEC R{n}", "ADD A,R{n}", "ADDC A,R{n}", "ORL A,R{n}", "ANL A,R{n}", "XRL A,R{n}", "MOV R{n},#{i}", "MOV {d},R{n}", "SUBB A,R{n}", "MOV R{n},{d}", "CJNE R{n},#{i},{r}", "XCH A,R{n}", "DJNZ R{n},{r}", "MOV A,R{n}", "MOV R{n},A" };
        return rows.SelectMany((row, index) => row.Split('|').Concat(Enumerable.Range(0, 8).Select(n => tails[index].Replace("{n}", n.ToString())))).ToArray();
    }

    public static int Length(byte opcode) => 1 + Regex.Matches(Table[opcode], @"\{([a-z][12]?)\}").Sum(m => m.Groups[1].Value == "a" ? 2 : 1);

    public static Mcs51Instruction Decode(ushort address, ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty || bytes.Length < Length(bytes[0]))
        {
            throw new StudioXException("MON51_INSTRUCTION", $"0x{address:X4} 的指令字节不完整。");
        }
        var opcode = bytes[0];
        var length = Length(opcode);
        var data = bytes[..length].ToArray();
        var offset = 1;
        ushort? target = null;
        var text = Regex.Replace(Table[opcode], @"\{([a-z][12]?)\}", match =>
        {
            var kind = match.Groups[1].Value;
            var value = kind switch
            {
                "a" => data[offset] << 8 | data[offset + 1],
                "j" => ((address + 2) & 0xf800) | (opcode & 0xe0) << 3 | data[offset],
                "r" => (ushort)(address + length + (sbyte)data[offset]),
                "d1" => data[1],
                "d2" => data[2],
                _ => data[offset]
            };
            offset += kind == "a" ? 2 : 1;
            if (kind is "j" or "r" || kind == "a" && opcode is 0x02 or 0x12)
            {
                target = (ushort)value;
            }
            return "0x" + value.ToString(kind is "a" or "j" or "r" ? "X4" : "X2");
        });
        return new(address, opcode, length, text, data, target);
    }
}
