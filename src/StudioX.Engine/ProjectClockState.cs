namespace StudioX.Engine;

using StudioX.Foundation;

/// <summary>已保存配置求出的源频率和系统频率；错误状态也保持托管，不能回退旧频率。</summary>
public sealed record ProjectClockState(bool Managed, bool Ready, string Message, string? PluginId = null,
    string? SourceKind = null, int? SourceFrequencyHz = null, int? SystemFrequencyHz = null, int? SystemDivider = null,
    string? ConfigurationSha256 = null)
{
    public void RequireReady()
    {
        if (Managed && !Ready)
        {
            throw new StudioXException("PROJECT_CLOCK", Message);
        }
    }

}
