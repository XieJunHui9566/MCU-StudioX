namespace StudioX.Engine;

public sealed record Ag32UartSettings(int Port, int Baud, bool ReceiveInterrupt)
{
    public string Module => $"UART{Port}";
    public int DataBits { get; init; } = 8;
    public string Parity { get; init; } = "NONE";
    public int StopBits { get; init; } = 1;
    public bool Fifo { get; init; } = true;
    public string FlowControl { get; init; } = "NONE";
    public string Direction { get; init; } = "AUTO";
    public int InterruptPriority { get; init; } = 1;
    public int AdditionalInterruptMask
    {
        get; init;
    }
}
