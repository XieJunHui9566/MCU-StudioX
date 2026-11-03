namespace StudioX.Engine.Pic;

using System.Text.Json;
using StudioX.Foundation;

/// <summary>能力取自官方 6.20 的 PICkit3 特性表；需要专用调试头的型号不能按普通芯片开放。</summary>
public sealed record PicDebugTarget(string DeviceId, int HardwareBreakpoints, string? Header)
{
    private static readonly Lazy<PicDebugTarget[]> Targets = new(() =>
    {
        using var stream = typeof(PicDebugTarget).Assembly.GetManifestResourceStream("StudioX.Engine.Pic.pickit3-targets.json")!;
        return JsonSerializer.Deserialize<PicDebugTarget[]>(stream, JsonStore.Options) ?? [];
    });
    public static PicDebugTarget? Find(string deviceId) => Targets.Value.SingleOrDefault(target => target.DeviceId == deviceId);
    public static bool Supports(string deviceId) => Find(deviceId) is { HardwareBreakpoints: > 0 };
    public static PicDebugTarget Require(string deviceId)
    {
        var target = Find(deviceId);
        return target is { HardwareBreakpoints: > 0 } ? target : throw new StudioXException("PIC_DEBUG_DEVICE",
            target?.Header is { } header ? "该型号需要专用 ICD 调试头：" + header : "官方 PICkit3 特性表未确认此型号的片上调试能力。");
    }
}
