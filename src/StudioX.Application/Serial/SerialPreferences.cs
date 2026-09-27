namespace StudioX.Application.Serial;

using StudioX.Devices;
public sealed record SerialPreferences(SerialSettings Connection, SerialTextMode ReceiveMode = SerialTextMode.Utf8,
    SerialTextMode SendMode = SerialTextMode.Utf8, SerialLineEnding Ending = SerialLineEnding.CrLf, bool Ansi = true,
    bool Timestamps = false, bool Escapes = false, string[]? History = null);
