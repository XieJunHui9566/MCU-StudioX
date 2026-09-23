namespace StudioX.Application.Serial;

/// <summary>按功能码长度与 CRC 重组字节流。宿主空闲超时仅用于收尾，不能当作线上 t3.5 测量。</summary>
public sealed class ModbusRtuDecoder(ModbusRole role = ModbusRole.PcMaster)
{
    private sealed class StreamState
    {
        public readonly List<byte> Bytes = [];
        public DateTimeOffset Time;
    }
    private readonly StreamState rx = new(), tx = new();
    public static ushort Crc(ReadOnlySpan<byte> bytes)
    {
        ushort value = 0xffff;
        foreach (var b in bytes)
        {
            value ^= b;
            for (var bit = 0; bit < 8; bit++) value = (ushort)((value >> 1) ^ ((value & 1) != 0 ? 0xa001 : 0));
        }
        return value;
    }
    public static byte[] WithCrc(ReadOnlySpan<byte> payload)
    {
        if (payload.Length is < 2 or > 254) throw new ArgumentException("RTU 地址和 PDU 应为 2–254 字节（不含 CRC）。");
        var result = new byte[payload.Length + 2]; payload.CopyTo(result);
        var crc = Crc(payload); result[^2] = (byte)crc; result[^1] = (byte)(crc >> 8); return result;
    }
    public void Reset() { rx.Bytes.Clear(); tx.Bytes.Clear(); }
    public IReadOnlyList<ProtocolFrame> Feed(SerialRecord record, bool flush = false)
    {
        if (record.Boundary) { var all = Flush(); Reset(); return all; }
        var s = record.Transmit ? tx : rx;
        var result = new List<ProtocolFrame>();
        foreach (var b in record.Data)
        {
            if (s.Bytes.Count == 0) s.Time = record.Time;
            s.Bytes.Add(b);
            // 合法 ADU 最大 256 字节。有限缓冲同时允许坏帧后寻找下一帧。
            if (s.Bytes.Count >= 512)
            {
                Extract(s, record.Transmit, false, result);
                if (s.Bytes.Count >= 512) Extract(s, record.Transmit, true, result);
            }
        }
        Extract(s, record.Transmit, flush, result); return result;
    }
    public IReadOnlyList<ProtocolFrame> Flush()
    {
        var result = new List<ProtocolFrame>(); Extract(rx, false, true, result); Extract(tx, true, true, result); return result;
    }
    private bool? Request(bool transmit) => role == ModbusRole.Monitor ? null : role == ModbusRole.PcMaster ? transmit : !transmit;
    private static bool Good(ReadOnlySpan<byte> b) => b.Length >= 4 && Crc(b[..^2]) == (b[^2] | b[^1] << 8);
    private static IEnumerable<int> Lengths(byte[] b, int offset, bool? request)
    {
        var count = b.Length - offset; if (count < 2) yield break;
        var f = b[offset + 1];
        if ((f & 0x80) != 0) { yield return 5; yield break; }
        if (request != false)
        {
            var size = f switch
            {
                1 or 2 or 3 or 4 or 5 or 6 or 8 => 8,
                7 or 11 or 12 or 17 => 4,
                15 or 16 when count >= 7 => 9 + b[offset + 6],
                22 => 10,
                23 when count >= 11 => 13 + b[offset + 10],
                _ => 0
            };
            if (size is >= 4 and <= 256) yield return size;
        }
        if (request != true)
        {
            var size = f switch
            {
                1 or 2 or 3 or 4 or 12 or 17 or 23 when count >= 3 => 5 + b[offset + 2],
                5 or 6 or 8 or 11 or 15 or 16 => 8,
                7 => 5, 22 => 10, _ => 0
            };
            if (size is >= 4 and <= 256) yield return size;
        }
    }
    private void Extract(StreamState state, bool transmit, bool flush, List<ProtocolFrame> output)
    {
        while (state.Bytes.Count > 0)
        {
            var bytes = state.Bytes.ToArray(); var request = Request(transmit);
            var sizes = Lengths(bytes, 0, request).Distinct().ToArray();
            var valid = sizes.FirstOrDefault(n => n <= bytes.Length && Good(bytes.AsSpan(0, n)));
            if (valid > 0) { Emit(valid, null); continue; }
            // 损坏的前缀不能吞掉后续完整且通过校验的帧。
            var skip = 0;
            for (var i = 1; i <= bytes.Length - 4; i++)
            {
                if (bytes[i] > 247) continue;
                if (Lengths(bytes, i, request).Any(n => n <= bytes.Length - i && Good(bytes.AsSpan(i, n)))) { skip = i; break; }
            }
            if (skip > 0) { Emit(skip, "无效前缀 / 重新同步"); continue; }
            if (!flush) break;
            // 未支持的功能码只在空闲/连接边界按原始帧显示，不能猜测字段。
            if (bytes.Length is >= 4 and <= 256 && Good(bytes))
            { Emit(bytes.Length, sizes.Length == 0 ? null : "长度与所选主从方向不匹配（CRC 正确）"); continue; }
            var length = sizes.Where(n => n <= bytes.Length).DefaultIfEmpty(Math.Min(bytes.Length, 256)).Max();
            Emit(length, length < 4 || sizes.All(n => n > bytes.Length) && sizes.Length > 0 ? "不完整帧" : "CRC 错误 / 帧边界未确认");
            void Emit(int count, string? error)
            {
                output.Add(Describe(bytes.AsSpan(0, count).ToArray(), state.Time, transmit, request, error));
                state.Bytes.RemoveRange(0, count);
            }
        }
    }
    private static string FunctionName(int f) => f switch
    {
        1 => "读线圈", 2 => "读离散输入", 3 => "读保持寄存器", 4 => "读输入寄存器",
        5 => "写单线圈", 6 => "写单寄存器", 7 => "读异常状态", 8 => "诊断",
        11 => "通信事件计数", 12 => "通信事件日志", 15 => "写多个线圈", 16 => "写多个寄存器",
        17 => "报告服务器 ID", 22 => "掩码写寄存器", 23 => "读写多个寄存器", _ => "未展开功能"
    };
    private static ProtocolFrame Describe(byte[] b, DateTimeOffset time, bool tx, bool? request, string? error)
    {
        var fields = new List<ProtocolField>(); var f = b.Length >= 2 ? b[1] & 0x7f : 0;
        var title = FunctionName(f); var kind = request is true ? "请求" : request is false ? "响应" : "监听 / 方向推断";
        var status = error ?? "CRC 正确";
        fields.Add(new("时间来源", "宿主 UTC；不代表线上字节间隔"));
        fields.Add(new("帧类型", kind)); fields.Add(new("长度", b.Length + " B"));
        if (b.Length > 0) fields.Add(new("站号", b[0] == 0 ? "0（广播，不应响应）" : b[0].ToString()));
        if (b.Length >= 4)
        {
            fields.Add(new("CRC 接收值", $"0x{(b[^2] | b[^1] << 8):X4}"));
            fields.Add(new("CRC 计算值", $"0x{Crc(b.AsSpan(0, b.Length - 2)):X4}（低字节先传）"));
        }
        void Warn(string text) { status = "字段异常"; fields.Add(new("警告", text)); }
        int Word(int offset) => b[offset] << 8 | b[offset + 1];
        void Address(int at = 2) => fields.Add(new("PDU 地址（从 0 起）", $"0x{Word(at):X4} / {Word(at)}"));
        if (error is null && b.Length >= 4)
        {
            if (b[0] > 247) Warn("站号超出 0–247。");
            if (b[0] == 0 && request == false) Warn("广播地址不应返回响应。");
            if ((b[1] & 0x80) != 0 && b.Length == 5)
            {
                title += " · 异常响应"; status = "异常响应";
                fields.Add(new("异常码", $"0x{b[2]:X2} · " + (b[2] switch { 1 => "非法功能", 2 => "非法数据地址", 3 => "非法数据值", 4 => "设备故障", 5 => "确认", 6 => "设备忙", 8 => "存储奇偶校验错误", 10 => "网关路径不可用", 11 => "网关目标无响应", _ => "厂商定义 / 未知" })));
            }
            else if (f is 1 or 2 or 3 or 4)
            {
                var isRequest = request ?? (b.Length == 8 && b.Length != 5 + b[2]);
                if (isRequest && b.Length == 8)
                {
                    Address(); var count = Word(4); fields.Add(new("数量", count.ToString()));
                    title += $" · 地址 {Word(2)}，数量 {count}";
                    if (count < 1 || count > (f <= 2 ? 2000 : 125) || Word(2) + count > 65536) Warn("请求数量或地址范围无效。");
                }
                else if (!isRequest && b.Length == 5 + b[2])
                {
                    fields.Add(new("数据字节数", b[2].ToString()));
                    if (b[2] is 0 or > 250) Warn("读响应的数据字节数应为 1–250。");
                    if (f <= 2) fields.Add(new("位状态（各字节 bit0 先，尾部可能填充）", string.Join(' ', b.Skip(3).Take(b[2]).Select(v => new string(Enumerable.Range(0, 8).Select(i => (v & 1 << i) == 0 ? '0' : '1').ToArray())))));
                    else
                    {
                        if (b[2] == 0 || b[2] > 250 || b[2] % 2 != 0) Warn("寄存器数据长度应为 2–250 的偶数。");
                        for (var i = 3; i + 1 < b.Length - 2; i += 2) fields.Add(new($"寄存器 +{(i - 3) / 2}", $"0x{Word(i):X4} · UInt16 {Word(i)} · Int16 {unchecked((short)Word(i))}"));
                    }
                    fields.Add(new("地址说明", "响应不含起始地址；此处 +N 为响应内偏移。"));
                }
                else Warn("长度与所选主从方向不匹配。");
            }
            else if (f is 5 or 6 && b.Length == 8)
            {
                Address(); fields.Add(new("写入值", f == 5 ? $"0x{Word(4):X4} · {(Word(4) == 0xff00 ? "ON" : Word(4) == 0 ? "OFF" : "非法线圈值")}" : $"0x{Word(4):X4} / {Word(4)}"));
                if (f == 5 && Word(4) is not (0 or 0xff00)) Warn("线圈值必须为 FF00 或 0000。");
                if (request is null) fields.Add(new("方向说明", "写单点请求与响应内容相同，监听时无法仅凭帧区分。"));
            }
            else if (f is 15 or 16 && b.Length >= 8)
            {
                Address(); var count = Word(4); fields.Add(new("写入数量", count.ToString()));
                var isRequest = request ?? b.Length != 8;
                if (count < 1 || count > (f == 15 ? 1968 : 123) || Word(2) + count > 65536) Warn("写入数量或地址范围无效。");
                if (isRequest && b.Length >= 9)
                {
                    if (b[6] != (f == 15 ? (count + 7) / 8 : count * 2)) Warn("字节数与写入数量不一致。");
                    if (f == 16) for (var i = 7; i + 1 < b.Length - 2; i += 2) fields.Add(new($"寄存器 +{(i - 7) / 2}", $"0x{Word(i):X4} / {Word(i)}"));
                    else fields.Add(new("线圈数据（bit0 先）", Convert.ToHexString(b.AsSpan(7, b.Length - 9))));
                }
            }
            else if (f == 22 && b.Length == 10) { Address(); fields.Add(new("AND 掩码", $"0x{Word(4):X4}")); fields.Add(new("OR 掩码", $"0x{Word(6):X4}")); }
            else if (f == 23 && request == true && b.Length >= 13)
            {
                fields.Add(new("读地址 / 数量", $"{Word(2)} / {Word(4)}")); fields.Add(new("写地址 / 数量", $"{Word(6)} / {Word(8)}"));
                fields.Add(new("写入数据", Convert.ToHexString(b.AsSpan(11, b.Length - 13))));
                if (Word(4) is < 1 or > 125 || Word(8) is < 1 or > 121 || b[10] != Word(8) * 2) Warn("读写数量或字节数无效。");
            }
            else fields.Add(new("PDU 数据", Convert.ToHexString(b.AsSpan(2, b.Length - 4))));
        }
        if (error is not null) fields.Add(new("诊断", error + "；检查波特率、校验位、主从方向与空闲收尾时间。"));
        return new(time, tx ? "TX" : "RX", "Modbus RTU", b.Length > 0 ? b[0].ToString() : "—",
            b.Length > 1 ? $"0x{b[1]:X2}" : "—", status, title, string.Join(' ', b.Select(v => v.ToString("X2"))), fields);
    }
}
