using System.Diagnostics;
using StudioX.Desktop;

internal static class OutputChecks
{
    public static CheckResult Run()
    {
        var watch = Stopwatch.StartNew();
        try
        {
            var view = new OutputLogView { Text = new string('x', 200000) + "\n" };
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 2000; i++)
            {
                view.AppendLogLine("short tool diagnostic");
            }
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            if (allocated > 64 * 1024 * 1024)
            {
                throw new InvalidOperationException($"短日志追加分配了 {allocated} 字节，超过 64 MiB。");
            }
            if (view.Document.LineCount != 2002 || view.Document.TextLength != 244001)
            {
                throw new InvalidOperationException("日志行或内容丢失。");
            }
            view.Text = "partial";
            view.AppendLogLine("next\r\n");
            view.UpdateProgress("working 10%");
            view.AppendLogLine("warning");
            view.UpdateProgress("working 100%");
            view.CommitProgress();
            view.AppendLogLine("done");
            if (view.Text != "partial\nnext\nwarning\nworking 100%\ndone\n")
            {
                throw new InvalidOperationException("换行、进度替换或诊断插入顺序错误：" + view.Text);
            }
            view.Text = "partial";
            view.UpdateProgress("progress");
            if (view.Text != "partial\nprogress\n")
            {
                throw new InvalidOperationException("进度没有从新行开始。");
            }
            return new("output-allocation-and-progress", true, watch.ElapsedMilliseconds, $"2000 lines, {allocated} allocated bytes; line/progress checks passed");
        }
        catch (Exception error) { return new("output-allocation-and-progress", false, watch.ElapsedMilliseconds, error.ToString()); }
    }
}
