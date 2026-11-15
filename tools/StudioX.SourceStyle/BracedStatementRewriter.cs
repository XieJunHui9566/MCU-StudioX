namespace StudioX.SourceStyle;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>补齐控制块并展开单行块；保留表达式、字符串、注释和预处理分支。</summary>
internal sealed class BracedStatementRewriter : CSharpSyntaxRewriter
{
    public override SyntaxNode? VisitBlock(BlockSyntax node)
    {
        var lines = node.GetLocation().GetLineSpan();
        var visited = (BlockSyntax)base.VisitBlock(node)!;
        // SDK 空白格式器仍保留部分 lambda/catch 单行块；先显式分行，再交给它统一缩进。
        var singleLine = lines.StartLinePosition.Line == lines.EndLinePosition.Line;
        if (!node.ContainsDirectives && singleLine)
        {
            visited = ExpandBlock(visited);
        }
        if (!node.ContainsDirectives && node.Parent is AnonymousFunctionExpressionSyntax function &&
            (singleLine || node.OpenBraceToken.GetLocation().GetLineSpan().StartLinePosition.Character == 0))
        {
            // SDK 以 lambda 开括号的现有列为缩进锚点，不能把新生成的括号留在第零列。
            var line = node.SyntaxTree.GetText().Lines.GetLineFromPosition(function.SpanStart).ToString();
            var sourceIndent = line.TakeWhile(character => character is ' ' or '\t').Count();
            var depth = node.Ancestors().Count(ancestor => ancestor is BlockSyntax or BaseTypeDeclarationSyntax or
                NamespaceDeclarationSyntax or AccessorListSyntax or SwitchStatementSyntax or SwitchSectionSyntax or InitializerExpressionSyntax);
            var trivia = visited.OpenBraceToken.LeadingTrivia;
            while (trivia.Count > 0 && trivia[^1].IsKind(SyntaxKind.WhitespaceTrivia))
            {
                trivia = trivia.RemoveAt(trivia.Count - 1);
            }
            visited = visited.WithOpenBraceToken(visited.OpenBraceToken.WithLeadingTrivia(
                trivia.Add(SyntaxFactory.Whitespace(new string(' ', Math.Max(sourceIndent, depth * 4))))));
        }
        return visited;
    }

    private static BlockSyntax ExpandBlock(BlockSyntax block)
    {
        static SyntaxTriviaList EndLine(SyntaxTriviaList trivia) => trivia.Any(item => item.IsKind(SyntaxKind.EndOfLineTrivia))
            ? trivia : trivia.Add(SyntaxFactory.EndOfLine("\n"));
        return block.WithOpenBraceToken(block.OpenBraceToken
                .WithLeadingTrivia(EndLine(block.OpenBraceToken.LeadingTrivia))
                .WithTrailingTrivia(EndLine(block.OpenBraceToken.TrailingTrivia)))
            .WithStatements(SyntaxFactory.List(block.Statements.Select(statement =>
                statement.WithTrailingTrivia(EndLine(statement.GetTrailingTrivia())))));
    }

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
        return ExpandBlock(SyntaxFactory.Block(statement.WithoutLeadingTrivia().WithoutTrailingTrivia())
            .WithTriviaFrom(statement));
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
