using System.Numerics;
using System.Text.Json;
using StudioX.Extensions.Abstractions;
using StudioX.LabPlugins;

internal static class ProgrammerChecks
{
    internal static async Task RunAsync(BitLabPlugin plugin, Action<bool, string> check, Action<Action, string> invalid)
    {
        JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
        JsonElement Data(object input) => Json(plugin.Calculate(Json(input)).Data);
        ulong Value(object input) => Data(input).GetProperty("updated").GetUInt64();
        check(plugin.Title == "程序员助手", "programmer assistant replaces old panel title");
        foreach (var width in new[] { 8, 16, 32, 64 })
        {
            var w = width.ToString();
            var max = width == 64 ? ulong.MaxValue : (1UL << width) - 1;
            var min = width == 64 ? long.MinValue : -(1L << (width - 1));
            var d = Data(new
            {
                width = w,
                value = "0x" + max.ToString("X")
            });
            check(d.GetProperty("signed").GetInt64() == -1 && d.GetProperty("unsigned").GetString() == max.ToString(), w + "-bit complete word and exact unsigned text");
            check(d.GetProperty("binary").GetString() == new string('1', width), w + "-bit binary conversion preserves all bits");
            check(Value(new
            {
                width = w,
                value = min.ToString()
            }) == (1UL << (width - 1)), w + "-bit negative minimum uses two's complement");
            check(Value(new
            {
                width = w,
                value = "0x" + max.ToString("X") + " + 1"
            }) == 0, w + "-bit addition wraps only calculation result");
            check(Value(new
            {
                width = w,
                value = "1 << " + width
            }) == 0, w + "-bit full-width shift does not use CLR modulo");
            check(Value(new
            {
                width = w,
                value = "0x" + max.ToString("X") + " >> " + width,
                signedMode = true
            }) == max, w + "-bit signed right shift sign-extends full width");
            check(Value(new
            {
                width = w,
                value = "0x" + max.ToString("X") + " >>> " + width,
                signedMode = true
            }) == 0, w + "-bit explicit logical shift clears full width");
            check(Value(new
            {
                width = w,
                value = "1 ROL " + (width - 1)
            }) == (1UL << (width - 1)), w + "-bit rotate moves low bit into sign bit");
            check(Value(new
            {
                width = w,
                value = "1 ROR 1"
            }) == (1UL << (width - 1)), w + "-bit rotate right wraps into highest bit");
            check(Value(new
            {
                width = w,
                value = "1 ROL " + width
            }) == 1, w + "-bit full rotation preserves value");
            invalid(() => Data(new { width = w, value = (new BigInteger(max) + 1).ToString() }), w + "-bit oversized positive literal rejected");
            invalid(() => Data(new { width = w, value = (new BigInteger(min) - 1).ToString() }), w + "-bit oversized negative literal rejected");
            invalid(() => Data(new { width = w, value = "1 << " + (width + 1) }), w + "-bit oversized shift rejected");
        }
        check(Value(new
        {
            value = "(0xF0 & 0x3C) | (1 << 8)"
        }) == 0x130, "mixed-base parenthesized bitwise expression");
        check(Value(new
        {
            value = "0b1010 XOR 0o7"
        }) == 13, "binary and octal keyword XOR");
        check(Value(new
        {
            value = "FF",
            radix = "16",
            width = "8"
        }) == 255, "HEX mode accepts unprefixed A-F");
        check(Value(new
        {
            value = "FF & 0d15",
            radix = "16",
            width = "8"
        }) == 15, "explicit DEC prefix overrides HEX mode");
        check(Value(new
        {
            value = "77",
            radix = "8"
        }) == 63 && Value(new
        {
            value = "101",
            radix = "2"
        }) == 5, "OCT and BIN input modes");
        check(Value(new
        {
            value = "0xFFFF_FFFF_FFFF_FFFF",
            width = "64"
        }) == ulong.MaxValue, "64-bit literal digit separators");
        check(Value(new
        {
            value = "~0",
            width = "8"
        }) == 255 && Value(new
        {
            value = "NOT 0xAA",
            width = "8"
        }) == 0x55, "unary bitwise NOT respects width");
        check(Value(new
        {
            value = "2 + 3 * 4 << 1 & 0xFF ^ 1 | 0x80"
        }) == 0x9d, "C-style arithmetic and bitwise precedence");
        check(Value(new
        {
            value = "10 - 3 - 2"
        }) == 5 && Value(new
        {
            value = "24 / 3 / 2"
        }) == 4, "subtraction and division are left associative");
        check(Value(new
        {
            value = "-7 / 2",
            signedMode = true,
            width = "8"
        }) == 253, "signed division truncates toward zero");
        check(Value(new
        {
            value = "-7 % 2",
            signedMode = true,
            width = "8"
        }) == 255, "signed remainder follows dividend");
        check(Value(new
        {
            value = "0x80 >> 1",
            signedMode = false,
            width = "8"
        }) == 0x40 && Value(new
        {
            value = "0x80 >> 1",
            signedMode = true,
            width = "8"
        }) == 0xc0, "signed mode controls right shift");
        foreach (var (operation, expected) in new (string, ulong)[] { ("and", 0x30), ("or", 0xfc), ("xor", 0xcc), ("nand", 0xcf), ("nor", 3) })
        {
            check(Value(new
            {
                width = "8",
                value = "0xF0",
                operand = "0x3C",
                operation
            }) == expected, "two-operand " + operation);
        }
        check(Data(new
        {
            value = "-1",
            width = "64",
            signedMode = true
        }).GetProperty("decimalText").GetString() == "-1", "signed DEC conversion retains negative text");
        check(Data(new
        {
            value = "18446744073709551615",
            width = "64"
        }).GetProperty("octal").GetString() == "1777777777777777777777", "maximum QWORD octal known vector");
        var field = Data(new
        {
            width = "64",
            value = "0",
            start = 0,
            count = 64,
            operation = "replace",
            operand = "0xFFFFFFFFFFFFFFFF"
        });
        check(field.GetProperty("updated").GetUInt64() == ulong.MaxValue && field.GetProperty("mask").GetUInt64() == ulong.MaxValue, "64-bit full field mask and replacement");
        var fieldWidgets = plugin.Calculate(Json(new
        {
            width = "64",
            value = "0",
            start = 0,
            count = 64,
            operation = "inspect"
        })).Widgets;
        check(fieldWidgets.Where(w => w.Id.StartsWith("bits", StringComparison.Ordinal)).Sum(w => w.Value!.Value.GetArrayLength()) == 64 && fieldWidgets.Any(w => w.Id == "bits-low"), "all QWORD bits fit existing host row limits");
        check(plugin.Calculate(Json(new
        {
            width = "64",
            value = "0"
        })).CopyText.Contains("UINT64_C", StringComparison.Ordinal), "64-bit C literal uses correct macro");
        foreach (var expression in new[] { "1 / 0", "1 % 0", "(1 + 2", "1 +", "1 2", "0b102", "0o8", "0x", "1.5", "1 && 2", "0x_FF", "1__2", new string('(', 33) + "1" + new string(')', 33), string.Join('+', Enumerable.Repeat("1", 130)) })
        {
            invalid(() => Data(new { value = expression }), "malformed or bounded expression rejected: " + expression[..Math.Min(40, expression.Length)]);
        }
        // 与独立 BigInteger 参考值比较，覆盖 QWORD 乘法最高位和跨位宽进位。
        var random = new Random(20260930);
        foreach (var width in new[] { 8, 16, 32, 64 })
        {
            var mask = (BigInteger.One << width) - 1;
            for (var i = 0; i < 30; i++)
            {
#pragma warning disable CA5394 // 固定种子的算术测试样本，不生成凭据或安全标识。
                var a = (ulong)(new BigInteger(random.NextInt64()) & mask);
                var b = (ulong)(new BigInteger(random.NextInt64()) & mask);
#pragma warning restore CA5394
                var expected = (ulong)((new BigInteger(a) * b + a) & mask);
                if (Value(new
                {
                    width = width.ToString(),
                    value = $"0x{a:X} * 0x{b:X} + 0x{a:X}"
                }) != expected)
                {
                    throw new InvalidOperationException("Fixed width reference mismatch");
                }
            }
        }
        check(true, "120 fixed-width arithmetic comparisons with BigInteger reference");
        var host = new CaptureHost();
        await plugin.ActivateAsync(host, CancellationToken.None);
        PluginProgrammerCalculatorState State() => host.Panel!.Widgets.Single().Value!.Value.Deserialize<PluginProgrammerCalculatorState>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        check(host.Panel!.Presentation == "window" && host.Panel.Widgets.Single().Kind == "programmerCalculator", "calculator requests a host-owned standalone window");
        JsonElement Form(object value) => Json(new
        {
            values = value
        });
        var computed = await plugin.InvokeAsync("command", "calculate", Form(new
        {
            width = "8",
            value = "0xF0",
            operand = "0x3C",
            operation = "and"
        }), CancellationToken.None);
        check(computed.GetProperty("data").GetProperty("updated").GetUInt64() == 0x30, "legacy structured form arguments remain accepted");
        var successful = State().CopyText;
        var failed = await plugin.InvokeAsync("command", "calculate", Form(new
        {
            value = "1/0"
        }), CancellationToken.None);
        check(!failed.GetProperty("ok").GetBoolean() && State().Error is not null && State().CopyText == successful, "calculator failure preserves successful output and host");
        await plugin.InvokeAsync("command", "open", Json(new
        {
        }), CancellationToken.None);
        check(host.Panel!.Title == "程序员助手", "reopening uses renamed panel");
        await plugin.InvokeAsync("command", "use-result", Json(new
        {
        }), CancellationToken.None);
        check(State().Expression == "0x30", "continue result updates actual input value");
        await plugin.InvokeAsync("command", "bitfield", Json(new
        {
        }), CancellationToken.None);
        check(State().FieldMode, "switch to retained bitfield tool");
        await plugin.InvokeAsync("command", "calculator", Json(new
        {
        }), CancellationToken.None);
        check(!State().FieldMode && State().CopyText.Contains("0x30", StringComparison.Ordinal), "calculator values survive bitfield round trip");
        await plugin.InvokeAsync("command", "clear", Json(new
        {
        }), CancellationToken.None);
        check(State().Hex == "00", "clear preserves selected width and resets result");
        async Task<JsonElement> Interact(object request) => await plugin.InvokeAsync("command", "calculate", Json(request), CancellationToken.None);
        computed = await Interact(new
        {
            action = "calculate",
            values = new
            {
                width = "64",
                radix = "16",
                value = "FFFFFFFFFFFFFFFF"
            }
        });
        check(computed.GetProperty("data").GetProperty("updated").GetUInt64() == ulong.MaxValue, "standalone interaction preserves all 64 bits over JSON");
        await Interact(new
        {
            action = "configure",
            setting = "radix",
            choice = "10"
        });
        check(State().Expression == ulong.MaxValue.ToString() && State().Radix == 10, "switching HEX to DEC preserves QWORD value exactly");
        await Interact(new
        {
            action = "configure",
            setting = "signedMode",
            choice = "true"
        });
        check(State().Expression == "-1" && State().SignedMode, "signed toggle changes interpretation without losing bits");
        await Interact(new
        {
            action = "configure",
            setting = "width",
            choice = "8"
        });
        check(State().Hex == "FF" && State().Width == 8, "explicit width reduction keeps low byte");
        await Interact(new
        {
            action = "bit",
            bit = 7
        });
        check(State().Hex == "7F", "bit toggle modifies the selected register bit");
        successful = State().CopyText;
        failed = await Interact(new
        {
            action = "bit",
            bit = 8
        });
        check(!failed.GetProperty("ok").GetBoolean() && State().CopyText == successful, "out-of-width bit toggle preserves result");
        failed = await Interact(new
        {
            action = "configure",
            setting = "radix",
            choice = "3"
        });
        check(!failed.GetProperty("ok").GetBoolean() && State().Radix == 10, "invalid radix cannot corrupt calculator settings");
        await Interact(new
        {
            action = "calculate",
            values = new
            {
                width = "64",
                value = "0"
            }
        });
        await Interact(new
        {
            action = "bit",
            bit = 63
        });
        check(State().Hex == "8000000000000000" && State().LittleEndian.EndsWith("80", StringComparison.Ordinal), "highest QWORD bit and byte order survive interaction");
        computed = await Interact(new
        {
            action = "preview",
            requestId = 101,
            values = new
            {
                width = "32",
                radix = "16",
                value = "51"
            }
        });
        check(State().Preview && State().RequestId == 101 && State().DecimalText == "81" && computed.GetProperty("view").ValueKind == JsonValueKind.Object, "live preview returns correlated state and exact conversions");
        successful = State().CopyText;
        foreach (var expression in new[] { "1 <<", "0x", "(", "1 +", "NOT", "1 <" })
        {
            computed = await Interact(new
            {
                action = "preview",
                requestId = 102,
                values = new
                {
                    value = expression
                }
            });
            check(computed.GetProperty("pending").GetBoolean() && State().Error is null && State().CopyText == successful, "unfinished live expression remains editable: " + expression);
        }
        computed = await Interact(new
        {
            action = "preview",
            values = new
            {
                value = "1/0"
            }
        });
        check(!computed.GetProperty("ok").GetBoolean() && State().Error is not null, "real live arithmetic errors remain visible");
        await Interact(new
        {
            action = "preview",
            values = new
            {
                width = "8",
                value = "0x81",
                signedMode = true
            }
        });
        await Interact(new
        {
            action = "shift",
            operation = "shr"
        });
        check(State().Hex == "C0", "instant right shift respects signed mode");
        await Interact(new
        {
            action = "shift",
            operation = ">>>"
        });
        check(State().Hex == "60", "instant logical right shift clears sign bit");
        await Interact(new
        {
            action = "preview",
            values = new
            {
                width = "8",
                value = "1"
            }
        });
        await Interact(new
        {
            action = "shift",
            operation = "ror"
        });
        check(State().Hex == "80", "instant rotate wraps into top bit");
        await Interact(new
        {
            action = "shift",
            operation = "rol"
        });
        check(State().Hex == "01", "instant rotate back restores bit");
        await Interact(new
        {
            action = "calculate",
            values = new
            {
                width = "8",
                value = "0",
                start = 0,
                count = 1,
                operation = "set"
            }
        });
        await Interact(new
        {
            action = "shift",
            operation = "shl"
        });
        check(State().Hex == "02", "instant operation continues the modified field result instead of its old input");
        await Interact(new
        {
            action = "clear"
        });
        check(State().Hex == "00" && !State().Preview && !State().Pending, "clear resets live input status and result");
        await plugin.DeactivateAsync(CancellationToken.None);
    }

    private sealed class CaptureHost : IPluginHost
    {
        internal PluginPanelDefinition? Panel
        {
            get; private set;
        }
        public Task PublishPanelAsync(PluginPanelDefinition panel, CancellationToken token)
        {
            Panel = panel;
            return Task.CompletedTask;
        }
        public Task<JsonElement> CallAsync(string tool, JsonElement arguments, CancellationToken token) => throw new InvalidOperationException("Unexpected host access");
        public Task LogAsync(string level, string message, CancellationToken token) => Task.CompletedTask;
    }
}
