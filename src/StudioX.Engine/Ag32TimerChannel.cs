namespace StudioX.Engine;

using System.Text.Json.Serialization;

public sealed record Ag32TimerChannel(decimal? Duty = null, bool Complementary = false,
    string? Edge = null, int Divider = 1, int Filter = 0, bool Interrupt = false)
{
    public string? CompareMode
    {
        get; init;
    }
    public uint? CompareTicks
    {
        get; init;
    }
    [JsonIgnore]
    public bool IsOutput => Duty.HasValue || CompareMode is not null;
}
