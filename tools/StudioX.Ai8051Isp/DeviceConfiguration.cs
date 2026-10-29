using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StudioX.Ai8051Isp;

public sealed record DeviceConfiguration(int Schema, string Model, int CpuMode, int IrcHz)
{
    public const int MinimumIrcHz = 1000000, MaximumIrcHz = 48000000;
    public const byte MaximumDivider = 8;

    public static DeviceConfiguration Load(string path)
    {
        var value = JsonSerializer.Deserialize<DeviceConfiguration>(File.ReadAllText(path), new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        }) ?? throw new InvalidDataException("Empty config.");
        value.Validate();
        return value;
    }
    public void Validate()
    {
        // 仅扩展已知 IRC 字段；CPU 模式和其它硬件选项仍使用原白名单。
        if (Schema != 1 || Model != "AI8051U-34K64" || CpuMode != 32)
        {
            throw new InvalidDataException("Only schema 1, AI8051U-34K64 and 32-bit configuration are supported.");
        }
        if (IrcHz is < MinimumIrcHz or > MaximumIrcHz)
        {
            throw new InvalidDataException("AI8051U 内部 IRC 目标频率须为 1–48 MHz。");
        }
    }
    public static void CheckExisting(TargetInfo target, bool recoverErased = false)
    {
        byte[] s = Convert.FromHexString(target.RawStatus);
        if (recoverErased && s.Length == 54 && s.AsSpan(1, 12).ToArray().All(b => b == 255) && s[15] == 255 && s[16] == 255 && s[51] == 255)
        {
            return;
        }
        if (s.Length != 54 || s[8] != 0x2f || s[9] != 0xff || s[10] != 0xbf || s[11] != 0x3e || s[12] != 0x3f || s[15] != 0xf7 || s[16] != 0xfc || s[51] != 0xfe)
        {
            throw new InvalidDataException("Current hardware options differ from the captured profile. Refusing to overwrite unknown option bits.");
        }
    }
    public static byte[] BuildOptions(uint measuredHz, byte trim, byte range, byte divider = 1, int requestedHz = 40000000)
    {
        new DeviceConfiguration(1, "AI8051U-34K64", 32, requestedHz).Validate();
        if (Math.Abs((double)measuredHz - requestedHz) / requestedHz > 0.005 ||
            divider is < 1 or > MaximumDivider || (ulong)measuredHz * divider > MaximumIrcHz * 1.005 ||
            range is not (0 or 0x10 or 0x20 or 0x30))
        {
            throw new InvalidDataException("IRC calibration is invalid or differs from the requested frequency by more than 0.5%.");
        }
        byte[] packet = Convert.FromHexString("0400005AA5FFFFFF00FFFF00FFFFFFFFFFFFFFFFFFFFFFFFFFFFFF00FF02629560803001FFFFFF3E3FBFAFF7FCFE");
        if (packet.Length != 46)
        {
            throw new InvalidDataException("Invalid option template.");
        }
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(29), measuredHz);
        packet[33] = trim;
        packet[34] = range;
        packet[35] = divider;
        return packet;
    }
}
