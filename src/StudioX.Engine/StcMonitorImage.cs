namespace StudioX.Engine;

using System.Security.Cryptography;
using System.Text;
using StudioX.Foundation;

/// <summary>核对原厂资源身份并生成研究用 HEX；实际制作仅接受专用编码 BIN，不接受任意监控 HEX。</summary>
public static class StcMonitorImage
{
    public const string VendorExecutableSha256 = "DC8B06F1FD951D2CBCCDAC9A6BA120233E82C83E53C56761BD8C794E86E2050E";
    public const string MonitorSha256 = "6F21800CC1C83D0F8FD0A5ED920186051F913EB889E0E92623E1EE63E3C71B2D";
    public const string ResetSha256 = "0D16C584EF418DE8B6C451C3447084499A531A2F752568EB4930586DA075D3BD";
    public const string SetupSha256 = "ABC080915DDDCA9589F184E9BE76FBCC757EDF001BE02AF7A70ECB57451106F9";
    public const string Bootloader = "7.2.5S";
    public static void ValidateSetup(byte[] bytes)
    {
        if (bytes.Length != 61440 || Hash(bytes) != SetupSha256)
            {throw new StudioXException("MON51_FIRMWARE", "制作镜像与已核对的 IAP15F2K61S2 / 7.2.5S 固件身份不符。");}
    }
    public static byte[] CreateHex(byte[] monitor, byte[] reset)
    {
        if (monitor.Length != 4096 || reset.Length != 512 || Hash(monitor) != MonitorSha256 || Hash(reset) != ResetSha256)
        {
            throw new StudioXException("MON51_FIRMWARE", "监控固件不符合已核对的 AiCube 6.96V-plus / IAP15F2K61S2 资源身份。");
        }
        var text = new StringBuilder();
        Add(reset, 0);
        Add(monitor, 0xe000);
        text.Append(":00000001FF\n");
        return Encoding.ASCII.GetBytes(text.ToString());
        void Add(byte[] bytes, int start)
        {
            for (var offset = 0; offset < bytes.Length; offset += 16)
            {
                var record = new byte[21];
                record[0] = 16;
                record[1] = (byte)((start + offset) >> 8);
                record[2] = (byte)(start + offset);
                bytes.AsSpan(offset, 16).CopyTo(record.AsSpan(4));
                record[^1] = unchecked((byte)-record.Sum(value => value));
                text.Append(':').Append(Convert.ToHexString(record)).Append('\n');
            }
        }
    }
    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
