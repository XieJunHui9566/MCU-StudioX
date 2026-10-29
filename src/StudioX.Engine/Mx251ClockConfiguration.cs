namespace StudioX.Engine;

using System.Globalization;
using System.Text.Json.Nodes;
using StudioX.Ai8051Isp;
using StudioX.Foundation;

/// <summary>工程宏是编译与 ISP 共用的 IRC 频率；USB 设备路径仍只保存在本机。</summary>
public static class Mx251ClockConfiguration
{
    public const string Define = "MX251_IRC_HZ";
    public const string TicksDefine = "MX251_DELAY_TICKS";
    public static int DelayTicks(int hz) => (hz + 2000) / 4000 - 1;

    public static async Task<int?> ReadAsync(string root, CancellationToken token = default)
        => Read(JsonNode.Parse(await File.ReadAllTextAsync(PathBoundary.Resolve(root, Mx251Profile.ConfigurationFile), token))!);

    public static int? Read(JsonNode configuration)
    {
        var defines = configuration["defines"] as JsonArray ?? throw Invalid("mx251.json 缺少 defines 数组。");
        var values = defines.Select(d => d?.GetValue<string>() ?? "").Where(IsClock).ToArray();
        foreach (var unit in configuration["units"]?.AsArray() ?? [])
        {
            if (unit?["defines"]?.AsArray().Any(d => IsClock(d?.GetValue<string>() ?? "") || IsTicks(d?.GetValue<string>() ?? "")) == true)
            {
                throw Invalid("IRC 频率必须在工程中统一设置，不能被单个源文件覆盖。");
            }
        }
        if (values.Length == 0)
        {
            return null;
        }
        if (values.Length != 1 || !values[0].StartsWith(Define + "=", StringComparison.Ordinal) ||
            !int.TryParse(values[0][(Define.Length + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var hz))
        {
            throw Invalid("MX251_IRC_HZ 必须是唯一的十进制 Hz 整数，请在工程详情保存频率。");
        }
        new DeviceConfiguration(1, "AI8051U-34K64", 32, hz).Validate();
        var ticks = defines.Select(d => d?.GetValue<string>() ?? "").Where(IsTicks).ToArray();
        if (ticks.Length > 0 && (ticks.Length != 1 || ticks[0] != TicksDefine + "=" + DelayTicks(hz).ToString(CultureInfo.InvariantCulture)))
        {
            throw Invalid("延时基准与 IRC 频率不一致，请在工程详情重新保存频率。");
        }
        return hz;
    }

    internal static async Task SaveAsync(string root, int hz, CancellationToken token)
    {
        new DeviceConfiguration(1, "AI8051U-34K64", 32, hz).Validate();
        var path = PathBoundary.Resolve(root, Mx251Profile.ConfigurationFile);
        var before = await File.ReadAllTextAsync(path, token);
        var config = JsonNode.Parse(before)!;
        var current = Read(config);
        var defines = config["defines"]!.AsArray();
        if (current == hz && defines.Any(d => IsTicks(d!.GetValue<string>())))
        {
            return;
        }
        for (var i = defines.Count - 1; i >= 0; i--)
        {
            if (IsClock(defines[i]!.GetValue<string>()) || IsTicks(defines[i]!.GetValue<string>()))
            {
                defines.RemoveAt(i);
            }
        }
        defines.Add(Define + "=" + hz.ToString(CultureInfo.InvariantCulture));
        defines.Add(TicksDefine + "=" + DelayTicks(hz).ToString(CultureInfo.InvariantCulture));
        // 保留可恢复的原配置，并拒绝覆盖保存期间的外部编辑。
        var history = PathBoundary.Resolve(root, ".studiox/history/mx251-clock/" + Guid.NewGuid().ToString("N") + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(history)!);
        await File.WriteAllTextAsync(history, before, token);
        if (await File.ReadAllTextAsync(path, token) != before)
        {
            throw Invalid("mx251.json 已被修改，请重新打开工程详情后保存。");
        }
        await JsonStore.WriteAsync(path, config, token);
        Mx251BuildService.Invalidate(root);
    }

    private static bool IsClock(string value) => value == Define || value.StartsWith(Define + "=", StringComparison.Ordinal);
    private static bool IsTicks(string value) => value == TicksDefine || value.StartsWith(TicksDefine + "=", StringComparison.Ordinal);
    private static StudioXException Invalid(string message) => new("MX251_CLOCK", message);
}
