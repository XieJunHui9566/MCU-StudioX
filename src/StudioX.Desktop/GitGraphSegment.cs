namespace StudioX.Desktop;

/// <summary>一段提交轨道。纵向位置 0、0.5、1 分别是本行顶部、提交点和底部。</summary>
public sealed record GitGraphSegment(int StartLane, double StartLevel, int EndLane, double EndLevel, int ColorIndex);
