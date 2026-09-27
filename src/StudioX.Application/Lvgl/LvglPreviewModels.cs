namespace StudioX.Application.Lvgl;

public sealed record LvglPreviewStats(long UptimeMs, long Frames, double Fps, long FlushPixels,
    long HeapUsedBytes, long HeapFreeBytes, long HeapLargestFreeBytes, long HeapObservedPeakBytes,
    double HeapFragmentationPercent, long WarningCount, long ErrorCount, bool HeapAvailable = true);
