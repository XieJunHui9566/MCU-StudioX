namespace StudioX.Devices;

using System.IO.Ports;
using System.Text.RegularExpressions;

public sealed record SerialSettings(string PortName = "", int BaudRate = 115200, int DataBits = 8,
    Parity Parity = Parity.None, StopBits StopBits = StopBits.One, Handshake FlowControl = Handshake.None,
    bool Dtr = false, bool Rts = false)
{
    public void Validate()
    {
        if (!Regex.IsMatch(PortName, @"^COM[1-9]\d{0,4}$", RegexOptions.IgnoreCase))
        {
            throw new ArgumentException("请选择有效的 COM 串口。");
        }
        if (BaudRate is < 50 or > 4000000)
        {
            throw new ArgumentException("波特率范围为 50–4000000，实际支持取决于串口驱动。");
        }
        if (DataBits is < 5 or > 8 || !Enum.IsDefined(Parity) || !Enum.IsDefined(StopBits) || StopBits == StopBits.None || !Enum.IsDefined(FlowControl))
        {
            throw new ArgumentException("串口参数无效。");
        }
        if (StopBits == StopBits.OnePointFive && DataBits != 5)
        {
            throw new ArgumentException("1.5 停止位需要 5 数据位。");
        }
        if (StopBits == StopBits.Two && DataBits == 5)
        {
            throw new ArgumentException("5 数据位请使用 1 或 1.5 停止位。");
        }
    }
    public bool HardwareFlow => FlowControl is Handshake.RequestToSend or Handshake.RequestToSendXOnXOff;
}
