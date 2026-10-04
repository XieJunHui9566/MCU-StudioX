namespace StudioX.LabPlugins;

using System.Text.Json;
using StudioX.Extensions.Abstractions;
using static LabPanel;

/// <summary>保留已安装插件的 ID 与入口类型，提供程序员计算器和独立位域工具页。</summary>
public sealed class BitLabPlugin : LabPlugin
{
    private IPluginHost? activeHost;
    private JsonElement calculatorInput = Json(new { });
    private JsonElement fieldInput = Json(new { operation = "inspect" });
    private LabResult? current;
    private bool fieldMode;
    private long revision;
    private long inputRevision;

    public override string Title => "程序员助手";
    protected override string Introduction => "程序员计算器：四种进制、定宽整数、按位运算与位域工具。";
    protected override JsonElement Schema => LabPanel.Schema(new()
    {
        ["width"] = EnumSchema("8", "16", "32", "64"),
        ["radix"] = EnumSchema("2", "8", "10", "16"),
        ["signedMode"] = new
        {
            type = "boolean"
        },
        ["value"] = StringSchema(1024),
        ["operand"] = StringSchema(1024),
        ["start"] = NumberSchema(0, 63, true),
        ["count"] = NumberSchema(1, 64, true),
        ["operation"] = EnumSchema("evaluate", "and", "or", "xor", "not", "nand", "nor", "+", "-", "*", "/", "%", "shl", "shr", ">>>", "rol", "ror", "inspect", "set", "clear", "toggle", "replace")
    });

    protected override PluginPanelWidget[] Inputs(JsonElement values)
    {
        var common = new List<PluginPanelWidget>
        {
            Select("width", "位宽", LabInput.Text(values, "width", "32"), ("8", "BYTE · 8 位"), ("16", "WORD · 16 位"), ("32", "DWORD · 32 位"), ("64", "QWORD · 64 位")),
            Select("radix", "输入进制（无前缀数字）", LabInput.Text(values, "radix", "10"), ("16", "HEX · 十六进制"), ("10", "DEC · 十进制"), ("8", "OCT · 八进制"), ("2", "BIN · 二进制")),
            Input("value", fieldMode ? "原始值" : "原始值 / 表达式 A", LabInput.Text(values, "value", "0x12345678"))
        };
        if (fieldMode)
        {
            var width = int.Parse(LabInput.Text(values, "width", "32"), System.Globalization.CultureInfo.InvariantCulture);
            var start = LabInput.Integer(values, "start", Math.Min(8, width - 1), 0, 63);
            common.Add(Number("start", "位域起始位（最低位为 0）", start));
            common.Add(Number("count", "位域长度", LabInput.Integer(values, "count", Math.Min(8, width - start), 1, 64)));
            common.Add(Select("operation", "位域操作", LabInput.Text(values, "operation", "inspect"),
                ("inspect", "查看位域"), ("set", "置 1"), ("clear", "清 0"), ("toggle", "翻转"), ("replace", "替换位域")));
            common.Add(Input("operand", "替换值（仅替换位域使用）", LabInput.Text(values, "operand", "0xAB")));
        }
        else
        {
            common.Add(Select("operation", "运算（也可直接在 A 中输入完整表达式）", LabInput.Text(values, "operation", "evaluate"),
                ("evaluate", "= 计算表达式 / 转换进制"), ("and", "AND · A & B · 位与"), ("or", "OR · A | B · 位或"),
                ("xor", "XOR · A ^ B · 异或"), ("not", "NOT · ~A · 按位取反"), ("nand", "NAND · 与非"), ("nor", "NOR · 或非"),
                ("shl", "<< 左移"), ("shr", ">> 右移（遵循有符号模式）"), (">>>", ">>> 逻辑右移"), ("rol", "ROL · 循环左移"), ("ror", "ROR · 循环右移"),
                ("+", "+ 加"), ("-", "− 减"), ("*", "× 乘"), ("/", "÷ 整数除法"), ("%", "% 取余")));
            common.Add(Input("operand", "操作数 / 表达式 B（转换进制和 NOT 不使用）", LabInput.Text(values, "operand", "0xFF")));
            common.Add(Check("signedMode", "有符号运算（影响 DEC、除法、取余和 >> 右移）", LabInput.Boolean(values, "signedMode")));
        }
        return common.ToArray();
    }

    public override LabResult Calculate(JsonElement values) => ProgrammerCalculator.Calculate(values);

    public override PluginContribution Describe() => new(
        [new("open", "打开程序员助手", "tools"), new("calculate", "计算 / 生成"),
         new("calculator", "程序员计算器"), new("bitfield", "位域工具"), new("clear", "清空计算器"), new("use-result", "结果作为 A")],
        [Panel()], [new("calculate", Introduction + " 支持括号与 + - * / % & | ^ ~ << >> >>>；AND/OR/XOR/NOT/NAND/NOR/ROL/ROR 可作为运算符。输入进制默认 DEC，前缀覆盖输入进制。输入严格检查范围，运算按位宽保留补码。省略参数使用示例。", Schema)]);

    public override async Task ActivateAsync(IPluginHost host, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        activeHost = host;
        current = Calculate(calculatorInput);
        await host.PublishPanelAsync(Panel(), token);
    }

