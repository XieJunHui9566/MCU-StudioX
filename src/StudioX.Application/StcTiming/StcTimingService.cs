namespace StudioX.Application.StcTiming;

using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using StudioX.Engine;
using StudioX.Foundation;
using StudioX.Packages;

/// <summary>只读取工程和包内寄存器声明，计算、生成文本；不执行工具或连接硬件。</summary>
public sealed partial class StcTimingService
{
    public ProjectClockService ProjectClocks { get; set; } = new();
    public async Task<StcTimingContext> ReadAsync(string directory, CancellationToken token = default)
    {
        var root = Path.GetFullPath(directory);
        var project = await ProjectService.ReadAsync(root, token).ConfigureAwait(false);
        if (project.Kind != ProjectKind.Pack || project.PackId is not ("stc.stc8" or "stc.iap15l2k61s2.config-preview") || project.ToolsetId != "stc.sdcc" ||
            project.CompilerId != "sdcc-4.5.0-15242")
        {
            throw new StudioXException("STC_TIMING_PROJECT", "请先打开 STC 8 位 SDCC 工程，再生成 51 延时或波特率函数。");
        }
        var manifest = await ReadBoundedAsync(root, "device/manifest.json", token).ConfigureAwait(false);
        var pack = System.Text.Json.JsonSerializer.Deserialize<PackManifest>(manifest, JsonStore.Options)
            ?? throw new StudioXException("STC_TIMING_PACK", "器件包清单为空。");
        var device = pack.Devices?.SingleOrDefault(d => d.Id == project.DeviceId);
        if (pack.FormatVersion != 1 || pack.Id != project.PackId || pack.Version != project.PackVersion ||
            device is null || device.ToolsetId != project.ToolsetId || device.ToolsetVersion != project.ToolsetVersion ||
            device.CompilerId != project.CompilerId || !device.Templates.Any(t => t.Id == project.TemplateId))
        {
            throw new StudioXException("STC_TIMING_PACK", "工程与包内的准确器件、模板或编译器身份不一致。");
        }
        var profile = StcTimingProfile.For(device);
        var header = await ReadBoundedAsync(root, "device/sdk/include/" + profile.Header, token).ConfigureAwait(false);
        var registers = new Dictionary<string, int>
        {
            ["TMOD"] = 0x89,
            ["TH0"] = 0x8c,
            ["TL0"] = 0x8a,
            ["TH1"] = 0x8d,
            ["TL1"] = 0x8b,
            ["TR0"] = 0x8c,
            ["TF0"] = 0x8d,
            ["ET0"] = 0xa9,
            ["TR1"] = 0x8e,
            ["TF1"] = 0x8f,
            ["ET1"] = 0xab,
            ["EA"] = 0xaf,
            ["SCON"] = 0x98,
            ["PCON"] = 0x87,
            ["ES"] = 0xac
        };
        if (profile.Family == "stc89")
        {
            registers["T2CON"] = 0xc8;
        }
        else
        {
            registers["AUXR"] = 0x8e;
            registers["P3M1"] = 0xb1;
            registers["P3M0"] = 0xb2;
        }
        if (profile.HasTimer2Baud)
        {
            registers["T2H"] = 0xd6;
            registers["T2L"] = 0xd7;
            registers["IE2"] = 0xaf;
        }
        if (profile.HasPinSwitch)
        {
            registers["P_SW1"] = 0xa2;
            registers["P1M1"] = 0x91;
            registers["P1M0"] = 0x92;
        }
        if (profile.ClockOutputRegister is { } clockOutput)
        {
            registers[clockOutput] = 0x8f;
        }
        foreach (var (register, address) in registers)
        {
            var bit = register is "TR0" or "TF0" or "ET0" or "TR1" or "TF1" or "ET1" or "EA" or "ES";
            if (!Regex.IsMatch(header, (bit ? "__sbit" : "__sfr") + @"\s+__at\(0[xX](?i:" + address.ToString("X2", System.Globalization.CultureInfo.InvariantCulture) + @")\)\s+" + register + @"\s*;", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
            {
                throw new StudioXException("STC_TIMING_HEADER", "包内寄存器声明缺失或地址不符：" + register);
            }
        }
        var isp = await StcIspSettings.ReadAsync(root, token).ConfigureAwait(false);
        var clock = await ProjectClocks.ReadAsync(root, token).ConfigureAwait(false);
        clock.RequireReady();
        isp.ValidateFor(StcIspCapabilities.For(device));
        var build = await ProjectBuildSettings.ReadAsync(root, token).ConfigureAwait(false);
        build.ValidateFor(project);
        var settingText = await ReadOptionalAsync(root, StcTimingSettings.RelativePath, token).ConfigureAwait(false);
        var settings = settingText.Length == 0 ? new StcTimingSettings(BaudGenerator: profile.HasTimer2Baud ? "timer2" : "timer1") :
            System.Text.Json.JsonSerializer.Deserialize<StcTimingSettings>(settingText, JsonStore.Options)
            ?? throw new StudioXException("STC_TIMING_SETTINGS", "计时参数为空。");
        profile.Validate(settings);
        // COM 号和 ISP 传输速度不参与代码计算；串口下载速度绝不是用户 UART 的波特率。
        var stamp = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(manifest + header + settingText +
            System.Text.Json.JsonSerializer.Serialize(project, JsonStore.Options) + $"|{isp.ClockMode}|{isp.ClockFrequencyHz}|{build.Mon51Profile}|{clock.ConfigurationSha256}")));
        return new(root, project, profile, settings, clock.Managed ? clock.SystemFrequencyHz : isp.ClockFrequencyHz, build.Mon51Profile, stamp);
    }

