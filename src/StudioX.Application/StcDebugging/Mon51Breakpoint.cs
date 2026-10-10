namespace StudioX.Application.StcDebugging;

public sealed record Mon51Breakpoint(ushort Address, byte Original, bool Installed = false)
{
    public string AddressText => $"0x{Address:X4}";
    public string OriginalText => $"{Original:X2}";
    public string Status => Installed ? "A5 已写入，等待命中" : "已保存；继续时写入 A5";
}