    public override async Task<JsonElement> InvokeAsync(string kind, string id, JsonElement arguments, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var host = activeHost ?? throw new InvalidOperationException("插件尚未激活。");
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("参数必须是对象。");
        }
        if (kind == "command" && id == "open")
        {
            await host.PublishPanelAsync(Panel(), token);
            return Json(new
            {
                ok = true
            });
        }
        if (kind is not ("command" or "agentTool") || (kind == "agentTool" && id != "calculate"))
        {
            throw new ArgumentException("未知插件命令。");
        }
        try
        {
            var values = arguments.TryGetProperty("values", out var submitted) ? NormalizeInputs(submitted) :
                kind == "command" ? CurrentInput : arguments;
            if (values.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException("表单参数必须是对象。");
            }
            if (kind == "command" && id is "calculator" or "bitfield")
            {
                // 先验证两页数据，再提交状态；错误输入不能破坏另一页或上次结果。
                _ = Calculate(values);
                var nextFieldMode = id == "bitfield";
                var next = Calculate(nextFieldMode == fieldMode ? values : nextFieldMode ? fieldInput : calculatorInput);
                Store(values);
                fieldMode = nextFieldMode;
                // 模式切换换一组字段 ID，使旧宿主不会把计算器运算和位域运算的值混用。
                inputRevision++;
                current = next;
            }
            else
            {
                if (id == "clear")
                {
                    values = Json(new
                    {
                        width = LabInput.Text(values, "width", "32"),
                        radix = LabInput.Text(values, "radix", "10"),
                        signedMode = LabInput.Boolean(values, "signedMode"),
                        value = "0",
                        operand = "0",
                        operation = "evaluate"
                    });
                }
                else if (id == "use-result")
                {
                    var data = Json(current?.Data ?? throw new ArgumentException("尚无可继续计算的结果。"));
                    var edited = values.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
                    edited["value"] = Json("0x" + data.GetProperty("hex").GetString());
                    edited["operation"] = Json("evaluate");
                    values = Json(edited);
                }
                else if (id != "calculate")
                {
                    throw new ArgumentException("未知插件命令。");
                }
                var computed = Calculate(values);
                token.ThrowIfCancellationRequested();
                var nextFieldMode = Json(computed.Data).GetProperty("operation").GetString() is "inspect" or "set" or "clear" or "toggle" or "replace";
                if (nextFieldMode != fieldMode)
                {
                    inputRevision++;
                }
                fieldMode = nextFieldMode;
                Store(values);
                current = computed;
                if (id is "clear" or "use-result" || kind == "agentTool")
                {
                    inputRevision++;
                }
            }
            await host.PublishPanelAsync(Panel(), token);
            return Json(new
            {
                ok = true,
                data = current!.Data,
                copyText = current.CopyText
            });
        }
        catch (ArgumentException error) when (kind == "command")
        {
            await host.PublishPanelAsync(Panel(error.Message), token);
            return Json(new
            {
                ok = false,
                error = error.Message
            });
        }
    }

    public override Task DeactivateAsync(CancellationToken token)
    {
        activeHost = null;
        return Task.CompletedTask;
    }
    private JsonElement CurrentInput => fieldMode ? fieldInput : calculatorInput;
    private void Store(JsonElement values)
    {
        if (fieldMode)
        {
            fieldInput = values.Clone();
        }
        else
        {
            calculatorInput = values.Clone();
        }
    }

    private JsonElement NormalizeInputs(JsonElement submitted)
    {
        if (submitted.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("表单参数必须是对象。");
        }
        // 宿主会提交复制框等全部输入；这里只保留当前表单字段，避免旧结果成为计算参数。
        var prefix = "input" + inputRevision + "_";
        if (!submitted.EnumerateObject().Any(p => p.Name.StartsWith(prefix, StringComparison.Ordinal)))
        {
            var names = Schema.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
            return Json(submitted.EnumerateObject().Where(p => names.Contains(p.Name)).ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal));
        }
        return Json(submitted.EnumerateObject().Where(p => p.Name.StartsWith(prefix, StringComparison.Ordinal))
            .ToDictionary(p => p.Name[prefix.Length..], p => p.Value.Clone(), StringComparer.Ordinal));
    }

    private PluginPanelDefinition Panel(string? error = null)
    {
        var widgets = new List<PluginPanelWidget>
        {
            new("calculator", "button", fieldMode ? "切换到程序员计算器" : "程序员计算器", CommandId: "calculator"),
            new("bitfield", "button", fieldMode ? "位域工具" : "切换到位域工具", CommandId: "bitfield")
        };
        if (error is not null)
        {
            widgets.Add(Text("error", "输入错误 / Error", error + " 保留上次成功结果。"));
        }
        if (current is { } result)
        {
            widgets.AddRange(result.Widgets);
        }
        widgets.Add(new("controls", "form", fieldMode ? "位域工具" : "程序员计算器", Children:
            [.. Inputs(CurrentInput).Select(w => w with { Id = "input" + inputRevision + "_" + w.Id }),
             new("calculate", "button", "计算 / 生成", CommandId: "calculate"),
             .. fieldMode ? Array.Empty<PluginPanelWidget>() : new PluginPanelWidget[]
             { new("use-result", "button", "结果作为 A（继续计算）", CommandId: "use-result"), new("clear", "button", "C · 清空", CommandId: "clear") }]));
        widgets.Add(Text("help", "输入提示", "无前缀数字使用所选进制；0x / 0d / 0o / 0b 可混合输入。例：(0xF0 & 0x3C) | (1 << 0d8)。支持 + − × ÷ %、括号和 AND / OR / XOR / NOT / NAND / NOR / ROL / ROR（除号使用 /，乘号使用 *）。输入须在位宽范围内；运算溢出保留低位，整数除法向 0 截断。移位量为 0–位宽。"));
        if (current is { } output)
        {
            widgets.Add(Input("output_" + ++revision, "复制结果（Ctrl+A / Ctrl+C）", output.CopyText));
        }
        return new("lab", Title, widgets.ToArray());
    }
}
