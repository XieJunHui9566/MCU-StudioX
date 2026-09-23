namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Media;

/// <summary>按 git log 输出顺序排列的一个提交及其父提交。</summary>
public sealed record GitGraphCommit(string Hash, IReadOnlyList<string> Parents);

/// <summary>一段提交轨道。纵向位置 0、0.5、1 分别是本行顶部、提交点和底部。</summary>
public sealed record GitGraphSegment(int StartLane, double StartLevel, int EndLane, double EndLevel, int ColorIndex);

/// <summary>一行提交图的完整绘制数据，不包含 WPF 对象，可直接绑定到 GitGraphGlyph。</summary>
public sealed record GitGraphRow(
    string Hash,
    int NodeLane,
    int NodeColorIndex,
    bool IsMerge,
    IReadOnlyList<GitGraphSegment> Segments,
    int LaneCount,
    int TotalLaneCount);

/// <summary>由提交和父提交的拓扑关系生成稳定的列轨道；输入须按 git log 从新到旧排列。</summary>
public static class GitGraphLayout
{
    private sealed record Track(string TargetHash, int ColorIndex);

    public static IReadOnlyList<GitGraphRow> Build(IReadOnlyList<(string Hash, IReadOnlyList<string> Parents)> commits)
        => Build(commits.Select(commit => new GitGraphCommit(commit.Hash, commit.Parents)));

