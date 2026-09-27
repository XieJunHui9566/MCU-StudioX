namespace StudioX.SourceStyle;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>公开契约按类型归档；保留命名空间、导入、XML 注释以及主类型的内部帮助类。</summary>
internal static class PublicTypeFiles
{
    public static IReadOnlyDictionary<string, string> Split(string path, CompilationUnitSyntax root)
    {
        if (root.Members.Count != 1 || root.Members[0] is not BaseNamespaceDeclarationSyntax scope ||
            scope.ContainsDirectives)
        {
            return new Dictionary<string, string>();
        }
        var types = scope.Members.OfType<BaseTypeDeclarationSyntax>()
            .Where(type => type.Modifiers.Any(SyntaxKind.PublicKeyword)).ToArray();
        if (types.Length < 2)
        {
            return new Dictionary<string, string>();
        }

        var fileTypeName = Path.GetFileNameWithoutExtension(path).Split('.')[0];
        var primary = types.FirstOrDefault(type => type.Identifier.ValueText == fileTypeName)
            ?? types.FirstOrDefault(type => type.Modifiers.Any(SyntaxKind.PartialKeyword))
            ?? types[0];
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in types.Where(type => type != primary))
        {
            var destination = Path.Combine(Path.GetDirectoryName(path)!, type.Identifier.ValueText + ".cs");
            if (File.Exists(destination))
            {
                throw new InvalidOperationException($"公开类型拆分目标已经存在：{destination}");
            }
            result.Add(destination, root.ReplaceNode(scope, scope.WithMembers(SyntaxFactory.SingletonList<MemberDeclarationSyntax>(type))).ToFullString());
        }
        var retained = scope.Members.Where(member => member is not BaseTypeDeclarationSyntax type || !types.Contains(type) || type == primary);
        result.Add(path, root.ReplaceNode(scope, scope.WithMembers(SyntaxFactory.List(retained))).ToFullString());
        return result;
    }
}
