namespace StudioX.Engine;

using System.Text;
using StudioX.Foundation;

internal static class Ag32InterruptCode
{
    internal static string Enable(string vector, int priority, string indent = "")
        => $"{indent}INT_EnableIRQ({vector}_IRQn, {(priority == 1 ? "PLIC_MIN_PRIORITY" : priority.ToString(System.Globalization.CultureInfo.InvariantCulture))});\n" +
           $"{indent}INT_EnableIntExternal();\n#ifndef STUDIOX_FREERTOS\n{indent}INT_EnableIntGlobal();\n#endif";

    internal static int SharedPriority(string vector, IEnumerable<int> values)
    {
        var priorities = values.Distinct().ToArray();
        if (priorities.Length != 1 || priorities[0] is < 1 or > 15)

        {
            throw new StudioXException("AG32_IRQ_PRIORITY", $"{vector} 共用一个 PLIC 向量，应设置相同的 1～15 优先级。");
        }
        return priorities[0];
    }

    internal static void Callback(StringBuilder header, StringBuilder source, string signature, string description, string unused = "")
    {
        header.AppendLine(signature + ";");
        source.AppendLine($"/* 用户中断回调。{description} */")
            .AppendLine("__attribute__((weak)) " + signature).AppendLine("{");
        if (unused.Length != 0)
        {
            source.Append("    ").AppendLine(unused);
        }
        source.AppendLine("}").AppendLine();
    }
}
