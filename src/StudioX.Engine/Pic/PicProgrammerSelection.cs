namespace StudioX.Engine.Pic;

using StudioX.Foundation;

/// <summary>没有设备时明确报错；只有多个不同序列号时才需要交互选择。</summary>
public static class PicProgrammerSelection
{
    public static PicProgrammer? Automatic(IReadOnlyList<PicProgrammer> programmers)
    {
        var unique = programmers.DistinctBy(p => p.Serial, StringComparer.Ordinal).ToArray();
        return unique.Length switch
        {
            0 => throw new StudioXException("PIC_PROGRAMMER_MISSING", "没有检测到 PICkit3，请检查 USB 连接。"),
            1 => unique[0],
            _ => null
        };
    }
}
