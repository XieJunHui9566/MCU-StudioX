namespace StudioX.Application.Lvgl;

using StudioX.Engine;
using StudioX.Engine.Lvgl;

public sealed record LvglPreviewResourceReport(BuildMemoryReport TargetBuild, LvglPreviewStats? PcStats,
    long DrawBufferBytes, long PcFramebufferBytes, int PcPointerBits, string? LvglVersion,
    string Evidence, string Limitations, LvglDisplayEstimate? DisplayTransfer = null,
    LvglTargetBuildEvidence? TargetEvidence = null);
