namespace StudioX.StcConfiguratorValidation;

internal static class DelayInstructionChecks
{
    internal static byte[] ReadHex(string text)
    {
        var image = new byte[65536];
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var record = Convert.FromHexString(line.Trim().AsSpan(1));
            if ((record.Sum(b => b) & 255) != 0)
            {
                throw new InvalidOperationException("Bad linked HEX checksum.");
            }
            if (record[3] == 0)
            {
                record.AsSpan(4, record[0]).CopyTo(image.AsSpan(record[1] * 256 + record[2]));
            }
        }
        return image;
    }

    internal static long Execute(byte[] image, int entry, int argument)
    {
        var registers = new byte[8];
        var ram = new byte[128];
        byte accumulator = 0;
        var pc = entry;
        long clocks = 4; // 调用方 LCALL，参数装载不计入函数耗时。
        byte Direct(int address) => address switch
        {
            0x82 => (byte)argument,
            0x83 => (byte)(argument >> 8),
            < 8 => registers[address],
            < 128 => ram[address],
            _ => throw new InvalidOperationException("Delay unexpectedly accesses memory/SFR: " + address)
        };
        void Store(int address, byte value)
        {
            if (address < 8)
            {
                registers[address] = value;
            }
            else if (address < 128)
            {
                ram[address] = value;
            }
            else
            {
                throw new InvalidOperationException("Delay unexpectedly writes an SFR: " + address);
            }
        }
        for (var steps = 0; steps < 10000000; steps++)
        {
            // 只合并语义已知的自跳：MOV A,direct; DEC A; MOV direct,A; JNZ -7。
            // 与生成器的计数公式无关，初值 0 按 uint8_t 回绕执行 256 次。
            if (image[pc] == 0xE5 && image[pc + 2] == 0x14 && image[pc + 3] == 0xF5 &&
                image[pc + 1] == image[pc + 4] && image[pc + 5] == 0x70 && image[pc + 6] == 0xF9)
            {
                var count = Direct(image[pc + 1]);
                clocks += 9L * (count == 0 ? 256 : count);
                Store(image[pc + 1], 0);
                accumulator = 0;
                pc += 7;
                continue;
            }
            var opcode = image[pc++];
            switch (opcode)
            {
                case 0x00:
                    clocks++;
                    break;
                case 0x22:
                    return clocks + 4;
                case 0x02:
                    pc = image[pc] * 256 + image[pc + 1];
                    clocks += 3;
                    break;
                case 0x14:
                    accumulator--;
                    clocks++;
                    break;
                case 0x75:
                    var direct = image[pc++];
                    Store(direct, image[pc++]);
                    clocks += 3;
                    break;
                case 0xE5:
                    accumulator = Direct(image[pc++]);
                    clocks += 2;
                    break;
                case 0xF5:
                    Store(image[pc++], accumulator);
                    clocks += 2;
                    break;
                case 0x45:
                    accumulator |= Direct(image[pc++]);
                    clocks += 2;
                    break;
                case 0x60:
                case 0x70:
                    var zeroOffset = (sbyte)image[pc++];
                    if ((accumulator == 0) == (opcode == 0x60))
                    {
                        pc += zeroOffset;
                    }
                    clocks += 4;
                    break;
                case 0x80:
                    var jump = (sbyte)image[pc++];
                    pc += jump;
                    clocks += 3;
                    break;
                case >= 0x08 and <= 0x0F:
                    registers[opcode - 8]++;
                    clocks += 2;
                    break;
                case >= 0x18 and <= 0x1F:
                    registers[opcode - 0x18]--;
                    clocks += 2;
                    break;
                case >= 0x48 and <= 0x4F:
                    accumulator |= registers[opcode - 0x48];
                    clocks++;
                    break;
                case >= 0x78 and <= 0x7F:
                    registers[opcode - 0x78] = image[pc++];
                    clocks += 2;
                    break;
                case >= 0xA8 and <= 0xAF:
                    registers[opcode - 0xA8] = Direct(image[pc++]);
                    clocks += 3;
                    break;
                case >= 0x88 and <= 0x8F:
                    Store(image[pc++], registers[opcode - 0x88]);
                    clocks += 2;
                    break;
                case >= 0xB8 and <= 0xBF:
                    var compare = image[pc++];
                    var compareOffset = (sbyte)image[pc++];
                    if (registers[opcode - 0xB8] != compare)
                    {
                        pc += compareOffset;
                    }
                    clocks += 4;
                    break;
                case >= 0xE8 and <= 0xEF:
                    accumulator = registers[opcode - 0xE8];
                    clocks++;
                    break;
                case >= 0xF8 and <= 0xFF:
                    registers[opcode - 0xF8] = accumulator;
                    clocks++;
                    break;
                case >= 0xD8 and <= 0xDF:
                    var offset = (sbyte)image[pc++];
                    var index = opcode - 0xD8;
                    if (offset == -2)
                    {
                        // 自跳 DJNZ 的指令语义可直接求和；包括初值 0 的 256 次回绕。
                        clocks += 4L * (registers[index] == 0 ? 256 : registers[index]);
                        registers[index] = 0;
                    }
                    else
                    {
                        registers[index]--;
                        if (registers[index] != 0)
                        {
                            pc += offset;
                        }
                        clocks += 4;
                    }
                    break;
                default:
                    throw new InvalidOperationException($"Unexpected Delay opcode 0x{opcode:X2} at {pc - 1:X4}; cannot claim instruction timing.");
            }
        }
        throw new InvalidOperationException("Delay does not return within the instruction budget.");
    }
}
