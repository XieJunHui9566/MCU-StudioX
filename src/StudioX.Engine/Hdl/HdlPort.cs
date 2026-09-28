namespace StudioX.Engine.Hdl;

/// <summary>网表连接按低位在前保存，数字为网线标识，0/1/x/z 为常量。</summary>
public sealed record HdlPort(string Name, string Direction, string[] Bits, int Offset = 0, bool Upto = false)
{
    public int IndexAt(int position) => Offset + (Upto ? Bits.Length - 1 - position : position);
}
