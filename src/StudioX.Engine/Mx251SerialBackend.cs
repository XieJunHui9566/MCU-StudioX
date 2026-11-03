namespace StudioX.Engine;

using System.IO.Ports;
using System.Security.Cryptography;
using System.Text;

public sealed class Mx251SerialBackend : IMx251SerialBackend
{
    public string[] GetPortNames() => SerialPort.GetPortNames().Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p.Length).ThenBy(p => p).ToArray();
    public IMx251SerialTransport Open(string port, Action<string, byte[]> trace) => new Connection(port, trace);
    private sealed class Connection : IMx251SerialTransport
    {
        private readonly SerialPort port;
        private readonly FileStream lease;
        private readonly Action<string, byte[]> trace;
        public Connection(string name, Action<string, byte[]> trace)
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MCUStudioX", "DeviceLocks");
            Directory.CreateDirectory(directory);
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("serial:" + name.ToUpperInvariant())));
            lease = new FileStream(Path.Combine(directory, key + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            port = new(name, 2400, Parity.Even, 8, StopBits.One)
            { DtrEnable = false, RtsEnable = true, Handshake = Handshake.None, ReadTimeout = 20, WriteTimeout = 3000,
              DiscardNull = false, ParityReplace = 0 };
            try { port.Open(); port.DiscardInBuffer(); }
            catch { port.Dispose(); lease.Dispose(); throw; }
            this.trace = trace;
        }
        public void SetBaudRate(int baud) => port.BaudRate = baud;
        public Task WriteAsync(byte[] bytes, CancellationToken token) => Task.Run(() =>
        {
            token.ThrowIfCancellationRequested(); port.Write(bytes, 0, bytes.Length); trace("tx", bytes);
        }, token);
        public Task<byte[]> ReadAsync(int timeoutMs, CancellationToken token) => Task.Run(() =>
        {
            token.ThrowIfCancellationRequested(); port.ReadTimeout = Math.Clamp(timeoutMs, 1, 100);
            var bytes = new byte[256];
            try { var count = port.Read(bytes, 0, bytes.Length); var result = bytes[..count]; trace("rx", result); return result; }
            catch (TimeoutException) { return []; }
        }, token);
        public void Dispose() { port.Dispose(); lease.Dispose(); }
    }
}

