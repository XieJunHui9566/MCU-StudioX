namespace StudioX.Application.CodeIntelligence;

/// <summary>RP2 端口的固定版本 API 提示；只跟踪显式导入、别名和直接构造，不执行 Python。</summary>
internal static class MicroPythonHints
{
    private static readonly HashSet<string> Modules = new(["machine", "time", "rp2", "micropython", "gc", "os", "sys", "json"], StringComparer.Ordinal);
    private static readonly Dictionary<string, PythonCatalog.Entry> Members = CreateMembers();
    internal static IReadOnlyDictionary<string, PythonCatalog.Entry> ApiMembers => Members;
    internal sealed record Context(IReadOnlyList<CodeSuggestion>? Suggestions, CodeSignature? Signature,
        IReadOnlyDictionary<string, PythonCatalog.Entry> Names);

    internal static Context Get(List<PythonSyntax.Token> tokens, string text, int offset, int start, int end)
    {
        var bindings = new Dictionary<string, string>(StringComparer.Ordinal);
        var before = tokens.FindLastIndex(item => item.End <= start);
        for (var i = 0; i < tokens.Count && tokens[i].End <= start; i++)
        {
            if (tokens[i].Value == "import" && (i < 2 || tokens[i - 2].Value != "from"))
            {
                for (var j = i + 1; j < tokens.Count && tokens[j].End <= start; j++)
                {
                    var name = tokens[j].Value;
                    if (!Modules.Contains(name))
                    {
                        break;
                    }
                    var alias = name;
                    if (j + 2 < tokens.Count && tokens[j + 1].Value == "as")
                    {
                        alias = tokens[j + 2].Value;
                        j += 2;
                    }
                    bindings[alias] = name;
                    if (j + 1 >= tokens.Count || tokens[j + 1].Value != ",")
                    {
                        break;
                    }
                    j++;
                }
            }
            if (tokens[i].Value == "from" && i + 2 < tokens.Count && Modules.Contains(tokens[i + 1].Value) && tokens[i + 2].Value == "import")
            {
                var module = tokens[i + 1].Value;
                for (var j = i + 3; j < tokens.Count && tokens[j].End <= start; j++)
                {
                    if (tokens[j].Value is "(" or "\n")
                    {
                        continue;
                    }
                    var key = module + "." + tokens[j].Value;
                    if (!Members.ContainsKey(key))
                    {
                        break;
                    }
                    var alias = tokens[j].Value;
                    if (j + 2 < tokens.Count && tokens[j + 1].Value == "as")
                    {
                        alias = tokens[j + 2].Value;
                        j += 2;
                    }
                    bindings[alias] = key;
                    if (j + 1 >= tokens.Count || tokens[j + 1].Value != ",")
                    {
                        break;
                    }
                    j++;
                }
            }
            if (tokens[i].Value is "def" or "class" && i + 1 < tokens.Count)
            {
                bindings.Remove(tokens[i + 1].Value);
            }
            if (tokens[i].Name && i + 2 < tokens.Count && tokens[i + 1].Value == "=" && tokens[i + 2].Value != "=" &&
                (i == 0 || tokens[i - 1].Value is "\n" or ";"))
            {
                var j = i + 2;
                var target = tokens[j].Value;
                while (j + 2 < tokens.Count && tokens[j + 1].Value == "." && tokens[j + 2].Name)
                {
                    target += "." + tokens[j + 2].Value;
                    j += 2;
                }
                var resolved = Resolve(target);
                bindings.Remove(tokens[i].Value);
                if (resolved is not null && (j + 1 >= tokens.Count || tokens[j + 1].Value != "(" ||
                    resolved is "machine.Pin" or "machine.PWM" or "machine.ADC" or "machine.I2C" or "machine.SPI" or "machine.UART" or "rp2.StateMachine"))
                {
                    bindings[tokens[i].Value] = resolved;
                }
            }
        }
        var names = bindings.Where(pair => Members.ContainsKey(pair.Value)).ToDictionary(pair => pair.Key,
            pair => Members[pair.Value] with { Name = pair.Key }, StringComparer.Ordinal);
        var range = new CodeRange(CodePositions.FromOffset(text, start), CodePositions.FromOffset(text, end));
        var prefix = text[start..offset];
        IReadOnlyList<CodeSuggestion> Suggest(IEnumerable<PythonCatalog.Entry> entries, bool import) => entries
            .Where(entry => entry.Name.StartsWith(prefix, StringComparison.Ordinal)).OrderBy(entry => entry.Name, StringComparer.Ordinal)
            .Select(entry => new CodeSuggestion(entry.Name, entry.Name, entry.Name, entry.Signature?.Label ?? entry.Detail,
                entry.Detail, import ? 9 : entry.Kind, range, entry.Name)).ToArray();
        IEnumerable<PythonCatalog.Entry> Children(string path) => Members.Where(pair => pair.Key.StartsWith(path + ".", StringComparison.Ordinal) &&
            !pair.Key[(path.Length + 1)..].Contains('.')).Select(pair => pair.Value);
        var line = before;
        while (line >= 0 && tokens[line].Value is not ("\n" or ";"))
        {
            line--;
        }
        var leading = tokens.Skip(line + 1).Take(Math.Max(0, before - line)).ToArray();
        if (leading.FirstOrDefault()?.Value is "import" or "from")
        {
            var imported = Array.FindIndex(leading, item => item.Value == "import");
            if (leading[0].Value == "from" && imported >= 2 && Modules.Contains(leading[1].Value))
            {
                return new(Suggest(Children(leading[1].Value), true), null, names);
            }
            return new(Suggest(Modules.Select(module => new PythonCatalog.Entry(module, 9, "MicroPython RP2 模块", null)), true), null, names);
        }
        CodeSignature? signature = null;
        var stack = new Stack<(int Open, int Argument)>();
        for (var i = 0; i < tokens.Count && tokens[i].Start < offset; i++)
        {
            var value = tokens[i].Value;
            if (value is "(" or "[" or "{")
            {
                stack.Push((i, 0));
            }
            else if (value is ")" or "]" or "}")
            {
                if (stack.Count > 0)
                {
                    stack.Pop();
                }
            }
            else if (value == "," && stack.TryPop(out var frame))
            {
                stack.Push((frame.Open, frame.Argument + 1));
            }
        }
        if (stack.TryPeek(out var call) && tokens[call.Open].Value == "(" && call.Open > 0)
        {
            var key = Resolve(Expression(call.Open - 1));
            if (key is not null && Members.TryGetValue(key, out var method) && method.Signature is { } declared)
            {
                signature = declared with
                {
                    ActiveParameter = Math.Min(call.Argument, Math.Max(0, declared.Parameters.Count - 1))
                };
            }
        }
        if (before >= 1 && tokens[before].Value == ".")
        {
            var key = Resolve(Expression(before - 1));
            return new(key is null ? [] : Suggest(Children(key), false), signature, names);
        }
        return new(null, signature, names);

        string Expression(int last)
        {
            if (last < 0 || !tokens[last].Name)
            {
                return "";
            }
            var value = tokens[last].Value;
            while (last >= 2 && tokens[last - 1].Value == "." && tokens[last - 2].Name)
            {
                value = tokens[last - 2].Value + "." + value;
                last -= 2;
            }
            return value;
        }
        string? Resolve(string value)
        {
            var dot = value.IndexOf('.');
            var first = dot < 0 ? value : value[..dot];
            return bindings.TryGetValue(first, out var bound) ? bound + (dot < 0 ? "" : value[dot..]) : null;
        }
    }

