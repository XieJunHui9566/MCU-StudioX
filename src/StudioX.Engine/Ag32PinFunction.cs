namespace StudioX.Engine;

/// <summary>锁定转换器提供的功能方向和内部 GPIO 共享资源。</summary>
public sealed record Ag32PinFunction(string Name, string Direction, string? SharedGpio);
