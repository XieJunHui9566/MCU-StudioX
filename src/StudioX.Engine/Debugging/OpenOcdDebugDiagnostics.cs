namespace StudioX.Engine.Debugging;

using StudioX.Foundation;

/// <summary>OpenOCD 监听端口成功并不代表目标已识别；保留目标检查的原始错误。</summary>
public static class OpenOcdDebugDiagnostics
{
    public static StudioXException? TargetExaminationFailure(string line)
    {
        if ((line.StartsWith("Error:", StringComparison.Ordinal) || line.StartsWith("Warn :", StringComparison.Ordinal)) &&
            (line.Contains("examination failed", StringComparison.OrdinalIgnoreCase) || line.Contains("Target not examined yet", StringComparison.Ordinal)))
        {
            return new("DEBUG_TARGET_EXAMINE", "OpenOCD 无法识别目标内核。请检查 SWDIO、SWCLK、GND、供电及复位状态。\n原始诊断：" + line);
        }
        return null;
    }
}
