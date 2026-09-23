namespace StudioX.Desktop;

using System.Xml.Linq;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;

internal static class CodeLanguage
{
    private static readonly Dictionary<(string, bool), IHighlightingDefinition> Cache = new();
    public static string ForFile(string path)
    {
        if (Path.GetFileName(path).Equals("CMakeLists.txt", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(path).Equals(".cmake", StringComparison.OrdinalIgnoreCase)) return "CMake";
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".c" or ".h" => "C", ".cpp" or ".cc" or ".cxx" or ".hpp" or ".hh" or ".hxx" => "C++",
            ".s" or ".asm" => "Assembly", ".ld" or ".lds" => "Linker", ".json" => "JSON",
            ".xml" or ".svd" => "XML", ".v" or ".sv" => "Verilog", ".ve" => "AGM Pin Map", _ => "Text"
        };
    }
    public static IHighlightingDefinition? Get(string language, bool dark)
    {
        if (language == "Text") return null;
        if (Cache.TryGetValue((language, dark), out var cached)) return cached;
        XNamespace ns = "http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008";
        var definition = new XElement(ns + "SyntaxDefinition", new XAttribute("name", language));
        var colors = new Dictionary<string, string>
        {
            ["Comment"] = dark ? "#7D8C79" : "#62735D", ["Keyword"] = dark ? "#CF8E6D" : "#9E4B16",
            ["Type"] = dark ? "#6FB7B2" : "#087A80", ["Function"] = dark ? "#E2BF83" : "#795E26",
            ["Variable"] = dark ? "#A9C7E8" : "#235C91", ["Macro"] = dark ? "#C59DD8" : "#8551A3",
            ["String"] = dark ? "#8FBC8F" : "#477D38", ["Number"] = dark ? "#6FB3CE" : "#176BA0",
            ["Punctuation"] = dark ? "#BCBEC4" : "#44474E"
        };
        foreach (var (name, color) in colors) definition.Add(new XElement(ns + "Color", new XAttribute("name", name), new XAttribute("foreground", color)));
        var rules = new XElement(ns + "RuleSet"); definition.Add(rules);
        XElement Rule(string color, string regex) => new(ns + "Rule", new XAttribute("color", color), regex);
        XElement Span(string color, string begin, string end, bool multiline = false, bool escapes = false)
        {
            var span = new XElement(ns + "Span", new XAttribute("color", color), new XAttribute("multiline", multiline), new XElement(ns + "Begin", begin), new XElement(ns + "End", end));
            if (escapes) span.Add(new XElement(ns + "RuleSet", new XElement(ns + "Span", new XElement(ns + "Begin", @"\\"), new XElement(ns + "End", "."))));
            return span;
        }
        void Keywords(string color, string words) => rules.Add(new XElement(ns + "Keywords", new XAttribute("color", color), words.Split(' ').Select(word => new XElement(ns + "Word", word))));
        if (language == "XML")
        {
            rules.Add(Span("Comment", "<!--", "-->", true), Span("String", "\"", "\"", true), Span("String", "'", "'", true));
            rules.Add(Rule("Keyword", @"</?[\w:.-]+|/?>"), Rule("Variable", @"[\w:.-]+(?=\s*=)"));
        }
        else if (language == "AGM Pin Map")
        {
            rules.Add(Span("Comment", "#", "$"));
            Keywords("Keyword", "SYSCLK BUSCLK HSECLK INPUT OUTPUT INOUT");
            rules.Add(Rule("Type", @"\bPIN_[0-9]+\b"), Rule("Keyword", @":(?:INPUT|OUTPUT|INOUT)\b"),
                Rule("Number", @"\b[0-9]+\b"), Rule("Variable", @"\b[A-Za-z_]\w*(?:\[[0-9]+\])?\b"));
        }
        else
        {
            if (language is "C" or "C++" or "Linker" or "Verilog")
                rules.Add(Span("Comment", @"/\*", @"\*/", true), Span("Comment", "//", "$"));
            if (language == "CMake") rules.Add(Span("Comment", @"\#\[\[", @"\]\]", true), Span("Comment", @"\#", "$"), Span("String", @"\[\[", @"\]\]", true));
            if (language == "Assembly") rules.Add(Span("Comment", @"[;\#]|//", "$"));
            rules.Add(Span("String", "\"", "\"", escapes: true));
            if (language is not "JSON" and not "Verilog") rules.Add(Span("String", "'", "'", escapes: true));
            if (language == "Verilog")
            {
                Keywords("Keyword", "always always_comb always_ff always_latch assign automatic begin case casex casez default disable do else end endcase endfunction endgenerate endmodule endtask for forever function generate genvar if initial inout input integer localparam logic module negedge output parameter posedge reg repeat return signed task typedef unsigned var wait while wire");
                rules.Add(Rule("Macro", @"`[A-Za-z_]\w*|\$[A-Za-z_]\w*"),
                    Rule("Number", @"(?:\b[0-9][0-9_]*\s*)?'[sS]?[bBoOdDhH][0-9a-fA-F_xXzZ?]+\b"));
            }
            if (language is "C" or "C++")
            {
                rules.Add(Rule("Macro", @"^\s*\#[ \t]*[A-Za-z_]\w*"));
                Keywords("Keyword", "auto break case const constexpr continue default do else enum extern for goto if inline register restrict return sizeof static struct switch typedef union volatile while alignas alignof bool catch class delete explicit export false friend mutable namespace new noexcept nullptr operator private protected public reinterpret_cast static_assert static_cast template this throw true try typename using virtual");
                Keywords("Type", "void char double float int long short signed unsigned _Bool _Complex");
                rules.Add(Rule("Type", @"\b[A-Za-z_]\w*_t\b"));
            }
            if (language == "Linker") Keywords("Keyword", "MEMORY SECTIONS ENTRY OUTPUT_ARCH OUTPUT_FORMAT KEEP AT ORIGIN LENGTH PROVIDE ALIGN LOADADDR SIZEOF ASSERT NOLOAD");
            if (language == "CMake") rules.Add(Rule("Variable", @"\$\{[^}]+\}|\$<[^>]+>"));
            if (language == "JSON") Keywords("Keyword", "true false null");
            if (language == "Assembly") rules.Add(Rule("Type", @"\b(?:x[0-9]+|[ast][0-9]+|zero|ra|sp|gp|tp)\b"), Rule("Keyword", @"\.[A-Za-z_]\w*"));
            rules.Add(Rule("Number", @"\b(?:0[xX][\da-fA-F]+|0[bB][01]+|[0-9]+(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?)[uUlLfF]*\b"));
            rules.Add(Rule("Function", @"\b[A-Za-z_]\w*(?=\s*\()"), Rule("Macro", @"\b[A-Z_][A-Z_0-9]*\b"), Rule("Variable", @"\b[A-Za-z_]\w*\b"));
            rules.Add(Rule("Punctuation", @"[{}\[\]();,.+*/%=!&|<>?:~-]"));
        }
        using var reader = definition.CreateReader();
        var result = HighlightingLoader.Load(reader, HighlightingManager.Instance);
        Cache[(language, dark)] = result; return result;
    }
}
