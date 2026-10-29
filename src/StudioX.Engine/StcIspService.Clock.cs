namespace StudioX.Engine;

using StudioX.Foundation;

public sealed partial class StcIspService
{
    private static StcIspSettings ApplyClock(ProjectClockState clock, StcIspSettings transport)
    {
        if (!clock.Managed)
        {
            return transport;
        }
        return transport with
        {
            ClockMode = !clock.Ready ? StcClockMode.Preserve : clock.SourceKind switch
            {
                "internalRc" => StcClockMode.InternalRc,
                "externalCrystal" => StcClockMode.ExternalCrystal,
                _ => throw new StudioXException("PROJECT_CLOCK", "STC 下载器不支持此时钟源类型。")
            },
            ClockFrequencyHz = clock.Ready ? clock.SourceFrequencyHz : null
        };
    }
}
