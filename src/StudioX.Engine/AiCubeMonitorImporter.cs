namespace StudioX.Engine;

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using StudioX.Foundation;

/// <summary>静态读取用户提供的已核对厂商 EXE，不执行该程序或加载其 DLL。</summary>
public static class AiCubeMonitorImporter
{
    public static Task<byte[]> ReadHexAsync(string executable, CancellationToken token = default) => ReadAsync(executable, false, token);
    public static Task<byte[]> ReadSetupImageAsync(string executable, CancellationToken token = default) => ReadAsync(executable, true, token);

    private static async Task<byte[]> ReadAsync(string executable, bool setup, CancellationToken token)
    {
        var info = new FileInfo(executable);
        if (!info.Exists || info.Length != 10395648)
        {
            throw Invalid("请选择已核对的 AiCube-ISP-v6.96V-plus.exe。");
        }
        var bytes = await File.ReadAllBytesAsync(executable, token);
        if (StcMonitorImage.Hash(bytes) != StcMonitorImage.VendorExecutableSha256)
        {
            throw Invalid("厂商程序 SHA-256 不匹配；尚未适配此版本，不能猜测资源或安装监控。");
        }
        var table = bytes.AsSpan(0x12f998, 256).ToArray();
        var inverse = bytes.AsSpan(0x12f898, 256).ToArray();
        if (table.Distinct().Count() != 256 || Enumerable.Range(0, 256).Any(i => inverse[table[i]] != i))
        {
            throw Invalid("厂商固件编码表无效。");
        }
        var monitor = Decode(ReadEntry(bytes, "res6.bin"), table);
        var reset = Decode(ReadEntry(bytes, "res2.bin"), table);
        var hex = StcMonitorImage.CreateHex(monitor, reset);
        if (!setup)
        {
            return hex;
        }
        // 原厂制作通道发送连续编码镜像；F449 加 02，状态尾部高半字节非 50 时另加 10。
        var plain = Enumerable.Repeat((byte)0xff, 0xf000).ToArray();
        reset.CopyTo(plain, 0);
        monitor.CopyTo(plain, 0xe000);
        plain[^1] = 0x12;
        var result = new byte[plain.Length];
        byte state = 0x4c, counter = 0;
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = inverse[(byte)((byte)(inverse[plain[i]] - table[(byte)(state + 1)]) ^ table[state])];
            state = unchecked((byte)((plain[i] ^ state) + counter));
            counter = unchecked((byte)(counter + 1));
        }
        return result;
    }

    private static byte[] Decode(byte[] encoded, byte[] table)
    {
        var result = new byte[encoded.Length];
        byte state = 0x4c, counter = 0;
        for (var i = 0; i < result.Length; i++)
        {
            var value = table[(byte)((table[encoded[i]] ^ table[state]) + table[(byte)(state + 1)])];
            result[i] = value;
            state = unchecked((byte)((value ^ state) + counter));
            counter = unchecked((byte)(counter + 1));
        }
        return result;
    }

    private static byte[] ReadEntry(byte[] executable, string name)
    {
        for (var at = 0; at + 30 <= executable.Length; at++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(executable.AsSpan(at, 4)) != 0x04034b50)
            {
                continue;
            }
            var header = executable.AsSpan(at, 30);
            var flags = BinaryPrimitives.ReadUInt16LittleEndian(header[6..]);
            var method = BinaryPrimitives.ReadUInt16LittleEndian(header[8..]);
            var compressed = BinaryPrimitives.ReadUInt32LittleEndian(header[18..]);
            var expanded = BinaryPrimitives.ReadUInt32LittleEndian(header[22..]);
            var nameSize = BinaryPrimitives.ReadUInt16LittleEndian(header[26..]);
            var extraSize = BinaryPrimitives.ReadUInt16LittleEndian(header[28..]);
            if (at + 30L + nameSize + extraSize + compressed > executable.Length || nameSize != name.Length ||
                Encoding.ASCII.GetString(executable, at + 30, nameSize) != name)
            {
                continue;
            }
            if (flags != 11 || method is not (0 or 8) || compressed is < 12 or > 65536 || expanded > 65536)
            {
                throw Invalid("厂商 ZIP 资源结构未通过核对。");
            }
            var crypt = new ZipCipher(Convert.FromHexString("686A3132335F2360215F333431353932"));
            var packed = executable.AsSpan(at + 30 + nameSize + extraSize, (int)compressed).ToArray();
            for (var i = 0; i < packed.Length; i++)
            {
                packed[i] = crypt.Decrypt(packed[i]);
            }
            var crc = BinaryPrimitives.ReadUInt32LittleEndian(header[14..]);
            // 此归档使用数据描述符，ZipCrypto 校验字节取修改时间高字节，而非 CRC。
            var modifiedTime = BinaryPrimitives.ReadUInt16LittleEndian(header[10..]);
            if (packed[11] != (byte)(modifiedTime >> 8))
            {
                throw Invalid("厂商 ZIP 解码校验失败。");
            }
            using var input = new MemoryStream(packed, 12, packed.Length - 12);
            using var output = new MemoryStream();
            if (method == 0)
            {
                input.CopyTo(output);
            }
            else
            {
                using var inflater = new DeflateStream(input, CompressionMode.Decompress);
                inflater.CopyTo(output);
            }
            var result = output.ToArray();
            if (result.Length != expanded || ZipCipher.Checksum(result) != crc)
            {
                throw Invalid("厂商 ZIP 资源长度或 CRC 不一致。");
            }
            return result;
        }
        throw Invalid("厂商 EXE 缺少所需监控资源。");
    }

    private sealed class ZipCipher
    {
        private uint key0 = 0x12345678, key1 = 0x23456789, key2 = 0x34567890;
        internal ZipCipher(byte[] password)
        {
            foreach (var value in password)
            {
                Update(value);
            }
        }
        internal byte Decrypt(byte value)
        {
            var temp = key2 | 2;
            var decoded = (byte)(value ^ unchecked(temp * (temp ^ 1) >> 8));
            Update(decoded);
            return decoded;
        }
        private void Update(byte value)
        {
            key0 = Crc(key0, value);
            key1 = unchecked((key1 + (byte)key0) * 134775813 + 1);
            key2 = Crc(key2, (byte)(key1 >> 24));
        }
        private static uint Crc(uint crc, byte value)
        {
            crc ^= value;
            for (var i = 0; i < 8; i++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xedb88320 : crc >> 1;
            }
            return crc;
        }
        internal static uint Checksum(byte[] bytes)
        {
            var crc = uint.MaxValue;
            foreach (var value in bytes)
            {
                crc = Crc(crc, value);
            }
            return ~crc;
        }
    }
    private static StudioXException Invalid(string message) => new("MON51_VENDOR_IMAGE", message);
}
