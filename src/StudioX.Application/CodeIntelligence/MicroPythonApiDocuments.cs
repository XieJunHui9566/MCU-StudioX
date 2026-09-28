namespace StudioX.Application.CodeIntelligence;

using System.Text;
using StudioX.Packages;

/// <summary>把与补全相同的固定 API 目录呈现为只读声明；不冒充固件 C 实现源码。</summary>
internal static class MicroPythonApiDocuments
{
    internal const string Prefix = "@micropython/";

    internal static IReadOnlyDictionary<string, string> Create(MicroPythonProfile? profile)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (profile is null)
        {
            return result;
        }
        foreach (var module in MicroPythonHints.ApiMembers.GroupBy(item => item.Key.Split('.')[0]))
        {
            var text = new StringBuilder($"# MicroPython {profile.Version} · {profile.Board}\n# 只读 API 声明，供跳转和参数查阅；不是解释器实现源码。\n# 常用调用形式以对应版本官方文档为准。\n\n");
            foreach (var top in module.Where(item => item.Key.Count(c => c == '.') == 1))
            {
                var children = module.Where(item => item.Key.StartsWith(top.Key + ".", StringComparison.Ordinal)).ToArray();
                if (children.Length > 0)
                {
                    text.AppendLine($"class {top.Value.Name}:");
                    if (top.Value.Signature is { } constructor)
                    {
                        text.AppendLine("    # " + constructor.Label);
                        text.AppendLine("    def __init__(self, *args, **kwargs): ...");
                    }
                    foreach (var child in children)
                    {
                        Append(child.Value, "    ");
                    }
                }
                else
                {
                    Append(top.Value, "");
                }
                text.AppendLine();
            }
            result[Prefix + module.Key + ".pyi"] = text.ToString();
            void Append(PythonCatalog.Entry entry, string indent)
            {
                if (entry.Signature is { } signature)
                {
                    text.AppendLine(indent + "# " + signature.Label);
                    text.AppendLine(indent + "def " + entry.Name + "(*args, **kwargs): ...");
                }
                else
                {
                    text.AppendLine(indent + entry.Name + " = ...");
                }
            }
        }
        return result;
    }
}
