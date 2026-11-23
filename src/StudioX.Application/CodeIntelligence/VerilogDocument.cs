namespace StudioX.Application.CodeIntelligence;

using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

internal sealed partial class VerilogDocument
{
    internal const string Keywords = "always always_comb always_ff always_latch assign automatic begin bit case casex casez default disable do else end endcase endfunction endgenerate endinterface endmodule endpackage endtask for foreach forever function generate genvar if initial inout input int integer interface localparam logic module negedge output package parameter posedge reg repeat return signed task time typedef unsigned var wait while wire";
    private static readonly HashSet<string> Reserved = new((Keywords + " const real realtime tri uwire wand wor byte shortint longint supply0 supply1 struct union enum static string import virtual void").Split(' '), StringComparer.Ordinal);
    private static readonly HashSet<string> Declarations = new("input output inout parameter localparam wire reg logic bit integer genvar int byte shortint longint real realtime time tri uwire wand wor".Split(' '), StringComparer.Ordinal);
    private static readonly ConditionalWeakTable<string, VerilogDocument> Documents = new();

    internal readonly record struct Token(string Text, int Offset)
    {
        public int End => Offset + Text.Length;
        public bool Identifier => Text.Length > 0 && (char.IsAsciiLetter(Text[0]) || Text[0] == '_') && !Reserved.Contains(Text);
        public bool Hidden => Text[0] == '"' || Text.StartsWith("//", StringComparison.Ordinal) || Text.StartsWith("/*", StringComparison.Ordinal);
    }
    internal sealed record Symbol(Token Name, string Role, int Kind, int Scope, int ScopeEnd, IReadOnlyList<Symbol> Members);
    private sealed record Scope(Token Name, string Role, int Start, int End);

    internal Token[] Tokens
    {
        get;
    }
    internal Symbol[] Symbols
    {
        get;
    }

    internal static VerilogDocument Read(string text, CancellationToken token) =>
        Documents.GetValue(text, value => new VerilogDocument(value, token));

    private VerilogDocument(string text, CancellationToken cancellation)
    {
        var tokens = new List<Token>();
        var significant = new List<Token>();
        foreach (Match match in TokenPattern().Matches(text))
        {
            cancellation.ThrowIfCancellationRequested();
            var token = new Token(match.Value, match.Index);
            tokens.Add(token);
            if (!token.Hidden)
            {
                significant.Add(token);
            }
        }
        Tokens = tokens.ToArray();
        var code = significant.ToArray();
        var scopes = new List<Scope>();
        int[] owners = new int[code.Length];
        int owner = -1;
        for (int i = 0; i < code.Length; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            string word = code[i].Text;
            if (word is "endmodule" or "endinterface" or "endpackage")
            {
                if (owner >= 0)
                {
                    scopes[owner] = scopes[owner] with
                    {
                        End = code[i].End
                    };
                }
                owner = -1;
            }
            int name = i + (At(code, i + 1).Text is "automatic" or "static" ? 2 : 1);
            if ((word is "module" or "interface" or "package") && At(code, name).Identifier)
            {
                owner = scopes.Count;
                scopes.Add(new(code[name], word, code[i].Offset, text.Length));
            }
            owners[i] = owner;
        }
        var symbols = new List<Symbol>();
        var members = scopes.Select(_ => new List<Symbol>()).ToArray();
        for (int i = 0; i < code.Length; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            var role = code[i].Text;
            if (role == "`define" && At(code, i + 1).Identifier)
            {
                Add(code[i + 1], "宏", 14, -1);
                continue;
            }
            if (role is "function" or "task")
            {
                int end = i + 1;
                Token name = default;
                while (end < code.Length && At(code, end).Text is not ("(" or ";" or "endmodule"))
                {
                    if (code[end].Identifier)
                    {
                        name = code[end];
                    }
                    end++;
                }
                if (name.Text is not null)
                {
                    Add(name, role, 12, owners[i]);
                }
                i = end - 1;
                continue;
            }
            if (!Declarations.Contains(role))
            {
                continue;
            }

            // 每个逗号项取赋值或非打包维度之前的最后一个标识符，跳过位宽及初值中的表达式。
            int depth = 0;
            bool afterName = false;
            Token declaration = default;
            int j = i + 1;
            for (; j < code.Length; j++)
            {
                cancellation.ThrowIfCancellationRequested();
                var part = code[j];
                if (depth == 0 && (part.Text is ")" or ";" or "endmodule" or "endinterface" or "endpackage" ||
                    (part.Text is "input" or "output" or "inout" or "parameter" or "localparam")))

                {
                    break;
                }
                if (depth == 0 && part.Text == ",")
                {
                    Emit();
                    declaration = default;
                    afterName = false;
                    continue;
                }
                if (depth == 0 && part.Text == "=")

                {
                    afterName = true;
                }
                if (part.Text is "(" or "[" or "{")
                {
                    depth++;
                }
                else if (part.Text is ")" or "]" or "}")
                {
                    depth--;
                }
                else if (depth == 0 && !afterName && part.Identifier)
                {
                    declaration = part;
                }
            }
            Emit();
            i = j - 1;
            void Emit()
            {
                if (declaration.Text is not null)

                {
                    Add(declaration, role, role is "input" or "output" or "inout" ? 8 :
                                            role is "parameter" or "localparam" ? 14 : 13, owners[i]);
                }
            }
        }
        symbols.AddRange(scopes.Select((scope, index) =>
            new Symbol(scope.Name, scope.Role, 5, scope.Start, scope.End, members[index])));
        Symbols = symbols.OrderBy(s => s.Name.Offset).ToArray();

        void Add(Token name, string role, int kind, int scope)
        {
            var symbol = new Symbol(name, role, kind, scope < 0 ? -1 : scopes[scope].Start, scope < 0 ? text.Length : scopes[scope].End, []);
            symbols.Add(symbol);
            if (scope >= 0)
            {
                members[scope].Add(symbol);
            }
        }
    }

