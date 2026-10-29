namespace StudioX.Engine.Pic;

using System.Text.RegularExpressions;
using StudioX.Foundation;

/// <summary>独立记录烧录组件的精确版本、内容指纹和用户选定的 PICkit3；首阶段仅支持目标板独立供电。</summary>
public sealed record PicDownloadSettings(int FormatVersion = 1, string ToolsetVersion = "6.20.0",
    string ToolFingerprint = "", string ProgrammerSerial = "", bool ReleaseAfterProgramming = true)
{
    public const string RelativePath = ".studiox/pic-download.json";
    public const string ToolsetId = "microchip.ipe";
    public const string CompilerId = "ipecmd-6.20";

    public void Validate(bool configured = true)
    {
        if (FormatVersion != 1 || ToolsetVersion != "6.20.0" || ToolFingerprint is null || ProgrammerSerial is null ||
            ToolFingerprint.Length > 0 && !Regex.IsMatch(ToolFingerprint, @"\A[0-9a-f]{64}\z") ||
            ProgrammerSerial.Length > 0 && !Regex.IsMatch(ProgrammerSerial, @"\A[A-Za-z0-9_-]{1,64}\z") ||
            configured && (ToolFingerprint.Length == 0 || ProgrammerSerial.Length == 0))
        {
            throw new StudioXException("PIC_DOWNLOAD_SETTINGS", "请准备兼容 PICkit3 的 IPE 6.20 组件，并明确选择烧录器序列号；目标板需独立供电。");
        }
    }
}
