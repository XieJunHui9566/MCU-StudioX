namespace StudioX.Application.StcDebugging;

using System.Globalization;
using System.Text.RegularExpressions;
using StudioX.Foundation;

/// <summary>读取 SDCC 原生 CDB；保留完整作用域键，不根据同名变量猜测地址。</summary>
public sealed class Mon51Symbols
{
    public IReadOnlyList<Mon51Symbol> Symbols { get; private init; } = [];
    public IReadOnlyList<Mon51SourceLine> Lines { get; private init; } = [];
    public IReadOnlyList<Mon51Function> Functions { get; private init; } = [];
    public IReadOnlyDictionary<string, Mon51Symbol[]> Structures { get; private init; } = new Dictionary<string, Mon51Symbol[]>();

    private static string Canonical(string key) => Regex.Replace(key, @"\$(\d+)_0\$", "$$$1$$");

    private static Mon51Symbol ParseSymbol(string text)
    {
        var match = Regex.Match(text, @"^([^$]+)\$([^$]+)\$([^$]+)\$(\d+)\(\{(\d+)\}(.+)\),([A-Z]),([01]),(-?\d+)(?:,\[([^\]]*)\])?$");
        if (!match.Success)
        {
            throw new StudioXException("MON51_CDB", "无法解析 CDB 符号记录：" + text);
        }
        var g = match.Groups;
        return new(Canonical(text[..text.IndexOf('(')]), g[1].Value, g[2].Value, g[3].Value, int.Parse(g[4].Value), int.Parse(g[5].Value), g[6].Value, g[7].Value[0], g[8].Value == "1", int.Parse(g[9].Value), g[10].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    public static Mon51Symbols Parse(string text, string sourceRoot, IReadOnlyList<string>? compiledSources = null)
    {
        if (text.Length > 16 * 1024 * 1024)
        {
            throw new StudioXException("MON51_CDB", "CDB 文件超过 16 MiB。");
        }
        var symbols = new Dictionary<string, Mon51Symbol>(StringComparer.Ordinal);
        var addresses = new Dictionary<string, ushort>(StringComparer.Ordinal);
        var ends = new Dictionary<string, ushort>(StringComparer.Ordinal);
        var functions = new List<(Mon51Symbol Symbol, bool Interrupt)>();
        var lines = new List<Mon51SourceLine>();
        var structures = new Dictionary<string, Mon51Symbol[]>(StringComparer.Ordinal);
        var sourceFiles = new Mon51SourceFiles(sourceRoot, compiledSources);
        var module = "";
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("M:", StringComparison.Ordinal))
            {
                module = line[2..];
            }
            else if (line.StartsWith("S:", StringComparison.Ordinal))
            {
                var symbol = ParseSymbol(line[2..]) with
                {
                    Module = module
                };
                if (symbols.TryGetValue(symbol.Key, out var previous) && previous != symbol)
                {
                    // 链接 CDB 可重复包含全局声明；不同类型或存储区不能静默覆盖。
                    if (previous.TypeChain != symbol.TypeChain || previous.Space != symbol.Space)
                    {
                        throw new StudioXException("MON51_CDB", "CDB 包含矛盾的符号声明：" + symbol.Key);
                    }
                }
                symbols[symbol.Key] = symbol;
            }
            else if (line.StartsWith("F:", StringComparison.Ordinal))
            {
                var last = line.LastIndexOf("),", StringComparison.Ordinal);
                if (last < 0)
                {
                    throw new StudioXException("MON51_CDB", "无效函数记录：" + line);
                }
                var fields = line[(last + 2)..].Split(',');
                if (fields.Length != 6)
                {
                    throw new StudioXException("MON51_CDB", "无效函数属性：" + line);
                }
                functions.Add((ParseSymbol(line[2..(last + 1)] + "," + string.Join(',', fields.Take(3))), fields[3] == "1"));
            }
            else if (line.StartsWith("L:", StringComparison.Ordinal))
            {
                var colon = line.LastIndexOf(':');
                if (colon < 3 || !ushort.TryParse(line[(colon + 1)..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var address))
                {
                    throw new StudioXException("MON51_CDB", "无效链接地址：" + line);
                }
                var key = Canonical(line[2..colon]);
                if (key.StartsWith("C$", StringComparison.Ordinal))
                {
                    var parts = key.Split('$');
                    if (parts.Length != 5 || !int.TryParse(parts[2], out var number) || number < 1 || !int.TryParse(parts[4], out var block) || address >= 0xdbfd)
                    {
                        throw new StudioXException("MON51_CDB", "无效源码行：" + line);
                    }
                    var file = sourceFiles.Resolve(parts[1]);
                    lines.Add(new(file, number, parts[3], block, address));
                }
                else if (key.StartsWith("X", StringComparison.Ordinal))
                {
                    ends[key[1..]] = address;
                }
                else if (!key.StartsWith("A$", StringComparison.Ordinal))
                {
                    if (addresses.TryGetValue(key, out var previousAddress) && previousAddress != address)
                    {
                        throw new StudioXException("MON51_CDB", "同一符号包含冲突的链接地址：" + key);
                    }
                    addresses[key] = address;
                }
            }
            else if (line.StartsWith("T:", StringComparison.Ordinal))
            {
                var start = line.IndexOf('[');
                var dollar = line.IndexOf('$');
                if (start < 0 || dollar < 0)
                {
                    throw new StudioXException("MON51_CDB", "无效结构记录：" + line);
                }
                var members = Regex.Matches(line[(start + 1)..], @"\(\{(\d+)\}S:(.+?\),[A-Z],[01],-?\d+(?:,\[[^\]]*\])?)\)");
                var typeModule = line[3..dollar];
                structures[typeModule + "." + line[(dollar + 1)..start]] = members.Select(m => ParseSymbol(m.Groups[2].Value) with { Address = ushort.Parse(m.Groups[1].Value), Module = typeModule }).ToArray();
            }
        }
        var result = new Mon51Symbols
        {
            Symbols = symbols.Values.Select(s => s with { Address = addresses.TryGetValue(s.Key, out var address) ? address : null }).ToArray(),
            Lines = lines.Distinct().OrderBy(l => l.Address).ThenBy(l => l.Line).ToArray(),
            Structures = structures,
            Functions = functions.Where(f => addresses.ContainsKey(f.Symbol.Key) && ends.ContainsKey(f.Symbol.Key)).Select(f =>
                new Mon51Function(f.Symbol.Name, f.Symbol.Scope, addresses[f.Symbol.Key], ends[f.Symbol.Key], f.Symbol.OnStack, f.Interrupt)).Distinct().OrderBy(f => f.Start).ToArray()
        };
        if (result.Lines.Count == 0 || result.Functions.Count == 0 || result.Functions.Any(f => f.End < f.Start || f.End >= 0xdbfd))
        {
            throw new StudioXException("MON51_CDB", "CDB 缺少用户区源码行或完整函数范围；请使用 SDCC --debug 重新编译。");
        }
        return result;
    }

    public Mon51Function? FunctionAt(ushort address) => Functions.FirstOrDefault(f => address >= f.Start && address <= f.End);
    public Mon51SourceLine? LineAt(ushort address)
    {
        var function = FunctionAt(address);
        return function is null ? null : Lines.LastOrDefault(l => l.Address <= address && l.Address >= function.Start && l.Address <= function.End);
    }
    public Mon51SourceLine? Bind(string file, int line) => Lines.Where(l => l.File.Equals(Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase) && l.Line == line).OrderBy(l => l.Address).FirstOrDefault();
}
