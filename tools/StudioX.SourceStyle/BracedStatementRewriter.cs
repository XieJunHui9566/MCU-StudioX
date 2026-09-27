namespace StudioX.SourceStyle;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>仅为嵌入的控制语句补块；不改写表达式、字符串、注释或预处理分支。</summary>
internal sealed class BracedStatementRewriter : CSharpSyntaxRewriter
{
    public override SyntaxNode? VisitFileScopedNamespaceDeclaration(FileScopedNamespaceDeclarationSyntax node)
    {
        var visited = (FileScopedNamespaceDeclarationSyntax)base.VisitFileScopedNamespaceDeclaration(node)!;
        var trivia = visited.SemicolonToken.TrailingTrivia;
        var next = (SyntaxNode?)visited.Externs.FirstOrDefault() ??
            (SyntaxNode?)visited.Usings.FirstOrDefault() ?? visited.Members.FirstOrDefault();
        if (next is null || trivia.Any(item => !IsWhitespace(item)))
        {
            return visited;
        }
        var leading = SyntaxFactory.TriviaList(next.GetLeadingTrivia().SkipWhile(IsWhitespace));
        visited = visited.ReplaceNode(next, next.WithLeadingTrivia(leading));
        return visited.WithSemicolonToken(visited.SemicolonToken.WithTrailingTrivia(
            SyntaxFactory.EndOfLine("\n"), SyntaxFactory.EndOfLine("\n")));
    }

    private static bool IsWhitespace(SyntaxTrivia trivia) =>
        trivia.IsKind(SyntaxKind.WhitespaceTrivia) || trivia.IsKind(SyntaxKind.EndOfLineTrivia);

    private static StatementSyntax Enclose(StatementSyntax statement)
    {
        if (statement is BlockSyntax || statement.ContainsDirectives)
        {
            return statement;
        }
        return SyntaxFactory.Block(statement.WithoutLeadingTrivia().WithoutTrailingTrivia())
            .WithTriviaFrom(statement);
    }

    public override SyntaxNode? VisitIfStatement(IfStatementSyntax node)
    {
        var visited = (IfStatementSyntax)base.VisitIfStatement(node)!;
        return visited.WithStatement(Enclose(visited.Statement));
    }

    public override SyntaxNode? VisitElseClause(ElseClauseSyntax node)
    {
        var visited = (ElseClauseSyntax)base.VisitElseClause(node)!;
        return visited.Statement is IfStatementSyntax ? visited : visited.WithStatement(Enclose(visited.Statement));
    }

    public override SyntaxNode? VisitForStatement(ForStatementSyntax node)
    {
        var visited = (ForStatementSyntax)base.VisitForStatement(node)!;
        return visited.WithStatement(Enclose(visited.Statement));
    }

    public override SyntaxNode? VisitForEachStatement(ForEachStatementSyntax node)
    {
        var visited = (ForEachStatementSyntax)base.VisitForEachStatement(node)!;
        return visited.WithStatement(Enclose(visited.Statement));
    }

    public override SyntaxNode? VisitForEachVariableStatement(ForEachVariableStatementSyntax node)
    {
        var visited = (ForEachVariableStatementSyntax)base.VisitForEachVariableStatement(node)!;
        return visited.WithStatement(Enclose(visited.Statement));
    }

    public override SyntaxNode? VisitWhileStatement(WhileStatementSyntax node)
    {
        var visited = (WhileStatementSyntax)base.VisitWhileStatement(node)!;
        return visited.WithStatement(Enclose(visited.Statement));
    }

    public override SyntaxNode? VisitDoStatement(DoStatementSyntax node)
    {
        var visited = (DoStatementSyntax)base.VisitDoStatement(node)!;
        return visited.WithStatement(Enclose(visited.Statement));
    }

    public override SyntaxNode? VisitUsingStatement(UsingStatementSyntax node)
    {
        var visited = (UsingStatementSyntax)base.VisitUsingStatement(node)!;
        return visited.WithStatement(Enclose(visited.Statement));
    }

    public override SyntaxNode? VisitLockStatement(LockStatementSyntax node)
    {
        var visited = (LockStatementSyntax)base.VisitLockStatement(node)!;
        return visited.WithStatement(Enclose(visited.Statement));
    }

    public override SyntaxNode? VisitFixedStatement(FixedStatementSyntax node)
    {
        var visited = (FixedStatementSyntax)base.VisitFixedStatement(node)!;
        return visited.WithStatement(Enclose(visited.Statement));
    }
}
