namespace StudioX.Application.Editing;

/// <summary>有界行差异，用于审阅，不参与修改计划的应用或撤销。</summary>
public sealed record CodeComparison(IReadOnlyList<CodeChangeRange> Changes, bool IsCoarse)
{
    public static CodeComparison Create(string before, string after)
    {
        EditorTextSafety.Validate(before);
        EditorTextSafety.Validate(after);
        if (before == after)
        {
            return new([], false);
        }
        var left = Lines(before);
        var right = Lines(after);
        var prefix = 0;
        var leftEnd = left.Length - 1;
        var rightEnd = right.Length - 1;
        bool Equal(int a, int b) => before.AsSpan(left[a], left[a + 1] - left[a]).SequenceEqual(after.AsSpan(right[b], right[b + 1] - right[b]));
        while (prefix < leftEnd && prefix < rightEnd && Equal(prefix, prefix))
        {
            prefix++;
        }
        while (leftEnd > prefix && rightEnd > prefix && Equal(leftEnd - 1, rightEnd - 1))
        {
            leftEnd--;
            rightEnd--;
        }

        // 限制编辑距离、比较字节和回溯内存，完全不同的大文件也不能锁住 UI。
        const int limit = 256;
        const int center = limit + 1;
        var vector = new int[2 * limit + 3];
        Array.Fill(vector, -1);
        vector[center + 1] = 0;
        var trace = new List<int[]>();
        var work = 0L;
        var budget = Math.Clamp(((long)before.Length + after.Length) * 4, 250000, 16000000);
        var n = leftEnd - prefix;
        var m = rightEnd - prefix;
        for (var distance = 0; distance <= limit; distance++)
        {
            trace.Add((int[])vector.Clone());
            for (var diagonal = -distance; diagonal <= distance; diagonal += 2)
            {
                var index = center + diagonal;
                var x = diagonal == -distance || diagonal != distance && vector[index - 1] < vector[index + 1]
                    ? vector[index + 1] : vector[index - 1] + 1;
                var y = x - diagonal;
                while (x < n && y < m)
                {
                    work += Math.Max(left[prefix + x + 1] - left[prefix + x], right[prefix + y + 1] - right[prefix + y]) + 1;
                    if (work > budget)
                    {
                        return Coarse();
                    }
                    if (!Equal(prefix + x, prefix + y))
                    {
                        break;
                    }
                    x++;
                    y++;
                }
                vector[index] = x;
                if (x >= n && y >= m)
                {
                    return Build(Backtrack(distance));
                }
            }
        }
        return Coarse();

        List<(int Left, int Right)> Backtrack(int distance)
        {
            var matches = new List<(int, int)>();
            var x = n;
            var y = m;
            for (var d = distance; d > 0; d--)
            {
                var previous = trace[d];
                var k = x - y;
                var previousK = k == -d || k != d && previous[center + k - 1] < previous[center + k + 1] ? k + 1 : k - 1;
                var previousX = previous[center + previousK];
                var previousY = previousX - previousK;
                while (x > previousX && y > previousY)
                {
                    matches.Add((prefix + --x, prefix + --y));
                }
                x = previousX;
                y = previousY;
            }
            while (x > 0 && y > 0)
            {
                matches.Add((prefix + --x, prefix + --y));
            }
            matches.Reverse();
            return matches;
        }
        CodeComparison Build(List<(int Left, int Right)> matches)
        {
            var changes = new List<CodeChangeRange>();
            var a = prefix;
            var b = prefix;
            matches.Add((leftEnd, rightEnd));
            foreach (var (nextA, nextB) in matches)
            {
                if (a != nextA || b != nextB)
                {
                    changes.Add(Trim(left[a], left[nextA], right[b], right[nextB]));
                }
                a = nextA + 1;
                b = nextB + 1;
            }
            return new(changes, false);
        }
        CodeComparison Coarse() => new([Trim(left[prefix], left[leftEnd], right[prefix], right[rightEnd])], true);
        CodeChangeRange Trim(int a, int endA, int b, int endB)
        {
            while (a < endA && b < endB && before[a] == after[b])
            {
                a++;
                b++;
            }
            while (endA > a && endB > b && before[endA - 1] == after[endB - 1])
            {
                endA--;
                endB--;
            }
            // UTF-16 的代理对必须作为整体标记，不能把一半字符当作差异边界。
            if (a > 0 && a < before.Length && char.IsLowSurrogate(before[a])) { a--; b--; }
            if (endA < before.Length && endA > a && char.IsLowSurrogate(before[endA])) { endA++; endB++; }
            return new(a, endA - a, b, endB - b);
        }
    }

    private static int[] Lines(string text)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is '\r' or '\n')
            {
                if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }
                starts.Add(i + 1);
            }
        }
        if (starts[^1] != text.Length)
        {
            starts.Add(text.Length);
        }
        return starts.ToArray();
    }
}
