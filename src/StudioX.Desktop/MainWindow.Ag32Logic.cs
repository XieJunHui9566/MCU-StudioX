namespace StudioX.Desktop;

using System.Windows;
using StudioX.Engine;

public partial class MainWindow
{
    private void RefreshAg32LogicUi(ProjectManifest? project, bool busy = false)
    {
        var enabled = project?.Logic is not null;
        var visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        Ag32LogicMenu.Visibility = BuildLogicButton.Visibility = DownloadLogicButton.Visibility = visibility;
        Ag32LogicMenu.IsEnabled = BuildLogicButton.IsEnabled = DownloadLogicButton.IsEnabled =
            enabled && projectDirectory is not null && !busy;
    }

    private async void Ag32Logic_Click(object sender, RoutedEventArgs e) => await RunAsync(async token =>
    {
        var inspection = await InspectAg32LogicAsync(token);
        ShowLogicInspection(inspection, "AG32 逻辑模式");
        services.Files.ShowInExplorer(RequireProject(), "logic");
        Status.Text = "已打开 AG32 逻辑目录；构建输出列出了文件与工具检查结果。";
    });

    private async void BuildLogic_Click(object sender, RoutedEventArgs e) => await RunAsync(async token =>
    {
        var inspection = await InspectAg32LogicAsync(token);
        ShowLogicInspection(inspection, "AG32 逻辑构建指引");
        Log("构建步骤：1. 按真实 LQFP48 板级原理图配置 pins.ve，并在厂商配套工程中选定 AGRV2KL48。");
        Log("构建步骤：2. 在 AGM AgRV SDK / PlatformIO 配套工程运行 Prepare LOGIC，由 VE 生成顶层接口及 Quartus/Supra 工程；StudioX 目前没有这个任务。");
        Log("构建步骤：3. 核对生成的接口，再编辑或合并 user_logic.v，确保用户模块已接入厂商生成的顶层；VE 改动后须重新执行 Prepare LOGIC 并合并接口。");
        Log("构建步骤：4. 用 Quartus II Full（厂商推荐 13.0.1，Lite 不适用）运行工程转换/编译，生成 VO 网表。");
        Log("构建步骤：5. 用 Supra 打开转换后的厂商工程并编译逻辑 BIN。逻辑镜像需与 pins.ve / AGRV2KL48 匹配。");
        Log("StudioX 目前只检查交接文件，不自动执行 Prepare LOGIC、Quartus II 或 Supra。");
        Status.Text = inspection.Errors.Count == 0 ? "逻辑构建步骤已显示；请从 AGM 配套工程的 Prepare LOGIC 开始。" :
            "逻辑工程静态检查发现问题，请查看构建日志。";
        MessageBox.Show(this, "先配置 pins.ve / AGRV2KL48，在 AGM AgRV SDK / PlatformIO 配套工程运行 Prepare LOGIC；核对生成接口后编辑或合并 user_logic.v，再用 Quartus II Full 生成 VO、Supra 编译 BIN。\n\nStudioX 目前没有 Prepare LOGIC 任务；详细检查结果见构建日志。", "AG32 逻辑构建指引", MessageBoxButton.OK, MessageBoxImage.Information);
    });

    private async void DownloadLogic_Click(object sender, RoutedEventArgs e) => await RunAsync(async token =>
    {
        var inspection = await InspectAg32LogicAsync(token);
        ShowLogicInspection(inspection, "AG32 逻辑下载指引");
        Log("逻辑 BIN 与 MCU 固件是两个独立映像。请使用 AGM 官方逻辑下载流程，将与 AGRV2KL48 / pins.ve 匹配的 BIN 写入逻辑区。");
        Log("StudioX 的“下载固件”仅用于 MCU 应用区；本指引不会连接烧录器，也不会擦写或下载逻辑区。");
        Status.Text = inspection.Errors.Count > 0 ? "逻辑文件静态检查未通过，请查看构建日志。" :
            inspection.BinaryBytes is null ? "尚无逻辑 BIN；先完成 Quartus II 与 Supra 编译。" :
            "已找到逻辑 BIN，尚未验证其内容；请使用 AGM 官方逻辑下载流程。";
        MessageBox.Show(this, inspection.Errors.Count > 0
            ? "逻辑文件静态检查未通过，请先修正构建日志中的问题。当前 StudioX 不会自动写入逻辑区。"
            : inspection.BinaryBytes is null
                ? "尚未找到逻辑 BIN。请先完成厂商 Prepare LOGIC、Quartus II Full 转换及 Supra 编译。\n\n当前 StudioX 不会自动写入逻辑区。"
                : "已找到逻辑 BIN，但 StudioX 无法验证其内容。请在 AGM 官方工具中按 AGRV2KL48 / LQFP48 的配置单独下载到逻辑区。\n\n当前 StudioX 不会自动写入逻辑区；详细检查结果见构建日志。",
            "AG32 逻辑下载指引", MessageBoxButton.OK, MessageBoxImage.Information);
    });

    private async Task<Ag32LogicInspection> InspectAg32LogicAsync(CancellationToken token)
    {
        var root = RequireProject();
        await SaveAllSourcesAsync(root, token);
        return await services.Ag32Logic.InspectAsync(root, token);
    }

    private void ShowLogicInspection(Ag32LogicInspection inspection, string heading)
    {
        ShowBottom(0);
        BuildLog.Clear();
        Log(heading + " · " + inspection.Settings.TargetDevice + " · AG32VF303CCT6 LQFP48");
        Log("Verilog：" + inspection.VerilogPath);
        Log("VE 引脚映射：" + inspection.PinMapPath + " · 已配置物理引脚 " + inspection.AssignedPinCount);
        Log("Quartus II：" + (inspection.QuartusExecutable ?? "未在 PATH 中检测到；可设置 STUDIOX_AG32_QUARTUS。必须使用 Full 版，检测不代表版本或授权有效。"));
        Log("Supra：" + (inspection.SupraExecutable ?? "未在 PATH 中检测到；可设置 STUDIOX_AG32_SUPRA，或从 AGM AgRV SDK 的 tool-agrv_logic/bin 启动。"));
        Log("预期逻辑 BIN：" + inspection.BinaryPath + (inspection.BinaryBytes is { } bytes ? $" · {bytes} 字节" : " · 尚未生成"));
        foreach (var error in inspection.Errors) Log("检查问题：" + error);
        foreach (var note in inspection.Notes) Log("提示：" + note);
    }
}
