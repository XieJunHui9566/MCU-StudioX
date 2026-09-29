namespace StudioX.Application.Editing;

/// <summary>用户布局与源码、工具锁定及恢复草稿分别存储。</summary>
public sealed record WorkbenchLayout(double Width = 1460, double Height = 920, bool Maximized = false,
    double ProjectWidth = 230, double BottomHeight = 150, double SplitRatio = .5, bool ProjectVisible = true, bool BottomVisible = true, bool OutlineVisible = true);
