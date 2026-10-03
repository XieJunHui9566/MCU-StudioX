namespace StudioX.ToolchainArchiveValidation;

using System.Text;

/// <summary>构造最小无压缩 7z 边界样本，保留恶意路径和属性，不让第三方写入器自动规范化。</summary>
internal static class MinimalSevenZip
{
    internal sealed record File(string Name, byte[] Bytes, int Attributes = 0, long? Length = null, bool Encrypted = false,
        byte[]? Lzma2Properties = null, bool Anti = false);
    internal static void Write(string path, File[] files, int? declaredFileCount = null)
    {
        using var header = new MemoryStream();
        using var writer = new BinaryWriter(header);
        void Number(ulong value)
        {
            byte first = 0;
            var extra = 0;
            for (; extra < 8; extra++)
            {
                if (value < (1UL << (7 * (extra + 1)))) { first |= (byte)(value >> (8 * extra)); break; }
                first |= (byte)(0x80 >> extra);
            }
            writer.Write(first);
            for (var i = 0; i < extra; i++) writer.Write((byte)(value >> (8 * i)));
        }
        var streams = files.Where(file => file.Bytes.Length > 0).ToArray();
        writer.Write(new byte[] { 1, 4, 6 }); Number(0); Number((ulong)streams.Length);
        writer.Write((byte)9); foreach (var file in streams) Number((ulong)file.Bytes.Length); writer.Write((byte)0);
        writer.Write(new byte[] { 7, 11 }); Number((ulong)streams.Length); writer.Write((byte)0);
        foreach (var file in streams)
        {
            Number(1);
            if (file.Encrypted) writer.Write(new byte[] { 0x24, 0x06, 0xf1, 0x07, 0x01, 2, 0, 0 });
            else if (file.Lzma2Properties is { } properties) { writer.Write(new byte[] { 0x21, 0x21 }); Number((ulong)properties.Length); writer.Write(properties); }
            else writer.Write(new byte[] { 1, 0 });
        }
        writer.Write((byte)12); foreach (var file in streams) Number((ulong)(file.Length ?? file.Bytes.Length));
        writer.Write(new byte[] { 0, 8, 0, 0, 5 }); Number((ulong)(declaredFileCount ?? files.Length));
        void Bits(byte id, bool[] bits)
        {
            var data = new byte[(bits.Length + 7) / 8];
            for (var i = 0; i < bits.Length; i++) if (bits[i]) data[i / 8] |= (byte)(0x80 >> (i % 8));
            writer.Write(id); Number((ulong)data.Length); writer.Write(data);
        }
        var empty = files.Where(file => file.Bytes.Length == 0).ToArray();
        if (empty.Length > 0)
        {
            Bits(14, files.Select(file => file.Bytes.Length == 0).ToArray());
            Bits(15, Enumerable.Repeat(true, empty.Length).ToArray());
            Bits(16, empty.Select(file => file.Anti).ToArray());
        }
        var names = Encoding.Unicode.GetBytes(string.Join('\0', files.Select(f => f.Name)) + '\0');
        writer.Write((byte)17); Number((ulong)names.Length + 1); writer.Write((byte)0); writer.Write(names);
        writer.Write((byte)21); Number((ulong)(2 + files.Length * 4)); writer.Write(new byte[] { 1, 0 });
        foreach (var file in files) writer.Write(file.Attributes);
        writer.Write(new byte[] { 0, 0 });
        WriteContainer(path, files.SelectMany(file => file.Bytes).ToArray(), header.ToArray());
    }
    internal static void EncodedHeaderBomb(string path)
    {
        // 单个 Copy 头流宣称展开 1 GiB；守卫必须在任何解码分配之前拒绝。
        WriteContainer(path, [1], [0x17, 6, 0, 1, 9, 1, 0, 7, 11, 1, 0, 1, 1, 0, 12, 0xf0, 0, 0, 0, 0x40, 0, 8, 0, 0]);
    }
    private static void WriteContainer(string path, byte[] payload, byte[] nextHeader)
    {
        using var start = new MemoryStream();
        using (var startWriter = new BinaryWriter(start, Encoding.UTF8, true))
        {
            startWriter.Write((ulong)payload.Length);
            startWriter.Write((ulong)nextHeader.Length);
            startWriter.Write(Crc(nextHeader));
        }
        using var output = System.IO.File.Create(path);
        using var outputWriter = new BinaryWriter(output);
        outputWriter.Write(new byte[] { 0x37, 0x7a, 0xbc, 0xaf, 0x27, 0x1c, 0, 4 });
        outputWriter.Write(Crc(start.ToArray())); outputWriter.Write(start.ToArray());
        outputWriter.Write(payload);
        outputWriter.Write(nextHeader);
    }
    private static uint Crc(byte[] bytes)
    {
        var value = uint.MaxValue;
        foreach (var item in bytes)
        {
            value ^= item;
            for (var bit = 0; bit < 8; bit++) value = (value >> 1) ^ (0xedb88320U & (uint)-(int)(value & 1));
        }
        return ~value;
    }
}
