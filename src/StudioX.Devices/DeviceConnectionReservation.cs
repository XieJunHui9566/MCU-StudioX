namespace StudioX.Devices;

/// <summary>为外部原生工具预约连接；只占用逻辑所有权，不打开设备句柄。</summary>
public sealed class DeviceConnectionReservation : IDisposable
{
    private Action? release;

    internal DeviceConnectionReservation(Action release) => this.release = release;

    public void Dispose() => Interlocked.Exchange(ref release, null)?.Invoke();
}
