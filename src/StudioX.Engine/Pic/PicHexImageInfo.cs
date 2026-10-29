namespace StudioX.Engine.Pic;

/// <summary>PIC16F877A 的字寻址 HEX 检查结果；文件字节数不等于程序字数。</summary>
public sealed record PicHexImageInfo(int ProgramWords, ushort ConfigurationWord);
