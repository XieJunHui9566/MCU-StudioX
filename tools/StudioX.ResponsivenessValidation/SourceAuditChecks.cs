using System.Diagnostics;
using System.Runtime.CompilerServices;
using StudioX.Application;
using StudioX.Application.CodeIntelligence;

internal static class SourceAuditChecks
{
    public static CheckResult Positions()
    {
        var watch = Stopwatch.StartNew();
        try
        {
            foreach (var text in new[] { "", "a\r\n文😀\n", "one\rtwo\r", "\n\n", "last line" })
            {
                for (var offset = 0; offset <= text.Length; offset++)
                {
                    var prefix = text[..offset];
                    var expected = new CodePosition(prefix.Count(c => c == '\n'), offset - prefix.LastIndexOf('\n') - 1);
                    Require(CodePositions.FromOffset(text, offset) == expected, "UTF-16 offset changed");
                }
                var start = 0;
                var lines = text.Split('\n');
                for (var line = 0; line < lines.Length; line++)
                {
                    var length = lines[line].EndsWith('\r') ? lines[line].Length - 1 : lines[line].Length;
                    for (var column = 0; column <= length; column++)
                    {
                        Require(CodePositions.ToOffset(text, new(line, column)) == start + column, "line/column changed");
                    }
                    Reject(() => CodePositions.ToOffset(text, new(line, length + 1)));
                    start += lines[line].Length + 1;
                }
                Reject(() => CodePositions.FromOffset(text, -1));
                Reject(() => CodePositions.FromOffset(text, text.Length + 1));
                Reject(() => CodePositions.ToOffset(text, new(lines.Length, 0)));
                Reject(() => CodePositions.ToOffset(text, new(-1, 0)));
            }
            var large = string.Concat(Enumerable.Repeat(new string('x', 99) + "\n", 20000));
            var timer = Stopwatch.StartNew();
            for (var i = 0; i < 10000; i++)
            {
                var offset = i * 200 + 40;
                var position = CodePositions.FromOffset(large, offset);
                Require(position == new CodePosition(i * 2, 40) && CodePositions.ToOffset(large, position) == offset, "large text position changed");
            }
            var elapsed = timer.Elapsed.TotalMilliseconds;
            Require(elapsed < 2000, "batch position conversion exceeded 2 seconds");
            Parallel.For(0, 2000, i => Require(CodePositions.ToOffset(large, CodePositions.FromOffset(large, i * 100)) == i * 100, "concurrent index read changed"));
            var weak = CreateReleasedSnapshot();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Require(!weak.IsAlive, "line index retained an obsolete document");
            return new("text-position-index", true, watch.ElapsedMilliseconds, $"2,000,000 characters / 10,000 round trips: {elapsed:F2} ms; UTF-16, CRLF, bounds, concurrency and weak lifetime passed");
        }
        catch (Exception ex)
        {
            return new("text-position-index", false, watch.ElapsedMilliseconds, ex.ToString());
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateReleasedSnapshot()
    {
        var text = new string('q', 10000) + "\n";
        _ = CodePositions.FromOffset(text, text.Length);
        return new(text);
    }

    public static CheckResult SearchCancellation()
    {
        var watch = Stopwatch.StartNew();
        try
        {
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            try
            {
                TextSearchService.Find("value", new("value", true, false, false), null, cancelled.Token);
                throw new InvalidOperationException("cancelled search ran");
            }
            catch (OperationCanceledException)
            {
            }
            var matches = TextSearchService.Find("value 12\r\nvalue 34", new("value (\\d+)", true, false, true), "n=$1");
            Require(matches.Count == 2 && matches[0].Replacement == "n=12" && matches[1].Replacement == "n=34", "regex replacement changed");
            return new("text-search-cancellation", true, watch.ElapsedMilliseconds, "cancelled work rejected; regex capture substitutions preserved");
        }
        catch (Exception ex)
        {
            return new("text-search-cancellation", false, watch.ElapsedMilliseconds, ex.ToString());
        }
    }

    private static void Require(bool value, string message)
    {
        if (!value)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void Reject(Action action)
    {
        try
        {
            action();
        }
        catch (ArgumentOutOfRangeException)
        {
            return;
        }
        throw new InvalidOperationException("invalid text coordinate accepted");
    }
}
