namespace StudioX.Engine.Pic;

/// <summary>IPECMD 枚举到的准确 PICkit3；序列号用于绑定审批与设备所有权。</summary>
public sealed record PicProgrammer(string Serial)
{
    public string DisplayName => "PICkit3 · " + Serial;
}
