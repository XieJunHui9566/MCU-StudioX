namespace StudioX.Application;

using StudioX.Ai8051Isp;
using StudioX.Engine;

public sealed record Mx251DownloadDeviceChoice(Mx251IspSettings? Selected, IReadOnlyList<HidDevice> Devices, string Message, Mx251IspSettings SavedSettings);
