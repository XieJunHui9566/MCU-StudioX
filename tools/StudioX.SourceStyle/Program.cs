using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using StudioX.SourceStyle;

if (args is ["--self-test"])
{
    SourceStyleChecks.Run();
    return 0;
}

if (args is ["--finish-whitespace", var sourceRoot, var baseline, var formatted, var operation] && operation is "--write" or "--check")
{
    return CSharpWhitespace.Finish(sourceRoot, baseline, formatted, operation == "--write");
}

if (args is not [var mode, var workspace] || mode is not ("--write" or "--check"))
{
    Console.Error.WriteLine("Usage: StudioX.SourceStyle --write|--check <workspace>");
    return 2;
}
var root = Path.GetFullPath(workspace);
if (!File.Exists(Path.Combine(root, "StudioX.slnx")))
{
    throw new ArgumentException("目标目录不是 MCU StudioX 源码仓库。");
}
var changes = new List<string>();
var createdTypes = 0;
var files = SourceFiles.Enumerate(root).ToArray();
foreach (var path in files)
{
    var extension = Path.GetExtension(path).ToLowerInvariant();
    if (extension is not (".cs" or ".xaml" or ".csproj" or ".props" or ".targets" or ".slnx"))
    {
        continue;
    }
    var original = File.ReadAllText(path);
    if (extension == ".cs")
    {
        var tree = CSharpSyntaxTree.ParseText(original, new CSharpParseOptions(LanguageVersion.Preview));
        var syntax = (CompilationUnitSyntax)tree.GetRoot();
        // 语法错误和预处理分支必须由作者处理，不能由格式工具猜测并改写。
        if (syntax.GetDiagnostics().Any(item => item.Severity == DiagnosticSeverity.Error))
        {
            throw new InvalidOperationException("格式化前源码存在语法错误：" + path);
        }
        var split = PublicTypeFiles.Split(path, syntax);
        if (split.Count > 0)
        {
            foreach (var (destination, source) in split)
            {
                Update(destination, RewriteStatements(source));
            }
            createdTypes += split.Count - 1;
        }
        else
        {
            Update(path, RewriteStatements(original));
        }
    }
    else
    {
        try
        {
            Update(path, XmlSourceFormatter.Format(original));
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("XML 格式化失败：" + path, ex);
        }
    }
}
Console.WriteLine($"{mode}: {files.Length} first-party files inspected, {changes.Count} structural style changes, {createdTypes} public type files.");
foreach (var path in changes)
{
    Console.WriteLine(Path.GetRelativePath(root, path));
}
return mode == "--check" && changes.Count > 0 ? 1 : 0;

void Update(string path, string text)
{
    var existing = File.Exists(path) ? File.ReadAllText(path) : null;
    if (existing == text)
    {
        return;
    }
    changes.Add(path);
    if (mode == "--write")
    {
        File.WriteAllText(path, text, new UTF8Encoding(false));
    }
}

string RewriteStatements(string text)
{
    var syntax = CSharpSyntaxTree.ParseText(text, new CSharpParseOptions(LanguageVersion.Preview)).GetRoot();
    var formatted = new BracedStatementRewriter().Visit(syntax)!.ToFullString();
    // 格式工具不得改写注释或字面量；补花括号之外的 token 必须逐字一致。
    var before = syntax.DescendantTokens().Where(token => !token.IsKind(SyntaxKind.OpenBraceToken) && !token.IsKind(SyntaxKind.CloseBraceToken)).Select(token => token.Text);
    var afterSyntax = CSharpSyntaxTree.ParseText(formatted, new CSharpParseOptions(LanguageVersion.Preview)).GetRoot();
    var after = afterSyntax.DescendantTokens().Where(token => !token.IsKind(SyntaxKind.OpenBraceToken) && !token.IsKind(SyntaxKind.CloseBraceToken)).Select(token => token.Text);
    var beforeComments = Comments(syntax);
    var afterComments = Comments(afterSyntax);
    if (!before.SequenceEqual(after) || !beforeComments.SequenceEqual(afterComments) ||
        afterSyntax.GetDiagnostics().Any(item => item.Severity == DiagnosticSeverity.Error))
    {
        throw new InvalidOperationException("补花括号改变了源码，已停止写入。");
    }
    return formatted;
}

IEnumerable<string> Comments(SyntaxNode syntax) => syntax.DescendantTrivia()
    .Where(trivia => trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) || trivia.IsKind(SyntaxKind.MultiLineCommentTrivia) ||
        trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) || trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
    .Select(trivia => trivia.ToFullString());