    public async Task ValidateAsync(StcTimingContext context, CancellationToken token = default)
    {
        var current = await ReadAsync(context.ProjectDirectory, token).ConfigureAwait(false);
        if (current.EvidenceStamp != context.EvidenceStamp || current.Project != context.Project)
        {
            throw new StudioXException("STC_TIMING_STALE", "工程、时钟、监控预设或计时参数已变化，请重新打开生成窗口。");
        }
    }

    public async Task<StcTimingContext> SaveAsync(StcTimingContext context, StcTimingSettings settings, CancellationToken token = default)
    {
        context.Profile.Validate(settings);
        var path = PathBoundary.Resolve(context.ProjectDirectory, StcTimingSettings.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var gate = new FileStream(PathBoundary.Resolve(context.ProjectDirectory, StcTimingSettings.RelativePath + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        await ValidateAsync(context, token).ConfigureAwait(false);
        await JsonStore.WriteAsync(path, settings, token).ConfigureAwait(false);
        return await ReadAsync(context.ProjectDirectory, token).ConfigureAwait(false);
    }

    public string PrepareInsertion(StcTimingPreview preview, string document)
    {
        if (!preview.CanInsert)
        {
            throw new StudioXException("STC_TIMING_UNSAFE", preview.Notice);
        }
        if (Regex.IsMatch(document, @"\b" + Regex.Escape(preview.FunctionName) + @"(?:_[A-Za-z0-9_]+)?\b", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
        {
            throw new StudioXException("STC_TIMING_DUPLICATE", "当前文件已包含同名函数，请换一个名称或更新原函数。");
        }
        var newline = document.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        return preview.Code.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", newline, StringComparison.Ordinal) + newline;
    }

    private static async Task<string> ReadOptionalAsync(string root, string relative, CancellationToken token) =>
        File.Exists(PathBoundary.Resolve(root, relative)) ? await ReadBoundedAsync(root, relative, token).ConfigureAwait(false) : "";

    private static async Task<string> ReadBoundedAsync(string root, string relative, CancellationToken token)
    {
        var path = PathBoundary.Resolve(root, relative);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 4 * 1024 * 1024)
        {
            throw new StudioXException("STC_TIMING_SIZE", "计时生成输入超过读取上限：" + relative);
        }
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(token).ConfigureAwait(false);
    }
}
