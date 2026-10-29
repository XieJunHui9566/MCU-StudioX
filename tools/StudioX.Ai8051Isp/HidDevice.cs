namespace StudioX.Ai8051Isp;

public sealed record HidDevice(string Path, ushort VendorId, ushort ProductId, string Product, int InputBytes, int OutputBytes);
