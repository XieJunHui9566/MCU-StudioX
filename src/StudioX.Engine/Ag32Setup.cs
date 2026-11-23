namespace StudioX.Engine;

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using StudioX.Foundation;

/// <summary>按实例保存图形参数。所有生成入口复用同一组合法取值和跨外设资源检查。</summary>
public sealed partial class Ag32Setup : IEquatable<Ag32Setup>
{
    public Dictionary<string, Dictionary<string, string>> Units { get; init; } = new(StringComparer.Ordinal);
    private static readonly Regex Marker = new(@"(?m)^[ \t]*#@StudioX:SETUP\b[^\r\n]*(?:\r?\n|\r|$)");
    private static readonly Regex UsbLine = new(@"(?im)^[ \t]*USB0[ \t]+(Device|Host|OTG)[ \t]*(?:#[^\r\n]*)?(?:\r?\n|\r|$)");
    private static readonly Lazy<Dictionary<string, Ag32SetupField[]>> Fields = new(BuildCatalog);
    public static IReadOnlyDictionary<string, Ag32SetupField[]> Catalog => Fields.Value;

    private static Dictionary<string, Ag32SetupField[]> BuildCatalog()
    {
        var fields = new Dictionary<string, Ag32SetupField[]>(StringComparer.Ordinal);
        static Ag32SetupField Pick(string key, string label, params string[] choices) => new(key, label, choices[0], choices);
        static Ag32SetupField Num(string key, string label, int value, int min, int max) =>
            new(key, label, value.ToString(CultureInfo.InvariantCulture), Minimum: min, Maximum: max);
        for (int i = 0; i < 3; i++)

        {
            fields[$"ADC{i}"] = [new("Sequence", "扫描顺序（通道号，用逗号分隔）", "0"),
                Num("Divider", "采样时钟分频寄存器（0～65535）", 15, 0, 65535), Pick("Mode", "启动方式", "软件单次", "上电单次", "连续转换", "DMA 连续")];
        }
        for (int i = 0; i < 2; i++)

        {
            fields[$"DAC{i}"] = [Num("Value", "初始输出码（0～1023）", 0, 0, 1023), Pick("Mode", "启动方式", "软件启动", "上电启动", "DMA 输出"), Num("Divider", "DMA 更新分频（0～65535）", 15, 0, 65535), Pick("Buffer", "输出缓冲", "启用", "关闭")];
        }
        for (int i = 1; i <= 2; i++)

        {
            fields[$"CMP{i}"] = [Pick("Plus","同相输入","PA1", i == 1 ? "PA4" : "PA3"),
                Pick("Minus","反相输入","PA4", i == 1 ? "PA0" : "PA2","PA5","¼ VREF","½ VREF","¾ VREF","VREF")];
        }
        for (int i = 0; i < 8; i++)

        {
            fields[$"EXT_INT{i}"] = [Num("Priority", "PLIC 优先级（1～15）", 1, 1, 15)];
        }
        for (int i = 0; i < 4; i++)

        {
            fields[$"LOCAL_INT{i}"] = [Pick("Source", "直连输入（低电平有效）", Enumerable.Range(0, 8).Select(n => $"EXT_INT{n}").ToArray())];
        }
        for (int i = 0; i < 5; i++)

        {
            fields[$"GPTIMER{i}"] = [Pick("Pulse","计数模式","重复","单脉冲"), Pick("Slave","从模式","关闭","触发启动","复位","门控"),
                Pick("Input","触发输入",["ETR", "CH0", "CH1", .. Enumerable.Range(1,4).Select(n => $"GPTIMER{(i+n)%5}_TRGO")]),
                Pick("Output","主触发输出","RESET","ENABLE","UPDATE","CC0IF","OC0REF","OC1REF","OC2REF","OC3REF")];
        }
        foreach (var unit in new[] { "SPI0", "SPI1" })

        {
            fields[unit] = [Pick("Phases","事务阶段数","1","2","3","4","5","6","7","8"),
                .. Enumerable.Range(1,8).SelectMany(n=>new[]{Pick($"Action{n}",$"阶段 {n} · 操作","TX","RX","DUMMY_TX","POLL"),
                    Pick($"Lines{n}",$"阶段 {n} · 数据线数","1","2","4"),Num($"Bytes{n}",$"阶段 {n} · 字节数",1,1,4092),
                    Num($"Mask{n}",$"阶段 {n} · 轮询掩码",1,0,255),Num($"Expect{n}",$"阶段 {n} · 期望值",0,0,255),Num($"Limit{n}",$"阶段 {n} · 尝试次数（0 持续至超时）",0,0,255)})];
        }
        fields["CAN0"] = [Pick("Mode","工作模式","正常","只听","自测"),Num("SJW","同步跳转宽度（TQ）",1,1,4),
            Pick("Samples","每位采样次数","1","3"),Pick("Filter","验收滤波器","单组","双组"),
            new("SecondId","第二组 ID（十六进制）","0"),new("SecondMask","第二组比较掩码（十六进制，1 表示比较）","7FF")];
        foreach (var unit in fields.Keys.Where(Ag32TimerInitialization.IsGeneral).ToArray())

        {
            fields[unit] = [..fields[unit],Pick("Direction","计数方向","向上","向下","中心对齐"), Num("Repeat","重复计数装载值",0,0,255),
                Num("BreakFilter","刹车输入滤波编码",0,0,15),Pick("EtrEdge","ETR 极性","正相","反相"),
                Pick("EtrDiv","ETR 分频","1","2","4","8"),Num("EtrFilter","ETR 输入滤波编码",0,0,15),
                .. Enumerable.Range(0,4).SelectMany(n => new[]{Pick($"Pol{n}",$"CH{n} 输出极性","正相","反相"),
                    Pick($"NPol{n}",$"CHN{n} 互补输出极性","正相","反相")})];
        }
        foreach (var unit in new[] { "TIMER0.1", "TIMER0.2", "TIMER1.1", "TIMER1.2" })

        {
            fields[unit] = [Pick("Mode", "计数方式", "周期", "单次", "自由运行"), Pick("Width", "计数位宽", "32", "16"), Pick("FreeDiv", "自由运行分频", "1", "16", "256")];
        }
        fields["CRC0"] = [Pick("Width","多项式位宽","32","16","8","7"),
            new("Polynomial","多项式（十六进制，省略最高项）","04C11DB7"), new("Seed","初始值（十六进制）","FFFFFFFF"),
            Pick("Reverse","输入反转单位","NONE","BYTE","HWORD","WORD"), Pick("ReverseOut","输出位反转","关闭","开启"),
            Pick("Endian","字节序","BIG","LITTLE")];
        fields["BKP"] = [Num("First", "起始备份寄存器", 0, 0, 15), Num("Last", "末尾备份寄存器", 15, 0, 15)];
        fields["RTC"] = [Pick("Clock","RTC 时钟源","LSE","LSI","LSE 旁路","LOCAL（逻辑转接 HSE）"), Num("Calibration","校准值（每 2²⁰ 个周期扣除的脉冲数）",0,0,127),
            Pick("Output","RTC 输出","关闭","校准时钟","秒脉冲","闹钟")];
        for (int i = 0; i < 5; i++)

        {
            fields[$"UART{i}"] = [Pick("RxLevel","接收 FIFO 触发水位","1/2","1/8","1/4","3/4","7/8"),
                Pick("TxLevel","发送 FIFO 触发水位","1/2","1/8","1/4","3/4","7/8"),Pick("Loop","硬件回环","关闭","开启")];
        }
        var sources = DmaRequests.Keys.ToArray();
        string[] bursts = ["1", "4", "8", "16", "32", "64", "128", "256"];
        for (int i = 0; i < 8; i++)

        {
            fields[$"DMA_CH{i}"] = [Pick("Request", "硬件请求", sources),Pick("Width","内存复制元素位宽","32","16","8"),
                Pick("SrcBurst","源突发长度（元素）",bursts),Pick("DstBurst","目标突发长度（元素）",bursts),
                Pick("Layout","缓冲区布局","连续缓冲区","分段缓冲区"),new("SegmentSizes","各段元素数（逗号分隔）","64,64"),
                Num("Blocks","连续传输容量（每单位 4092 个元素）",1,1,32),Pick("Notify","完成与错误通知","轮询","中断"),
                Pick("Target","事件更新寄存器","ARR","CCR0","CCR1","CCR2","CCR3")];
        }
        fields["IWDG"] = [Pick("Clock","运行 / Stop 时钟","LSI","LSE"),Pick("Divider","预分频","2","4","8","16","32","64","128","256"),
            Pick("Freeze","冻结计数","不停","Stop","Standby","Stop 和 Standby")];
        fields["PWR"] = [.. Enumerable.Range(0,8).Select(i => Pick($"EXT{i}", $"Stop · EXT_INT{i}","关闭","上升沿","下降沿","双边沿")),
            Pick("Alarm","Stop · RTC 闹钟","关闭","上升沿"), Pick("Standby","Standby 闹钟策略","WKUP / 复位","WKUP / RTC 闹钟 / 复位"), Num("AlarmDelay","入睡前设置闹钟（RTC 计数）",10,1,int.MaxValue)];
        fields["USB0"] = [Pick("Role","控制器角色","Device","Host"), Pick("Clock","60 MHz PHY 时钟","内部 PLL","外部 USB0_CLK"),
            .. Enumerable.Range(1,3).SelectMany(ep => new[]{"OUT","IN"}.SelectMany(dir => new[]{
                Pick($"EP{ep}{dir}",$"EP{ep} {dir} 传输","关闭","Bulk","Interrupt","Isochronous"),
                Num($"Size{ep}{dir}",$"EP{ep} {dir} 最大包长（字节）",64,1,1023)}))];
        fields["MAC0"] = [Pick("Mode","介质接口","RMII","MII"),Pick("Ref","参考时钟","PHY 输入","MCU 输出"),
            Pick("Speed","链路速率","100 Mbit/s","10 Mbit/s"),Pick("Duplex","链路双工","全双工","半双工"),
            Num("Phy","PHY 地址（0～31）",0,0,31), new("Address","本地 MAC 地址","02:00:00:00:00:01")];
        fields["DMA_IRQ"] = [Num("Complete", "完成优先级", 1, 1, 15), Num("Error", "错误优先级", 1, 1, 15)];
        fields["HyperRAM"] = [Pick("Latency", "存储器延迟模式", "固定延迟", "可变延迟")];
        fields["FLASH"] = [];
        fields["FCB0"] = [];
        fields["MAC0"] = [.. fields["MAC0"], Pick("Link", "链路协商", "固定配置", "自动协商")];
        foreach (var unit in new[] { "USB0", "MAC0", "FLASH", "FCB0" })

        {
            fields[unit] = [.. fields[unit], Num("Events", "中断事件", 0, 0, IrqEvents(unit).Sum(e => e.Mask)), Num("Priority", "PLIC 优先级", 1, 1, 15)];
        }
        return fields;
    }

