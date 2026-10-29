namespace StudioX.Application.Mcp;

using StudioX.Foundation;

/// <summary>只对已净化的源码快照计算关键词 BM25，不执行工程内容或外部程序。</summary>
internal static class SourceBm25Search
{
    internal sealed record Match(string Path, int Line, double Score, string Snippet);
    private sealed record Document(string Path, int Length, int[] Frequencies, int Line, string Snippet);

    internal static void ValidateQuery(string query, CancellationToken token) => _ = QueryTerms(query, token);

    private static List<string> QueryTerms(string query, CancellationToken token)
    {
        var terms = new List<string>();
        VisitWords(query, (word, _) =>
        {
            if (word.Length <= 160 && !terms.Contains(word, StringComparer.OrdinalIgnoreCase))
            {
                terms.Add(word);
            }
        }, token);
        if (terms.Count is < 1 or > 32)
        {
            throw new StudioXException("MCP_QMD_QUERY", "检索需要 1–32 个关键词，不支持仅标点的查询。");
        }
        return terms;
    }

    internal static async Task<IReadOnlyList<Match>> SearchAsync(string mirror,
        IReadOnlyDictionary<string, string> paths, string query, int maximum, CancellationToken token)
    {
        var terms = QueryTerms(query, token);
        var termIndexes = terms.Select((term, index) => (term, index))
            .ToDictionary(item => item.term, item => item.index, StringComparer.OrdinalIgnoreCase);
        var documents = new List<Document>();
        var documentFrequencies = new int[terms.Count];
        long totalLength = 0;
        foreach (var (name, path) in paths.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            var source = PathBoundary.Resolve(mirror, name);
            await using var stream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > 512 * 1024)
            {
                throw new StudioXException("MCP_QMD_SCOPE", "检索快照超过单文件大小限制。");
            }
            using var reader = new StreamReader(stream, new System.Text.UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false);
            var content = await reader.ReadToEndAsync(token).ConfigureAwait(false);
            var frequencies = new int[terms.Count];
            var length = 0;
            var firstMatch = -1;
            VisitWords(content, (word, offset) =>
            {
                length++;
                if (termIndexes.TryGetValue(word, out var index))
                {
                    frequencies[index]++;
                    if (firstMatch < 0)
                    {
                        firstMatch = offset;
                    }
                }
            }, token);
            for (var index = 0; index < frequencies.Length; index++)
            {
                if (frequencies[index] > 0)
                {
                    documentFrequencies[index]++;
                }
            }
            var line = 1;
            var start = Math.Max(0, firstMatch);
            for (var index = 0; index < start; index++)
            {
                if (content[index] == '\n')
                {
                    line++;
                }
            }
            // 结果字符预算按最坏 JSON 转义计算，不把长文件全文交给模型。
            var budget = Math.Min(800, 12000 / (maximum * 6));
            var snippet = firstMatch < 0 ? "" : content.Substring(start, Math.Min(budget, content.Length - start));
            documents.Add(new(path, length, frequencies, line, snippet));
            totalLength += length;
        }
        if (documents.Count == 0 || totalLength == 0)
        {
            return [];
        }
        var averageLength = (double)totalLength / documents.Count;
        const double k1 = 1.2, b = 0.75;
        var matches = new List<Match>();
        foreach (var document in documents)
        {
            token.ThrowIfCancellationRequested();
            var score = 0d;
            for (var index = 0; index < terms.Count; index++)
            {
                var frequency = document.Frequencies[index];
                if (frequency == 0)
                {
                    continue;
                }
                var inverseFrequency = Math.Log(1 +
                    (documents.Count - documentFrequencies[index] + 0.5) / (documentFrequencies[index] + 0.5));
                score += inverseFrequency * frequency * (k1 + 1) /
                    (frequency + k1 * (1 - b + b * document.Length / averageLength));
            }
            if (score > 0)
            {
                matches.Add(new(document.Path, document.Line, score, document.Snippet));
            }
        }
        return matches.OrderByDescending(item => item.Score).ThenBy(item => item.Path, StringComparer.Ordinal)
            .Take(maximum).ToArray();
    }

    private static void VisitWords(string text, Action<string, int> visit, CancellationToken token)
    {
        var start = -1;
        for (var index = 0; index <= text.Length; index++)
        {
            if ((index & 4095) == 0)
            {
                token.ThrowIfCancellationRequested();
            }
            if (index < text.Length && (char.IsLetterOrDigit(text[index]) || text[index] == '_'))
            {
                if (start < 0)
                {
                    start = index;
                }
            }
            else if (start >= 0)
            {
                // 极长的无分隔文本不分配大 token；普通标识符仍按完整词匹配。
                if (index - start <= 160)
                {
                    visit(text[start..index], start);
                }
                start = -1;
            }
        }
    }
}