    public static IReadOnlyList<GitGraphRow> Build(IEnumerable<GitGraphCommit> commits)
    {
        ArgumentNullException.ThrowIfNull(commits);
        var ordered = commits.ToArray();
        var remaining = new HashSet<string>(ordered.Select(commit => commit.Hash), StringComparer.OrdinalIgnoreCase);
        var pending = new List<Track>();
        var rows = new List<GitGraphRow>(ordered.Length);
        var nextColor = 0;
        var greatestLaneCount = 1;

        foreach (var commit in ordered)
        {
            if (string.IsNullOrWhiteSpace(commit.Hash))
                throw new ArgumentException("提交哈希不能为空。", nameof(commits));
            remaining.Remove(commit.Hash);

            var incoming = pending.ToArray();
            var matches = Enumerable.Range(0, incoming.Length)
                .Where(index => string.Equals(incoming[index].TargetHash, commit.Hash, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            // 不在现有轨道的提交是另一个分支的头；放在当前轨道右侧，不扰动已有分支。
            var nodeLane = matches.Length == 0 ? incoming.Length : matches[0];
            var nodeColor = matches.Length == 0 ? nextColor++ : incoming[nodeLane].ColorIndex;
            var segments = new List<GitGraphSegment>();

            foreach (var index in matches)
                segments.Add(new(index, 0, nodeLane, 0.5, incoming[index].ColorIndex));

            var matched = matches.ToHashSet();
            var outgoing = Enumerable.Range(0, incoming.Length)
                .Where(index => !matched.Contains(index))
                .Select(index => incoming[index]).ToList();

            // 第一父提交继承当前轨道，其余父提交开启并行轨道；已存在的父轨道直接汇入。
            var insertAt = Math.Min(nodeLane, outgoing.Count);
            var parentTracks = new List<Track>();
            foreach (var parentHash in (commit.Parents ?? []).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(parentHash)) continue;
                // 搜索或分支筛选可能隐去父提交；不为不可见提交保留永久悬空的轨道。
                if (!remaining.Contains(parentHash)) continue;
                var track = outgoing.FirstOrDefault(candidate =>
                    string.Equals(candidate.TargetHash, parentHash, StringComparison.OrdinalIgnoreCase));
                if (track is null)
                {
                    track = new Track(parentHash, parentTracks.Count == 0 ? nodeColor : nextColor++);
                    outgoing.Insert(insertAt, track);
                    insertAt++;
                }
                else if (parentTracks.Count == 0)
                {
                    // 第一父提交已经占有轨道时，让后续父轨道接在其右侧。
                    insertAt = outgoing.IndexOf(track) + 1;
                }
                parentTracks.Add(track);
            }

            for (var oldLane = 0; oldLane < incoming.Length; oldLane++)
            {
                if (matched.Contains(oldLane)) continue;
                var newLane = outgoing.IndexOf(incoming[oldLane]);
                segments.Add(new(oldLane, 0, newLane, 1, incoming[oldLane].ColorIndex));
            }
            foreach (var track in parentTracks)
                segments.Add(new(nodeLane, 0.5, outgoing.IndexOf(track), 1, track.ColorIndex));

            var laneCount = Math.Max(Math.Max(incoming.Length, outgoing.Count), nodeLane + 1);
            greatestLaneCount = Math.Max(greatestLaneCount, laneCount);
            rows.Add(new GitGraphRow(commit.Hash, nodeLane, nodeColor,
                (commit.Parents?.Distinct(StringComparer.OrdinalIgnoreCase).Count() ?? 0) > 1,
                segments.ToArray(), laneCount, 0));
            pending = outgoing;
        }

        // 所有行使用相同的列宽，避免分支数量变化时轨道在相邻行错位。
        return rows.Select(row => row with { TotalLaneCount = greatestLaneCount }).ToArray();
    }
}

/// <summary>轻量 WPF 提交图标；每行由 GitGraphLayout 计算，绘制由当前主题承载。</summary>
public sealed class GitGraphGlyph : FrameworkElement
{
    public static readonly DependencyProperty RowProperty = DependencyProperty.Register(
        nameof(Row), typeof(GitGraphRow), typeof(GitGraphGlyph),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    // 兼容把布局属性命名为 Layout 的调用方。
    public static readonly DependencyProperty LayoutProperty = DependencyProperty.Register(
        nameof(Layout), typeof(GitGraphRow), typeof(GitGraphGlyph),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public GitGraphRow? Row
    {
        get => (GitGraphRow?)GetValue(RowProperty);
        set => SetValue(RowProperty, value);
    }

    public GitGraphRow? Layout
    {
        get => (GitGraphRow?)GetValue(LayoutProperty);
        set => SetValue(LayoutProperty, value);
    }

    private static readonly Brush[] Colors =
    [
        MakeBrush(0x62, 0xA9, 0xF5), MakeBrush(0xE8, 0xA4, 0x62),
        MakeBrush(0xA4, 0x86, 0xE8), MakeBrush(0x69, 0xBD, 0xA5),
        MakeBrush(0xE5, 0x7F, 0x94), MakeBrush(0xD3, 0xB1, 0x61),
        MakeBrush(0x8A, 0xB8, 0xDA), MakeBrush(0xB5, 0xC6, 0x75)
    ];

    private static Brush MakeBrush(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var row = Row ?? Layout;
        if (row is null || ActualWidth <= 0 || ActualHeight <= 0) return;

        var spacing = Math.Min(16d, Math.Max(1d, (ActualWidth - 24d) / Math.Max(1, row.TotalLaneCount - 1)));
        double X(int lane) => 12d + lane * spacing;
        double Y(double level) => level * ActualHeight;

        foreach (var segment in row.Segments)
        {
            var start = new Point(X(segment.StartLane), Y(segment.StartLevel));
            var end = new Point(X(segment.EndLane), Y(segment.EndLevel));
            var pen = new Pen(Colors[Math.Abs(segment.ColorIndex % Colors.Length)], 2)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round
            };
            if (segment.StartLane == segment.EndLane)
            {
                drawingContext.DrawLine(pen, start, end);
            }
            else
            {
                var bend = (end.Y - start.Y) * 0.52;
                var curve = new StreamGeometry();
                using (var context = curve.Open())
                {
                    context.BeginFigure(start, false, false);
                    context.BezierTo(new(start.X, start.Y + bend), new(end.X, end.Y - bend), end, true, false);
                }
                curve.Freeze();
                drawingContext.DrawGeometry(null, pen, curve);
            }
        }

        var center = new Point(X(row.NodeLane), ActualHeight / 2);
        var radius = row.IsMerge ? 5d : 4d;
        drawingContext.DrawEllipse(Colors[Math.Abs(row.NodeColorIndex % Colors.Length)], null, center, radius, radius);
    }
}