    internal Token? SymbolAt(int offset)
    {
        var previous = Tokens.LastOrDefault(t => t.Offset <= offset);
        if (previous.Text is not null && previous.End >= offset && (previous.Identifier || previous.Text.StartsWith('`')))

        {
            return previous;
        }
        return null;
    }

    internal bool InCommentOrString(int offset)
    {
        var previous = Tokens.LastOrDefault(t => t.Offset < offset);
        if (previous.Text is null || !previous.Hidden || previous.End < offset)
        {
            return false;
        }
        return previous.End > offset || previous.Text.StartsWith("//", StringComparison.Ordinal) ||
            (previous.Text[0] == '"' ? previous.Text.Length == 1 || !previous.Text.EndsWith('"') : !previous.Text.EndsWith("*/", StringComparison.Ordinal));
    }

    internal (string Module, bool Parameters, HashSet<string> Used)? NamedConnections(int dot)
    {
        var tokens = Tokens.TakeWhile(t => t.Offset < dot).Where(t => !t.Hidden).ToArray();
        var used = new HashSet<string>(StringComparer.Ordinal);
        int depth = 0;
        for (int i = tokens.Length - 1; i >= 0; i--)
        {
            var word = tokens[i].Text;
            if (word == ")")
            {
                depth++;
            }
            else if (word == "(")
            {
                if (depth-- == 0)
                {
                    bool parameters = At(tokens, i - 1).Text == "#";
                    if (parameters || At(tokens, i - 1).Identifier)
                    {
                        int type = i - 2;
                        if (At(tokens, type).Text == ")")
                        {
                            int nested = 1;
                            while (--type >= 0 && nested > 0)

                            {
                                nested += tokens[type].Text switch
                                {
                                    ")" => 1,
                                    "(" => -1,
                                    _ => 0
                                };
                            }
                            if (At(tokens, type).Text == "#")
                            {
                                type--;
                            }
                        }
                        if (At(tokens, type).Identifier)
                        {
                            return (tokens[type].Text, parameters, used);
                        }
                    }
                    return null;
                }
            }
            if (depth == 0 && word == "." && At(tokens, i + 1).Identifier && At(tokens, i + 2).Text == "(")

            {
                used.Add(tokens[i + 1].Text);
            }
        }
        return null;
    }

    private static Token At(Token[] tokens, int index) => (uint)index < (uint)tokens.Length ? tokens[index] : new("", 0);

    [GeneratedRegex("""//[^\r\n]*|/\*[\s\S]*?(?:\*/|\z)|"(?:\\[^\r\n]|[^"\\\r\n])*"?|\\[^\s]+|(?:[0-9][0-9_]*\s*)?'[sS]?[bBoOdDhH][0-9a-fA-F_xXzZ?]+|'[01xXzZ]|[0-9][\w]*|[`$]?[A-Za-z_][A-Za-z0-9_$]*|\S""")]
    private static partial Regex TokenPattern();
}
