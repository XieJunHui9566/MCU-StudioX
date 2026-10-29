namespace StudioX.PicValidation;

using StudioX.Application;
using StudioX.Foundation;

internal static class LcdProject
{
    public static async Task CreateAsync(string evidence, string runtime, string packPath, string projectRoot, string source)
    {
        Directory.CreateDirectory(evidence);
        await using var services = new WorkbenchService(runtime, Path.Combine(evidence, "data"));
        var pack = await services.Packs.ImportAsync(packPath);
        await services.Projects.CreateAsync(pack, "PIC16F877A", "xc8-basic", "PicLcdHello", projectRoot);
        await File.WriteAllBytesAsync(Path.Combine(projectRoot, "src/main.c"), await File.ReadAllBytesAsync(source));
        Directory.CreateDirectory(Path.Combine(projectRoot, "docs"));
        File.Copy(Path.Combine(Path.GetDirectoryName(evidence)!, "references/PIC-DIP40-Schematic.pdf"), Path.Combine(projectRoot, "docs/PIC-DIP40-Schematic.pdf"));
        await File.WriteAllTextAsync(Path.Combine(projectRoot, "README.txt"),
            "PIC16F877A / PIC-DIP40 V2 / 4 MHz / LCD1602\n" +
            "第一行显示 Hello, World!。工程直接由 MCU StudioX 编译和 PICkit3 下载，不需要另装 MPLAB。\n" +
            "LCD：D0-D7 接 RD0-RD7，RS 接 RE0，RW 接 RE1，E 接 RE2。\n" +
            "请断电拔掉 LED-EN（LED-VCC/J3）跳帽，再上电。LCD 与 LED 共用 PORTD。\n" +
            "若只亮背光，请调右侧标有 1602 的蓝色对比度电位器。\n" +
            "保持原配置字 0x3F32；EEPROM 和用户 ID 由下载流程保留。\n" +
            "原理图副本见 docs/PIC-DIP40-Schematic.pdf。参考资料：\n" +
            "https://github.com/wang-zhuoran/PIC16F877A-examples/blob/main/PIC-DIP40%20Schematic.pdf\n" +
            "https://github.com/giljr/PIC16_DIP40_BOARD_CODES\n" +
            "https://ww1.microchip.com/downloads/en/devicedoc/39582b.pdf\n" +
            "https://www.sparkfun.com/datasheets/LCD/HD44780.pdf\n");
        await BuildAsync(evidence, runtime, projectRoot);
    }

    public static async Task BuildAsync(string evidence, string runtime, string projectRoot)
    {
        Directory.CreateDirectory(evidence);
        await using var services = new WorkbenchService(runtime, Path.Combine(evidence, "data"));
        var result = await services.Builds.BuildAsync(projectRoot, output: new InlineProgress(Console.Write));
        await JsonStore.WriteAsync(Path.Combine(evidence, "build.json"), result);
        if (!result.Success)
        {
            throw new InvalidOperationException("LCD firmware build failed");
        }
        await services.PicDownloads.SaveSettingsAsync(projectRoot, "BUR195068601", true);
        var preview = await services.PicDownloads.PreviewAsync(projectRoot);
        if (preview.Image.ConfigurationWord != 0x3f32)
        {
            throw new InvalidOperationException("Original configuration differs");
        }
        await JsonStore.WriteAsync(Path.Combine(evidence, "preview.json"), preview);
        Console.WriteLine("LCD SHA-256: " + preview.Firmware.Sha256);
    }

    public static async Task DownloadAsync(string evidence, string runtime, string project, string approvedSha)
    {
        Directory.CreateDirectory(evidence);
        await using var services = new WorkbenchService(runtime, Path.Combine(evidence, "data"));
        var preview = await services.PicDownloads.PreviewAsync(project);
        if (preview.Firmware.Sha256 != approvedSha || preview.Settings.ProgrammerSerial != "BUR195068601" || preview.Image.ConfigurationWord != 0x3f32)
        {
            throw new InvalidOperationException("LCD image or target differs from the reviewed project");
        }
        var result = await services.PicDownloads.DownloadApprovedAsync(project, preview.ApprovalId, new InlineProgress(Console.Write));
        await JsonStore.WriteAsync(Path.Combine(evidence, "download.json"), result);
        if (!result.Success)
        {
            throw new InvalidOperationException("LCD download not verified");
        }
    }
}
