namespace StudioX.EspressifRegistryValidation;

internal sealed class RegistryClock : TimeProvider
{
    private DateTimeOffset now = new(2026, 9, 27, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => now;

    public void Advance(TimeSpan duration) => now += duration;
}
