namespace StudioX.Application;

using System.Diagnostics;

/// <summary>打开 AG32 厂商的 Supra 下载与许可说明；固定 HTTPS 地址不接受模型提供的 URL。</summary>
public static class Ag32PinMappingDocumentation
{
    public static void OpenDownload() => Open("https://www.agm-micro.com/products.aspx?id=77&lang=cn&p=2075");

    public static void OpenLicenseInstructions() => Open("https://www.agmfpga.com/doc_26729587.html");

    private static void Open(string address)
    {
        using var process = Process.Start(new ProcessStartInfo(address) { UseShellExecute = true });
    }
}
