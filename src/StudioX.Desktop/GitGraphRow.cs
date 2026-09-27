namespace StudioX.Desktop;

/// <summary>一行提交图的完整绘制数据，不包含 WPF 对象，可直接绑定到 GitGraphGlyph。</summary>
public sealed record GitGraphRow(
    string Hash,
    int NodeLane,
    int NodeColorIndex,
    bool IsMerge,
    IReadOnlyList<GitGraphSegment> Segments,
    int LaneCount,
    int TotalLaneCount);