    public string Value(string unit, string field) => Units.TryGetValue(unit, out var values) && values.TryGetValue(field, out var value)
        ? value : Catalog[unit].Single(f => f.Key == field).Default;
    public int Number(string unit, string field) =>
        int.TryParse(Value(unit, field), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n :
            throw Error($"{unit} · {Catalog[unit].Single(f=>f.Key == field).Label} 需要非负整数。");
    internal static StudioXException Error(string error) => new("AG32_PERIPHERAL_SETUP", error);

    internal static Ag32Setup Read(byte[] bytes)
    {
        var matches = Marker.Matches(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF'));
        if (matches.Count == 0)

        {
            return new();
        }
        try
        {
            if (matches.Count != 1)

            {
                throw Error("外设配置记录重复。");
            }
            var settings = JsonSerializer.Deserialize<Ag32Setup>(matches[0].Value.Trim()["#@StudioX:SETUP".Length..]) ?? throw Error("外设配置为空。");
            settings.CheckFields();
            return settings;
        }
        catch (JsonException e)
        {
            throw Error("外设配置格式有误：" + e.Message);
        }
    }

    internal byte[] Write(byte[] bytes)
    {
        CheckFields();
        var previous = Read(bytes);
        if (Equals(previous))
        {
            CheckUsbDirective(bytes);
            return bytes;
        }
        var original = Encoding.UTF8.GetString(bytes);
        var bom = original.StartsWith('\uFEFF');
        var text = Marker.Replace(original.TrimStart('\uFEFF'), "");
        var nl = original.Contains("\r\n") ? "\r\n" : "\n";
        var usb = UsbLine.Matches(text);
        const string ownedRole = "#@StudioX:USB_ROLE";
        if (previous.Units.ContainsKey("USB0") || Units.ContainsKey("USB0"))
        {
            var enabled = Units.ContainsKey("USB0");
            var ownsLine = usb.Count == 1 && usb[0].Value.TrimEnd().EndsWith(ownedRole, StringComparison.Ordinal);
            if (usb.Count > 1 || enabled && usb.Count == 1 && !ownsLine && !previous.Units.ContainsKey("USB0") &&
                !usb[0].Groups[1].Value.Equals(Value("USB0", "Role"), StringComparison.OrdinalIgnoreCase))

            {
                throw Error("已有手写 USB0 角色与选择不一致，请先核对 VE 中的 USB0 行。");
            }
            if (enabled && !ownsLine && usb.Count == 1 && previous.Units.ContainsKey("USB0"))
            {
                // 旧工程手写的角色行只更新角色值，保留其行内注释和格式。
                var role = usb[0].Groups[1];
                text = text.Remove(role.Index, role.Length).Insert(role.Index, Value("USB0", "Role"));
            }
            if (ownsLine)

            {
                text = UsbLine.Replace(text, "");
            }
            if (enabled && (ownsLine || usb.Count == 0))

            {
                text = text.TrimEnd('\r', '\n') + nl + "USB0 " + Value("USB0", "Role") + " " + ownedRole + nl;
            }
        }
        if (Units.Count != 0)

        {
            text = text.TrimEnd('\r', '\n') + nl + "#@StudioX:SETUP " + JsonSerializer.Serialize(this) + nl;
        }
        return Encoding.UTF8.GetBytes((bom ? "\uFEFF" : "") + text);
    }

    public bool Equals(Ag32Setup? other) =>
        other is not null && Units.Count == other.Units.Count && Units.All(unit =>
            other.Units.TryGetValue(unit.Key, out var values) && unit.Value.Count == values.Count &&
            unit.Value.All(field => values.TryGetValue(field.Key, out var value) && value == field.Value));

    public override bool Equals(object? obj) => obj is Ag32Setup other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var unit in Units.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            hash.Add(unit.Key);
            foreach (var field in unit.Value.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                hash.Add(field.Key);
                hash.Add(field.Value);
            }
        }
        return hash.ToHashCode();
    }

    private void CheckFields()
    {
        if (Units is null)

        {
            throw Error("缺少外设配置。");
        }
        foreach (var (unit, values) in Units)
        {
            if (!Catalog.TryGetValue(unit, out var fields) || values is null || values.Keys.Except(fields.Select(f => f.Key)).Any())

            {
                throw Error("无法识别外设参数：" + unit);
            }
            foreach (var f in fields)
            {
                var v = Value(unit, f.Key);
                if (v is null || v.Length > (f.Key == "SegmentSizes" ? 256 : 100) || f.Choices is not null && !f.Choices.Contains(v) ||
                    f.Minimum is int min && (!int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < min || n > f.Maximum))

                {
                    throw Error($"{unit} · {f.Label} 无效。");
                }
            }
        }
    }
}
