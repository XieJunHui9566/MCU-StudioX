using System.Security.Cryptography;

namespace StudioX.Ai8051Isp;

public sealed record FirmwareImage(byte[] Code, string Sha256, uint BaseAddress)
{
    // 32 位映像必须显式位于 FF0000 页；拒绝混合页、重叠、损坏记录和隐式截断。
    public static FirmwareImage Load(string path)
    {
        byte[] file = File.ReadAllBytes(path);
        return Parse(System.Text.Encoding.ASCII.GetString(file), Convert.ToHexString(SHA256.HashData(file)));
    }
    public static FirmwareImage Parse(string text, string sha256 = "")
    {
        var memory = new SortedDictionary<uint, byte>();
        uint high = 0;
        bool ended = false;
        foreach (string line in text.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0))
        {
            if (ended || !line.StartsWith(':'))
            {
                throw new InvalidDataException("Unexpected Intel HEX data after EOF or missing colon.");
            }
            byte[] record;
            try
            {
                record = Convert.FromHexString(line[1..]);
            }
            catch (FormatException e) { throw new InvalidDataException("Invalid Intel HEX encoding.", e); }
            if (record.Length < 5 || record.Length != record[0] + 5 || (record.Sum(x => x) & 255) != 0)
            {
                throw new InvalidDataException("Intel HEX size/checksum mismatch.");
            }
            uint offset = (uint)(record[1] << 8 | record[2]);
            switch (record[3])
            {
                case 0:
                    if (offset + record[0] > 65536)
                    {
                        throw new InvalidDataException("Intel HEX record crosses a segment boundary.");
                    }
                    for (uint i = 0; i < record[0]; i++)
                    {
                        uint address = checked(high + offset + i);
                        if (address is < 0xff0000 or > 0xffffff || !memory.TryAdd(address, record[4 + i]))
                        {
                            throw new InvalidDataException("Intel HEX overlaps or is outside FF0000..FFFFFF (32-bit profile).");
                        }
                    }
                    break;
                case 1:
                    if (record[0] != 0 || offset != 0)
                    {
                        throw new InvalidDataException("Invalid EOF record.");
                    }
                    ended = true;
                    break;
                case 2:
                case 4:
                    if (record[0] != 2 || offset != 0)
                    {
                        throw new InvalidDataException("Invalid extended address.");
                    }
                    high = (uint)(record[4] << 8 | record[5]) << (record[3] == 2 ? 4 : 16);
                    break;
                default:
                    throw new InvalidDataException($"Unsupported HEX record type {record[3]}.");
            }
        }
        if (!ended || memory.Count == 0 || memory.First().Key != 0xff0000)
        {
            throw new InvalidDataException("Missing EOF, image or reset entry.");
        }
        var data = new byte[memory.Last().Key - 0xff0000 + 1];
        Array.Fill(data, (byte)255);
        foreach (var pair in memory)
        {
            data[pair.Key - 0xff0000] = pair.Value;
        }
        return new(data, sha256, 0xff0000);
    }
}
