namespace StudioX.Engine;

using System.Buffers.Binary;
using SharpCompress.Compressors.LZMA;
using StudioX.Foundation;

/// <summary>在第三方解析器分配数组和字典之前限制 7z 头、文件数及解码资源。</summary>
internal static class SevenZipHeaderGuard
{
    internal const int MaximumHeaderBytes = 64 * 1024 * 1024;
    internal const int MaximumDictionaryBytes = 64 * 1024 * 1024;
    internal static void Validate(Stream stream, CancellationToken token)
    {
        stream.Position = 0;
        Span<byte> start = stackalloc byte[32];
        stream.ReadExactly(start);
        var offset = BinaryPrimitives.ReadUInt64LittleEndian(start[12..]);
        var size = BinaryPrimitives.ReadUInt64LittleEndian(start[20..]);
        if (size > MaximumHeaderBytes || offset > (ulong)(stream.Length - 32) || size > (ulong)(stream.Length - 32) - offset)
        {
            throw SizeError();
        }
        stream.Position = 32 + (long)offset;
        var bytes = new byte[(int)size];
        stream.ReadExactly(bytes);
        var reader = new Header(bytes, token);
        var type = reader.Byte();
        if (type == 0x17)
        {
            var encoded = reader.Streams(MaximumHeaderBytes);
            if (encoded.Folders.Length != 1 || encoded.Folders[0].Coders != 1 || encoded.Sizes.Length != 1 || encoded.Sizes[0] > MaximumHeaderBytes ||
                encoded.Position > (ulong)(stream.Length - 32) || encoded.Sizes[0] > (ulong)(stream.Length - 32) - encoded.Position)
            {
                throw SizeError();
            }
            var folder = encoded.Folders[0];
            var packed = new byte[(int)encoded.Sizes[0]];
            stream.Position = 32 + (long)encoded.Position;
            stream.ReadExactly(packed);
            using var input = new MemoryStream(packed, false);
            var unpacked = new byte[(int)folder.Size];
            using var decoded = folder.Method == 0 ? (Stream)input : LzmaStream.Create(folder.Properties, input,
                input.Length, unpacked.Length, presetDictionary: null, isLzma2: folder.Method == 0x21, leaveOpen: true);
            for (var copied = 0; copied < unpacked.Length;)
            {
                token.ThrowIfCancellationRequested();
                var read = decoded.Read(unpacked, copied, Math.Min(131072, unpacked.Length - copied));
                if (read == 0)
                {
                    throw new StudioXException("TOOLS_ARCHIVE", "7z 压缩头不完整。");
                }
                copied += read;
            }
            if (decoded.ReadByte() != -1)
            {
                throw SizeError();
            }
            reader = new Header(unpacked, token);
            type = reader.Byte();
        }
        if (type != 1)
        {
            throw ProfileError();
        }
        type = reader.Byte();
        if (type == 2)
        {
            while (reader.Byte() != 0)
            {
                reader.Skip(reader.Number());
            }
            type = reader.Byte();
        }
        // 格式 1 无外部属性流；允许有界 LZMA 字典及固定大小的分支/Delta 过滤器。
        if (type == 3)
        {
            throw ProfileError();
        }
        if (type == 4)
        {
            _ = reader.Streams(ToolchainArchive.MaximumBytes);
            type = reader.Byte();
        }
        if (type != 5)
        {
            throw ProfileError();
        }
        _ = reader.Count();
        while ((type = reader.Byte()) != 0)
        {
            reader.Skip(reader.Number());
        }
        if (reader.Byte() != 0 || !reader.AtEnd)
        {
            throw ProfileError();
        }
        stream.Position = 0;
    }

