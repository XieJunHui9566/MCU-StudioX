namespace StudioX.Application.CodeIntelligence;

using System.Text;
using StudioX.Engine;
using StudioX.Foundation;

/// <summary>有界读取 GCC/CMake 参数响应文件；只解析数据，缓存限定在一次配置加载中。</summary>
internal sealed class CompilationResponseFiles
{
    private sealed record Input(string[] Arguments, string Text);
    private readonly Dictionary<string, Input> inputs = new(StringComparer.OrdinalIgnoreCase);
    internal string[] Paths => inputs.Keys.ToArray();
    internal bool Unchanged => inputs.All(pair => ReadText(pair.Key) == pair.Value.Text);
    internal string[] Expand(string[] original, string directory)
    {
        if (original.Length == 0)
        {
            throw new StudioXException("LANGUAGE_ESPRESSIF_COMMAND", "原生编译命令为空。");
        }
        return [original[0], .. ExpandCore(original.Skip(1), directory, new(StringComparer.OrdinalIgnoreCase), 0)];
    }
    private IReadOnlyList<string> ExpandCore(IEnumerable<string> arguments, string directory, HashSet<string> stack, int depth)
    {
        var expanded = new List<string>();
        foreach (var argument in arguments)
        {
            if (!argument.StartsWith('@'))
            {
                expanded.Add(argument);
            }
            else
            {
                var path = EspressifPathIdentity.NormalizePath(Path.GetFullPath(argument[1..], directory));
                if (depth >= 4 || !stack.Add(path))
                {
                    throw new StudioXException("LANGUAGE_ESPRESSIF_RESPONSE", "编译参数响应文件循环引用或嵌套过深：" + path);
                }
                if (!inputs.TryGetValue(path, out var input))
                {
                    if (inputs.Count >= 128)
                    {
                        throw new StudioXException("LANGUAGE_ESPRESSIF_RESPONSE", "响应文件数量超出分析范围。");
                    }
                    var text = ReadText(path);
                    input = new(CodeIntelligenceService.SplitCMakeCommand(text), text);
                    inputs[path] = input;
                }
                // GCC 的嵌套响应文件沿用编译工作目录；不执行脚本或 specs。
                expanded.AddRange(ExpandCore(input.Arguments, directory, stack, depth + 1));
                stack.Remove(path);
            }
            if (expanded.Count > 4096)
            {
                throw new StudioXException("LANGUAGE_ESPRESSIF_RESPONSE", "展开后的编译参数超出分析范围。");
            }
        }
        return expanded;
    }
    private static string ReadText(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 256 * 1024)
        {
            throw new StudioXException("LANGUAGE_ESPRESSIF_RESPONSE", "编译参数响应文件过大：" + path);
        }
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = stream.Read(buffer)) > 0)
        {
            if (memory.Length + count > 256 * 1024)
            {
                throw new StudioXException("LANGUAGE_ESPRESSIF_RESPONSE", "读取期间编译参数响应文件超出范围：" + path);
            }
            memory.Write(buffer, 0, count);
        }
        memory.Position = 0;
        using var reader = new StreamReader(memory, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
