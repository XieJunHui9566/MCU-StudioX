namespace StudioX.Engine.Lvgl;

using StudioX.Foundation;

/// <summary>像素净荷模型；传输上限不包含渲染、命令与总线空隙，不能视为实板 FPS。</summary>
public sealed record LvglDisplayEstimate(long FullFrameBytes, int RequestedFramesPerSecond,
    long RequiredBytesPerSecond, long? CalibratedBytesPerSecond, double? FullFrameTransportLimitFps,
    string? CalibrationSource)
{
    public static LvglDisplayEstimate Calculate(LvglPreviewConfiguration configuration) =>
        Calculate(configuration.Width, configuration.Height, configuration.ColorDepth,
            configuration.TargetFramesPerSecond, configuration.DisplayBandwidthBytesPerSecond,
            configuration.DisplayBandwidthSource);

    public static LvglDisplayEstimate Calculate(int width, int height, int colorDepth, int framesPerSecond,
        long? bandwidthBytesPerSecond = null, string? calibrationSource = null)
    {
        if (width is < 1 or > 4096 || height is < 1 or > 4096 || colorDepth is not (16 or 32) ||
            framesPerSecond is < 1 or > 240 || bandwidthBytesPerSecond is <= 0 or > 1_000_000_000_000)
        {
            throw new StudioXException("LVGL_TRANSFER_MODEL", "像素带宽模型参数无效。");
        }
        var bytes = checked((long)width * height * (colorDepth / 8));
        return new(bytes, framesPerSecond, checked(bytes * framesPerSecond), bandwidthBytesPerSecond,
            bandwidthBytesPerSecond is { } measured ? (double)measured / bytes : null,
            bandwidthBytesPerSecond is null ? null : calibrationSource);
    }
}
