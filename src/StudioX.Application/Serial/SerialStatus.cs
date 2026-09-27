namespace StudioX.Application.Serial;

using StudioX.Devices;
public sealed record SerialStatus(bool Connected, string Message, long Received, long Sent, long DroppedFrames,
    long DiscardedHistoryBytes, SerialPins? Pins, string[] Errors);