    private static Dictionary<string, PythonCatalog.Entry> CreateMembers()
    {
        var result = new Dictionary<string, PythonCatalog.Entry>(StringComparer.Ordinal);
        void Function(string path, string arguments)
        {
            var name = path[(path.LastIndexOf('.') + 1)..];
            var parameters = arguments.Length == 0 ? [] : arguments.Split('|');
            result[path] = new(name, 3, "MicroPython 1.29.0 · RP2 · " + path,
                new(path + "(" + string.Join(", ", parameters) + ")", "固定版本常用调用形式；以板上固件支持为准。", parameters, 0));
        }
        void Constant(string path) => result[path] = new(path[(path.LastIndexOf('.') + 1)..], 21, "MicroPython RP2 · " + path, null);
        Function("machine.Pin", "id|mode=…|pull=None|value=…");
        foreach (var item in new[] { "IN", "OUT", "OPEN_DRAIN", "PULL_UP", "PULL_DOWN", "IRQ_RISING", "IRQ_FALLING" })
        {
            Constant("machine.Pin." + item);
        }
        Function("machine.Pin.value", "value=…");
        Function("machine.Pin.on", "");
        Function("machine.Pin.off", "");
        Function("machine.Pin.toggle", "");
        Function("machine.Pin.irq", "handler=None|trigger=…|hard=False");
        Function("machine.PWM", "dest|freq=…|duty_u16=…");
        Function("machine.PWM.freq", "value=…");
        Function("machine.PWM.duty_u16", "value=…");
        Function("machine.PWM.deinit", "");
        Function("machine.ADC", "id");
        Function("machine.ADC.read_u16", "");
        Constant("machine.ADC.CORE_TEMP");
        Function("machine.I2C", "id|scl|sda|freq=400000");
        Function("machine.I2C.scan", "");
        Function("machine.I2C.readfrom", "addr|nbytes|stop=True");
        Function("machine.I2C.writeto", "addr|buf|stop=True");
        Function("machine.SPI", "id|baudrate=1000000|polarity=0|phase=0|sck|mosi|miso");
        Function("machine.SPI.write", "buf");
        Function("machine.SPI.read", "nbytes|write=0x00");
        Function("machine.UART", "id|baudrate=115200|tx|rx");
        Function("machine.UART.any", "");
        Function("machine.UART.read", "nbytes=…");
        Function("machine.UART.write", "buf");
        Function("machine.freq", "hz=…");
        Function("machine.unique_id", "");
        Function("machine.reset", "");
        Function("machine.soft_reset", "");
        Function("machine.bootloader", "");
        Function("machine.idle", "");
        Function("time.sleep", "seconds");
        Function("time.sleep_ms", "ms");
        Function("time.sleep_us", "us");
        Function("time.ticks_ms", "");
        Function("time.ticks_us", "");
        Function("time.ticks_diff", "ticks1|ticks2");
        Function("time.ticks_add", "ticks|delta");
        Function("rp2.StateMachine", "id|program|freq=-1|in_base=…|out_base=…|set_base=…|jmp_pin=…|sideset_base=…");
        Function("rp2.StateMachine.active", "value=…");
        Function("rp2.StateMachine.put", "value|shift=0");
        Function("rp2.StateMachine.get", "buf=…|shift=0");
        Function("rp2.asm_pio", "out_init=…|set_init=…|sideset_init=…");
        Constant("rp2.PIO.OUT_LOW");
        Constant("rp2.PIO.OUT_HIGH");
        result["rp2.PIO"] = new("PIO", 7, "MicroPython RP2 PIO", null);
        Function("gc.collect", "");
        Function("gc.mem_free", "");
        Function("gc.mem_alloc", "");
        Function("micropython.const", "value");
        Function("micropython.alloc_emergency_exception_buf", "size");
        Function("os.listdir", "path='.'");
        Function("os.stat", "path");
        Function("os.uname", "");
        Function("json.dumps", "obj");
        Function("json.loads", "text");
        Constant("sys.implementation");
        Constant("sys.path");
        return result;
    }
}
