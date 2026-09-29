namespace StudioX.Application.OpenOcdPlot;

using System.Globalization;
using System.Text.RegularExpressions;
using StudioX.Engine.Debugging;

public static class OpenOcdPlotResolver
{
    public static async Task<OpenOcdPlotChannel> ResolveAsync(GdbDebugAdapter adapter, string expression, PlotScalar type,
        Guid session, uint ramOrigin, uint ramBytes, bool littleEndian, CancellationToken token = default)
    {
        if (expression.Length > 160 || !Regex.IsMatch(expression, @"^[A-Za-z_][A-Za-z_0-9]*(?:(?:\.[A-Za-z_][A-Za-z_0-9]*)|(?:\[[0-9]{1,8}\]))*$", RegexOptions.NonBacktracking))
        { throw new ArgumentException("请输入全局变量、成员或固定数组下标。"); }
        // 从全局作用域解析，避免当前栈帧的同名局部变量；逐级拒绝指针和越界下标。
        foreach (Match step in Regex.Matches(expression, @"\.|\[([0-9]+)\]", RegexOptions.NonBacktracking))
        {
            var parent = await DescribeAsync("::" + expression[..step.Index]);
            if (parent.Type.Contains('*') || parent.Type.Contains('&')) { throw new ArgumentException("连续采样不支持指针或引用链。"); }
            if (step.Value.StartsWith('['))
            {
                var bound = Regex.Match(parent.Type, @"\[([0-9]+)\]");
                if (!bound.Success || !uint.TryParse(bound.Groups[1].Value, out var length) || uint.Parse(step.Groups[1].Value, CultureInfo.InvariantCulture) >= length)
                { throw new ArgumentException("数组下标超出 ELF 声明的范围，或目标不是固定数组。"); }
            }
        }
        var global = "::" + expression;
        var value = await DescribeAsync(global);
        if (value.Children != "0" || value.Type.Contains('*') || value.Type.Contains('&') || value.Type.Contains('['))
        { throw new ArgumentException("请选择数值成员或单个数组元素；不采集结构体、数组整体或指针。"); }
        var scalar = type == PlotScalar.Auto ? PlotScalarCodec.Infer(value.Type) : type;
        var sizeText = await EvaluateAsync("sizeof(" + global + ")");
        if (!int.TryParse(sizeText, CultureInfo.InvariantCulture, out var size) || size != PlotScalarCodec.Size(scalar))
        { throw new ArgumentException("所选数值类型与 ELF 中变量的大小不一致：" + value.Type + " / " + sizeText); }
        var addressText = await EvaluateAsync("(unsigned long)&(" + global + ")");
        var hex = addressText.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        if (!uint.TryParse(hex ? addressText[2..] : addressText, hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None, CultureInfo.InvariantCulture, out var address))
        { throw new ArgumentException("无法解析变量地址：" + addressText); }
        if (address < ramOrigin || (ulong)address + (uint)size > (ulong)ramOrigin + ramBytes)
        { throw new ArgumentException("变量不在器件包声明的主 SRAM 中，暂不支持此存储区的连续采样。"); }
        return new(session, expression, address, scalar, littleEndian);

        async Task<string> EvaluateAsync(string text) => (await adapter.SendAsync("-data-evaluate-expression " + MiRecord.Quote(text), token)).String("value");
        async Task<(string Type, string Children)> DescribeAsync(string text)
        {
            var variable = await adapter.SendAsync("-var-create - * " + MiRecord.Quote(text), token);
            var id = variable.String("name");
            if (!Regex.IsMatch(id, @"^[A-Za-z_][A-Za-z_0-9]*$", RegexOptions.NonBacktracking)) { throw new IOException("GDB 返回了非法变量标识。"); }
            try { return (variable.String("type"), variable.String("numchild", "0")); }
            finally { await adapter.SendAsync("-var-delete " + id, CancellationToken.None); }
        }
    }
}
