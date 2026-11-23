namespace StudioX.Engine;

using System.Text.Json.Serialization;

public sealed record Ag32TimerSettings(string Module, decimal FrequencyHz,
    Ag32TimerChannel? Ch0 = null, Ag32TimerChannel? Ch1 = null, Ag32TimerChannel? Ch2 = null, Ag32TimerChannel? Ch3 = null,
    string? Encoder = null, uint EncoderTop = uint.MaxValue, int EncoderFilter = 0, decimal DeadTimeNs = 0, string? BreakPolarity = null)
{
    public bool UpdateInterrupt
    {
        get; init;
    }
    public bool BreakInterrupt
    {
        get; init;
    }
    public int InterruptPriority { get; init; } = 1;
    [JsonIgnore]
    public Ag32TimerChannel?[] Channels => [Ch0, Ch1, Ch2, Ch3];
}
