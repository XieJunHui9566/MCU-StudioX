namespace StudioX.Engine;

public interface IMx251SerialBackend
{
    string[] GetPortNames();
    IMx251SerialTransport Open(string port, Action<string, byte[]> trace);
}
