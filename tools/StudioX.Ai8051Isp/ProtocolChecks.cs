using System.Buffers.Binary;
using System.Text.Json;

namespace StudioX.Ai8051Isp;

internal static class ProtocolChecks
{
    public static object Verify(string capture, string hex)
    {
        var image = FirmwareImage.Load(hex);
        var transmitted = new Dictionary<int, byte>();
        var completed = new HashSet<int>();
        int reports = 0, replies = 0, blocks = 0, checks = 0;
        byte[]? pending = null;
        foreach (string line in File.ReadLines(capture))
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            string kind = root.GetProperty("kind").GetString()!;
            if (kind is "capture_warning" or "agent_error" or "io_error" or "pending_at_close")
            {
                throw new InvalidDataException("Capture contains I/O errors.");
            }
            if (kind == "tx_complete")
            {
                completed.Add(root.GetProperty("id").GetInt32());
                continue;
            }
            if (kind is not ("tx_submit" or "rx"))
            {
                continue;
            }
            byte[] packet = Convert.FromHexString(root.GetProperty("hex").GetString()!);
            if (kind == "rx")
            {
                BridgeProtocol.Decode(packet);
                replies++;
                continue;
            }
            reports++;
            uint offset = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(3));
            byte[] rebuilt = BridgeProtocol.Encode(packet[2], packet.AsSpan(8, packet[7]), offset);
            // 官方空闲末包 len=0，却带遗留缓冲区字节；只审计，不将其作为下载命令。
            if (packet[2] == 0x40 && packet[7] == 0)
            {
                continue;
            }
            if (!packet.SequenceEqual(rebuilt))
            {
                throw new InvalidDataException("Transmit framing differs from captured packet.");
            }
            if (packet[2] == 0x44)
            {
                if (offset == 0 && packet[7] == 56)
                {
                    if (pending != null)
                    {
                        throw new InvalidDataException("Unfinished block.");
                    }
                    pending = new byte[69];
                    packet.AsSpan(8, 56).CopyTo(pending);
                }
                else if (offset == 56 && packet[7] == 13 && pending != null)
                {
                    packet.AsSpan(8, 13).CopyTo(pending.AsSpan(56));
                }
                else
                {
                    throw new InvalidDataException("Unexpected block segmentation.");
                }
            }
            if (packet[2] == 0x45)
            {
                if (pending == null)
                {
                    throw new InvalidDataException("Commit without a block.");
                }
                int address = BinaryPrimitives.ReadUInt16BigEndian(pending.AsSpan(1));
                if (pending[0] != (address == 0 ? 0x22 : 2) || pending[3] != 0x5a || pending[4] != 0xa5)
                {
                    throw new InvalidDataException("Invalid write opcode.");
                }
                for (int i = 0; i < 64; i++)
                {
                    if (!transmitted.TryAdd(address + i, pending[5 + i]))
                    {
                        throw new InvalidDataException("Repeated write address.");
                    }
                }
                pending = null;
                blocks++;
            }
        }
        if (completed.Count != reports || pending != null || transmitted.Count != (image.Code.Length + 63) / 64 * 64)
        {
            throw new InvalidDataException("Incomplete capture.");
        }
        foreach (var pair in transmitted)
        {
            if (pair.Value != (pair.Key < image.Code.Length ? image.Code[pair.Key] : 255))
            {
                throw new InvalidDataException("Transmitted firmware differs from HEX.");
            }
        }
        void Reject(Action action)
        {
            try
            {
                action();
            }
            catch (Exception e) when (e is InvalidDataException or ArgumentOutOfRangeException) { checks++; return; }
            throw new Exception("Invalid input unexpectedly accepted.");
        }
        var valid = new byte[65];
        valid[1] = 0xa5;
        valid[3] = 1;
        valid[4] = 1;
        valid[64] = 0x59;
        if (!BridgeProtocol.Decode(valid).SequenceEqual(new byte[] { 1 }))
        {
            throw new Exception("ACK decode failed.");
        }
        checks++;
        Reject(() => BridgeProtocol.Decode(valid[..64]));
        var bad = valid.ToArray();
        bad[4] ^= 1;
        Reject(() => BridgeProtocol.Decode(bad));
        bad = valid.ToArray();
        bad[1] = 0;
        Reject(() => BridgeProtocol.Decode(bad));
        bad = valid.ToArray();
        bad[3] = 61;
        bad[64] = unchecked((byte)-bad.Take(64).Sum(x => x));
        Reject(() => BridgeProtocol.Decode(bad));
        Reject(() => BridgeProtocol.Encode(0x44, new byte[57]));
        string text = File.ReadAllText(hex);
        Reject(() => FirmwareImage.Parse(text.Replace(":0200000400FFFB", ":0200000400FFFA")));
        Reject(() => FirmwareImage.Parse(":020000040000FA\n:0100000000FF\n:00000001FF"));
        Reject(() => FirmwareImage.Parse(":0200000400FFFB\n:0100000000FF\n:0100000000FF\n:00000001FF"));
        Reject(() => FirmwareImage.Parse(":0200000400FFFB\n:0100000000FF"));
        Reject(() => FirmwareImage.Parse(":0200000400FFFB\n:0100000000FF\n:00000001FF\n:0100010000FE"));
        return new
        {
            passed = true,
            txReports = reports,
            rxReports = replies,
            blocks,
            bytes = image.Code.Length,
            framingAndBoundaryChecks = checks,
            firmwareMatches = true,
            flashReadback = false
        };
    }
}
