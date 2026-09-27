namespace StudioX.SourceStyle;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>验证格式工具容易改变意义的边界：悬挂 else、指令、注释、XML 字符引用与混合文本。</summary>
internal static class SourceStyleChecks
{
    public static void Run()
    {
        var sample = "class Example { void M(bool a, bool b) { if (a) if (b) M(a,b); else M(b,a); for (;;) break; while(a) break; do break; while(a); lock(this) M(a,b); using(var x = new System.IO.MemoryStream()) M(a,b); // 保留注释\n } }";
        var syntax = CSharpSyntaxTree.ParseText(sample).GetRoot();
        var rewritten = new BracedStatementRewriter().Visit(syntax)!;
        var parsed = CSharpSyntaxTree.ParseText(rewritten.ToFullString()).GetRoot();
        Require(!parsed.GetDiagnostics().Any(item => item.Severity == DiagnosticSeverity.Error), "rewritten control statements parse");
        var inner = parsed.DescendantNodes().OfType<IfStatementSyntax>().Last();
        Require(inner.Else is not null && parsed.DescendantNodes().OfType<IfStatementSyntax>().First().Else is null, "dangling else remains bound to the inner if");
        Require(parsed.DescendantNodes().OfType<IfStatementSyntax>().All(node => node.Statement is BlockSyntax), "if statements gain explicit blocks");
        Require(parsed.DescendantTrivia().Any(item => item.ToString().Contains("保留注释")), "original comments remain present");
        Require(new BracedStatementRewriter().Visit(parsed)!.ToFullString() == parsed.ToFullString(), "brace expansion is idempotent");
        foreach (var source in new[] { "namespace Example;\n/// <summary>职责。</summary>\nclass C {}", "namespace Example;\n\n\nusing System;\nclass C {}" })
        {
            var namespaced = new BracedStatementRewriter().Visit(CSharpSyntaxTree.ParseText(source).GetRoot())!.ToFullString();
            Require(new BracedStatementRewriter().Visit(CSharpSyntaxTree.ParseText(namespaced).GetRoot())!.ToFullString() == namespaced,
                "namespace spacing remains stable across parsing and rewriting");
        }
        var directive = CSharpSyntaxTree.ParseText("class C { void M(bool a) { if (a)\n#if DEBUG\nM(a);\n#else\nM(false);\n#endif\n } }").GetRoot();
        Require(new BracedStatementRewriter().Visit(directive)!.ToFullString().Contains("#if DEBUG"), "conditional compilation directives are retained");
        var splitSyntax = (CompilationUnitSyntax)CSharpSyntaxTree.ParseText("namespace Example; public record Item(int Value); public partial class Controller { }").GetRoot();
        var split = PublicTypeFiles.Split(Path.Combine(Path.GetTempPath(), "Controller.State.cs"), splitSyntax);
        Require(split.Count == 2 && split.Values.Count(value => value.Contains("partial class Controller")) == 1, "partial implementation retains its file and independent DTO is split");
        foreach (var xml in new[]
        {
            "<Root><Child Value=\"line1&#xA;line2&#xD;line3\"/></Root>",
            "<Root><Text xml:space=\"preserve\"> A <Run/> B </Text></Root>",
            "<Root xmlns:x=\"urn:test\"><Text>Hello <x:Run/> World</Text><![CDATA[ keep <xml> ]]></Root>",
            "<?xml version=\"1.0\" encoding=\"utf-8\"?><Root><Child/></Root>"
        })
        {
            var formatted = XmlSourceFormatter.Format(xml);
            Require(XmlSourceFormatter.Format(formatted) == formatted, "XML formatting is semantically equal and idempotent");
        }
        Console.WriteLine("PASS 13 source formatter boundary checks.");
    }

    private static void Require(bool condition, string description)
    {
        if (!condition)
        {
            throw new InvalidOperationException(description);
        }
        Console.WriteLine("PASS " + description);
    }
}