    private static StudioXException SizeError() => new("TOOLS_ARCHIVE_SIZE", "7z 头、文件数、展开大小或解码字典超出限制。");
    private static StudioXException ProfileError() => new("TOOLS_ARCHIVE_FORMAT", "不支持此 7z 组件压缩配置；请使用 MCU StudioX 的组件制作脚本重新打包。");
    private sealed record Folder(ulong Method, byte[] Properties, ulong Size, bool Crc, int Coders, int Output);
    private sealed record StreamsInfo(ulong Position, ulong[] Sizes, Folder[] Folders);
    private sealed class Header(byte[] bytes, CancellationToken token)
    {
        private int position;
        internal bool AtEnd => position == bytes.Length;
        internal byte Byte()
        {
            token.ThrowIfCancellationRequested();
            if (position >= bytes.Length)
            {
                throw new StudioXException("TOOLS_ARCHIVE", "7z 头不完整。");
            }
            return bytes[position++];
        }
        internal ulong Number()
        {
            var first = Byte();
            ulong value = 0;
            for (var i = 0; i < 8; i++)
            {
                var mask = 0x80 >> i;
                if ((first & mask) == 0)
                {
                    return value | ((ulong)(first & (mask - 1)) << (8 * i));
                }
                value |= (ulong)Byte() << (8 * i);
            }
            return value;
        }
        internal int Count()
        {
            var count = Number();
            if (count > ToolchainArchive.MaximumFiles)
            {
                throw SizeError();
            }
            return (int)count;
        }
        internal void Skip(ulong count)
        {
            if (count > (ulong)(bytes.Length - position))
            {
                throw new StudioXException("TOOLS_ARCHIVE", "7z 头数据越界。");
            }
            position += (int)count;
        }
        private byte[] Read(int count)
        {
            var start = position;
            Skip((ulong)count);
            return bytes.AsSpan(start, count).ToArray();
        }
        private bool[] Digests(int count)
        {
            var all = Byte();
            if (all > 1)
            {
                throw ProfileError();
            }
            var flags = all == 1 ? [] : Read((count + 7) / 8);
            var defined = new bool[count];
            for (var i = 0; i < count; i++)
            {
                if (defined[i] = all == 1 || (flags[i / 8] & (0x80 >> (i % 8))) != 0)
                {
                    Skip(4);
                }
            }
            return defined;
        }
        internal StreamsInfo Streams(long maximumBytes)
        {
            if (Byte() != 6)
            {
                throw ProfileError();
            }
            var packPosition = Number();
            var packCount = Count();
            if (packCount == 0 || Byte() != 9)
            {
                throw ProfileError();
            }
            var sizes = new ulong[packCount];
            for (var i = 0; i < sizes.Length; i++)
            {
                sizes[i] = Number();
            }
            var type = Byte();
            if (type == 10)
            {
                _ = Digests(packCount);
                type = Byte();
            }
            if (type != 0 || Byte() != 7 || Byte() != 11)
            {
                throw ProfileError();
            }
            var count = Count();
            if (count != packCount || Byte() != 0)
            {
                throw ProfileError();
            }
            var folders = new Folder[count];
            for (var i = 0; i < count; i++)
            {
                var coders = Number();
                if (coders is not (1 or 2))
                {
                    throw ProfileError();
                }
                ulong codec = 0;
                byte[] codecProperties = [];
                var codecs = 0;
                var filters = 0;
                for (var coder = 0; coder < (int)coders; coder++)
                {
                    var flags = Byte();
                    var methodSize = flags & 15;
                    if (methodSize is 0 or > 8 || (flags & 0xc0) != 0)
                    {
                        throw ProfileError();
                    }
                    ulong method = 0;
                    for (var j = 0; j < methodSize; j++)
                    {
                        method = (method << 8) | Byte();
                    }
                    if (method == 0x06f10701)
                    {
                        throw new StudioXException("TOOLS_ARCHIVE_ENCRYPTED", "开发环境组件不能使用加密或带密码的归档。");
                    }
                    var filter = method is 3 or 0x0a or 0x0b or 0x03030103 or 0x03030205 or 0x03030401 or 0x03030501 or 0x03030701 or 0x03030805;
                    if (!filter && method is not (0 or 0x030101 or 0x21))
                    {
                        throw ProfileError();
                    }
                    if ((flags & 0x10) != 0 && (Number() != 1 || Number() != 1))
                    {
                        throw ProfileError();
                    }
                    var propertyCount = (flags & 0x20) != 0 ? Number() : 0;
                    if (propertyCount > 5)
                    {
                        throw ProfileError();
                    }
                    var properties = Read((int)propertyCount);
                    ulong dictionary;
                    if (method == 0x21)
                    {
                        if (properties.Length != 1 || properties[0] > 40)
                        {
                            throw ProfileError();
                        }
                        dictionary = properties[0] == 40 ? uint.MaxValue : (ulong)(2 | (properties[0] & 1)) << ((properties[0] >> 1) + 11);
                    }
                    else if (method == 0x030101)
                    {
                        if (properties.Length != 5 || properties[0] >= 225)
                        {
                            throw ProfileError();
                        }
                        dictionary = BinaryPrimitives.ReadUInt32LittleEndian(properties.AsSpan(1));
                    }
                    else if (filter)
                    {
                        if (method == 3 ? properties.Length != 1 : properties.Length is not (0 or 4))
                        {
                            throw ProfileError();
                        }
                        filters++;
                        dictionary = 0;
                    }
                    else
                    {
                        if (properties.Length != 0)
                        {
                            throw ProfileError();
                        }
                        dictionary = 0;
                    }
                    if (dictionary > MaximumDictionaryBytes)
                    {
                        throw SizeError();
                    }
                    if (!filter)
                    {
                        codec = method;
                        codecProperties = properties;
                        codecs++;
                    }
                }
                if (codecs != 1 || filters != (int)coders - 1)
                {
                    throw ProfileError();
                }
                var output = 0;
                if (coders == 2)
                {
                    var boundInput = Number();
                    var boundOutput = Number();
                    if (boundInput > 1 || boundOutput > 1 || boundInput == boundOutput)
                    {
                        throw ProfileError();
                    }
                    output = 1 - (int)boundOutput;
                }
                folders[i] = new(codec, codecProperties, 0, false, (int)coders, output);
            }
            if (Byte() != 12)
            {
                throw ProfileError();
            }
            ulong total = 0;
            for (var i = 0; i < count; i++)
            {
                ulong size = 0;
                for (var coder = 0; coder < folders[i].Coders; coder++)
                {
                    var unpackSize = Number();
                    if (unpackSize > (ulong)maximumBytes)
                    {
                        throw SizeError();
                    }
                    if (coder == folders[i].Output)
                    {
                        size = unpackSize;
                    }
                }
                if (size > (ulong)maximumBytes - total)
                {
                    throw SizeError();
                }
                total += size;
                folders[i] = folders[i] with
                {
                    Size = size
                };
            }
            type = Byte();
            if (type == 10)
            {
                var defined = Digests(count);
                for (var i = 0; i < count; i++)
                {
                    folders[i] = folders[i] with
                    {
                        Crc = defined[i]
                    };
                }
                type = Byte();
            }
            if (type != 0)
            {
                throw ProfileError();
            }
            type = Byte();
            if (type == 8)
            {
                var streams = Enumerable.Repeat(1, count).ToArray();
                type = Byte();
                if (type == 13)
                {
                    var totalStreams = 0;
                    for (var i = 0; i < count; i++)
                    {
                        streams[i] = Count();
                        if (streams[i] > ToolchainArchive.MaximumFiles - totalStreams)
                        {
                            throw SizeError();
                        }
                        totalStreams += streams[i];
                    }
                    type = Byte();
                }
                if (type == 9)
                {
                    for (var i = 0; i < count; i++)
                    {
                        ulong subTotal = 0;
                        for (var j = 1; j < streams[i]; j++)
                        {
                            var size = Number();
                            if (size > folders[i].Size - subTotal)
                            {
                                throw SizeError();
                            }
                            subTotal += size;
                        }
                    }
                    type = Byte();
                }
                if (type == 10)
                {
                    var digests = Enumerable.Range(0, count).Sum(i => streams[i] == 1 && folders[i].Crc ? 0 : streams[i]);
                    _ = Digests(digests);
                    type = Byte();
                }
                if (type != 0)
                {
                    throw ProfileError();
                }
                type = Byte();
            }
            if (type != 0)
            {
                throw ProfileError();
            }
            return new(packPosition, sizes, folders);
        }
    }
}
