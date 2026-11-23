namespace StudioX.Application.CodeIntelligence;

/// <summary>复用注释、字符串及括号扫描；不依赖构建结果或语言服务器。</summary>
public static class CodeFolding
{
    public static bool Supports(string language) => language is "C" or "C++" or "JSON" or "Linker" or "Verilog";

    public static CodeFoldingRange[] Find(string text, string language, CancellationToken token = default)
    {
        if (!Supports(language))
        {
            return [];
        }
        if (language == "Verilog")
        {
            return FindVerilog(text, token);
        }
        var result = new List<CodeFoldingRange>();
        var opened = new Dictionary<int, (int Offset, int Line)>();
        var line = 0;
        var nextLine = text.IndexOf('\n');
        foreach (var bracket in BracketPairs.Find(text, language, token))
        {
            token.ThrowIfCancellationRequested();
            while (nextLine >= 0 && nextLine < bracket.Offset)
            {
                line++;
                nextLine = text.IndexOf('\n', nextLine + 1);
            }
            var character = text[bracket.Offset];
            if (character == '{' || language == "JSON" && character == '[')
            {
                opened[bracket.Depth] = (bracket.Offset, line);
            }
            else if ((character == '}' || language == "JSON" && character == ']') && opened.Remove(bracket.Depth, out var start) && line > start.Line)
            {
                result.Add(new(start.Offset + 1, bracket.Offset));
            }
        }
        return result.OrderBy(range => range.StartOffset).ToArray();
    }

    private static CodeFoldingRange[] FindVerilog(string text, CancellationToken token)
    {
        if (text.Length > 1_048_576)
        {
            return [];
        }
        var ranges = new List<CodeFoldingRange>();
        var blocks = new Stack<(string Closing, int EndOfOpening)>();
        foreach (var part in VerilogDocument.Read(text, token).Tokens)
        {
            token.ThrowIfCancellationRequested();
            var closing = part.Text switch
            {
                "module" => "endmodule",
                "interface" => "endinterface",
                "package" => "endpackage",
                "begin" => "end",
                "generate" => "endgenerate",
                "case" or "casex" or "casez" => "endcase",
                "function" => "endfunction",
                "task" => "endtask",
                _ => null
            };
            if (closing is not null)

            {
                blocks.Push((closing, part.End));
            }
            else if (blocks.TryPeek(out var block) && part.Text == block.Closing)
            {
                blocks.Pop();
                var start = CodePositions.FromOffset(text, block.EndOfOpening);
                var end = CodePositions.FromOffset(text, part.Offset);
                if (start.Line < end.Line)

                {
                    ranges.Add(new(block.EndOfOpening, part.Offset));
                }
            }
        }
        return ranges.OrderBy(range => range.StartOffset).ToArray();
    }
}
