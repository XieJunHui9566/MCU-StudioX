namespace StudioX.Ai8051Isp;

// MeasuredHz 是分频后的系统时钟；保留用户目标以检查误差，不能以实测值代替目标。
public sealed record Calibration(uint MeasuredHz, byte Trim, byte Range, byte Divider = 1, int RequestedHz = 40000000);
