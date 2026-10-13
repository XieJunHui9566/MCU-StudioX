namespace StudioX.Application.StcTiming;

using StudioX.Engine;

public sealed record StcTimingContext(string ProjectDirectory, ProjectManifest Project, StcTimingProfile Profile,
    StcTimingSettings Settings, int? ProjectClockHz, bool Mon51, string EvidenceStamp)
{
    public int? ClockHz => ProjectClockHz ?? Settings.ReferenceClockHz;
}
