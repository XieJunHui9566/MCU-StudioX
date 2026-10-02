namespace StudioX.Engine;

using System.Globalization;
using System.Text.RegularExpressions;

public static class BuildDetailAnalyzer
{
    public static IReadOnlyList<BuildContribution> ParseMap(string text)
    {
        if (text.Length > 128 * 1024 * 1024)
        {
            throw new InvalidDataException("MAP 超过 128 MiB。");
        }
        var rows = new List<BuildContribution>();
        string? pending = null;
        var allocated = false;
        foreach (var line in text.Split('\n'))
        {
            if (line.Trim() == "Linker script and memory map")
            {
                allocated = true;
                continue;
            }
            if (!allocated)
            {
                continue;
            }
            var section = Regex.Match(line, @"^\s+(\.(?:text|rodata|data|bss|sdata|sbss)(?:\.[\w.$]+)?)\s*(.*)$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            var body = line;
            if (section.Success)
            {
                pending = section.Groups[1].Value;
                body = section.Groups[2].Value;
            }
            if (pending is null)
            {
                continue;
            }
            var value = Regex.Match(body, @"^\s*0x[0-9a-fA-F]+\s+0x([0-9a-fA-F]+)\s+(.+(?:\.o|\.obj|\.a\([^)]*\)))\s*$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            if (!value.Success)
            {
                if (!string.IsNullOrWhiteSpace(body))
                {
                    pending = null;
                }
                continue;
            }
            var size = long.Parse(value.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var file = value.Groups[2].Value.Trim().Replace('\\', '/');
            var kind = pending.Split('.')[1];
            var name = pending.Length > kind.Length + 2 ? pending[(kind.Length + 2)..] : pending;
            if (size > 0)
            {
                rows.Add(new(kind, name, file, size));
            }
            pending = null;
        }
        return rows.GroupBy(r => (r.Category, r.Name, r.File)).Select(g => g.First() with { Bytes = g.Sum(v => v.Bytes) }).OrderByDescending(r => r.Bytes).ToArray();
    }

    public static IReadOnlyList<CompilationTiming> ParseNinjaLog(string text)
    {
        if (text.Length > 32 * 1024 * 1024)
        {
            throw new InvalidDataException("Ninja 日志超过 32 MiB。");
        }
        var rows = new Dictionary<string, CompilationTiming>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n'))
        {
            var parts = line.TrimEnd('\r').Split('\t');
            if (parts.Length != 5 || !long.TryParse(parts[0], out var start) || !long.TryParse(parts[1], out var end) || start < 0 || end < start)
            {
                continue;
            }
            if (!parts[3].EndsWith(".o", StringComparison.Ordinal) && !parts[3].EndsWith(".obj", StringComparison.Ordinal))
            {
                continue;
            }
            rows[parts[3]] = new(parts[3], end - start);
        }
        return rows.Values.OrderByDescending(r => r.Milliseconds).Take(100).ToArray();
    }
}
