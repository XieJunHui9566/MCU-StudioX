namespace StudioX.Engine;

using System.Security.Cryptography;
using System.Text.Json;
using StudioX.Foundation;

/// <summary>下载审批固定整个多映像布局，包括配置文件、芯片、Flash 参数和每个 BIN。</summary>
public sealed record EspressifFlashLayout(string FlasherArgumentsRelativePath, string FlasherArgumentsSha256,
    string Target, string FlashMode, string FlashFrequency, string FlashSize, bool UseStub,
    EspressifFlashImage[] Images)
{
    public string LayoutSha256 => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
        new
        {
            FlasherArgumentsRelativePath,
            FlasherArgumentsSha256,
            Target,
            FlashMode,
            FlashFrequency,
            FlashSize,
            UseStub,
            Images
        },
        JsonStore.Options)));

    public long ImageBytes => Images.Sum(image => image.Bytes);
}
