namespace StudioX.Application.StcTiming;

public sealed record StcTimingPreview(string FunctionName, string Code, string Summary,
    IReadOnlyList<string> Resources, long TimerTicks, int Reload, decimal ActualValue,
    decimal ErrorPercent, bool CanInsert, string Notice);
