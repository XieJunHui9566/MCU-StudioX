namespace StudioX.Application.Onboarding;

using StudioX.Foundation;

/// <summary>引导内容和独立用户偏好；不扫描 SDK，也不连接或写入设备。</summary>
public sealed class FirstProjectGuideService(string dataDirectory)
{
    private readonly string path = Path.Combine(dataDirectory, "first-project-guide.json");
    public async Task<bool> IsDismissedAsync(CancellationToken token = default) => File.Exists(path)
        && (await JsonStore.ReadAsync<GuidePreference>(path, token).ConfigureAwait(false)).Dismissed;
    public Task DismissAsync(CancellationToken token = default) => JsonStore.WriteAsync(path, new GuidePreference(true), token);

    public static IReadOnlyList<FirstProjectStep> Steps(FirstProjectProgress p)
    {
        var opened = p.ProjectDirectory is not null;
        var native = !p.Script && !p.Experimental;
        var buildStatus = p.BuildSucceeded switch
        {
            true => "本次工程编译成功",
            false => "本次编译失败，请查看最早的错误和原始日志",
            _ => "尚未执行本次工程编译"
        };
        return [
            new("准备目标与工具", "先确认板上完整芯片料号、容量与封装，再决定框架：普通 C/C++、HAL/SPL、RTOS、ESP-IDF 或 MicroPython。没有开发板也可以先完成创建、编辑与编译。\n\n内置工具由 IDE 管理，不需要把 GCC/Python 加入系统 PATH。缺少器件包时可从新建工程页导入或同步；创建或打开工程后，点击“准备工程开发环境组件”，按工程锁定的 ID、版本与编译器查看缺失项、下载大小和安装空间；支持继续下载与离线导入。已有工具损坏时进入工具校验与修复。", "知道准确目标与希望使用的框架；工具准备页列出当前工程的明确需求。", "准备步骤可随时回看", "tools", "准备工程开发环境组件", "distribution"),
            new("创建或打开工程", "点击“选择器件与模板”，依次选择厂商 → 器件包 → 完整型号 → 模板，填写工程名，再选择父目录。创建前核对模板说明和开发环境组件版本；不要用近似型号代替。\n\n已有 StudioX 工程可用“打开已有工程”；CubeMX 工程需要 CMake 工程入口。ESP-IDF 必须核对 target（例如 esp32s3），Flash/PSRAM 参数以实际模组为准。", "工程树出现文件，顶部显示工程名、器件和锁定开发环境组件版本。", opened ? "已打开：" + p.ProjectName + "\n目标：" + p.Target + "\n工具：" + p.Toolset + "\n目录：" + p.ProjectDirectory : "尚未打开工程", "create", "选择器件与模板", "project-create", opened),
            new("检查工程环境", "在首次编译前运行快速健康检查，查看工程锁、源文件入口、必要工具、SDK 和构建配置。红色问题要先修复；黄色提示需要阅读。\n\n快速检查不做完整 SDK 文件哈希校验。需要排查工具损坏时，在健康页选择完整校验；修复缓存前先阅读将备份的内容。", "当前工程快速检查无错误；适用性和警告已确认。", p.HealthPassed ? "本次工程快速检查无错误" : "尚未通过当前工程检查", "health", "运行工程健康检查", "troubleshooting", p.HealthPassed, opened),
            new("编辑与保存源码", "点击“打开入口源码”，阅读模板的入口和注释，可先只加一条自己的注释。Ctrl+S 保存当前文件，Ctrl+Shift+S 保存全部文件；标签上的修改标记消失代表已保存。\n\n新增、移除或改名 C/C++ 源文件后，在工程工具或文件右键菜单打开“源码登记与编译列表”，选择构建文件、目标与操作，预览后应用到编辑器，再保存并编译。复杂变量、条件或目录收集写法会提示手工核对；只出现在工程树中不等于参与编译。", "入口文件可编辑，完成一次显式保存；源码属于自己的工程目录。", p.SourceSaved ? "已在本次工程执行保存" : "尚未执行保存", "source", "打开入口源码", "editor", p.SourceSaved, opened),
            new(p.Script ? "检查脚本" : p.Experimental ? "了解实验工程" : "首次编译", p.Script ? "MicroPython 工程运行的是脚本，本步骤不调用 MCU C/C++ 编译器。先保存并检查入口脚本；连接板上的解释器、发送脚本或运行脚本都需要你随后明确操作。" : p.Experimental ? "此工程使用实验支持，不能把创建成功当作原生构建已验证。先阅读框架帮助和项目说明，按已有工具配置操作，保留原始输出。" : "点击“编译当前工程”或按 F7。IDE 会先保存文件并检查环境，然后配置、编译和链接；底部构建输出会显示原始诊断。首次 ESP-IDF 编译可能较久，可用顶部停止按钮取消。\n\n失败时从最早的 error/fatal error 开始；不要仅根据最后一行 exit 判断原因。点击健康检查或故障帮助进入具体修复步骤。", native ? "构建输出明确显示编译成功；查看生成产物路径。" : "保存源码并了解这个框架实际支持的运行方式。", native ? buildStatus : "此步骤不适用原生固件编译", native ? "build" : "help", native ? "编译当前工程（F7）" : "阅读框架帮助", p.Script ? "micropython" : p.Experimental ? "project-create" : p.Espressif ? "esp-idf-errors" : "build-errors", native && p.BuildSucceeded == true, opened),
            new("查看结果与产物", "点击“查看构建输出”，确认最后的成功或失败结论，并阅读产物路径。普通 MCU 通常生成 ELF 和 BIN/HEX，ESP-IDF 还会生成引导程序、分区表与应用镜像。\n\nFlash/RAM 占用可在工程左侧查看。编辑源码后需要再次编译，旧产物不能证明新源码已通过；健康检查通过也不等于编译成功。", p.Script ? "MicroPython 的源码是脚本，不会产生 C/C++ ELF 固件。" : "记录当前工程编译结果和可用产物位置。", p.Artifacts is { Count: > 0 } ? string.Join('\n', p.Artifacts) : "本次引导尚无固件产物；结果以实际构建日志为准。", "output", "查看构建输出", "build", native && p.BuildSucceeded == true, opened),
            new("接下来：运行与调试", "软件部分完成后，可再核对实板型号、供电、接口、烧录器或串口，以及准备写入的当前固件。下载可能覆盖板上程序，先决定是否备份。\n\n在下载设置选择实际探针/COM；调试需匹配 ELF 和已下载固件，使用断点、暂停和单步。串口终端可观察程序输出。这里只提供操作说明，连接、下载和调试都由你明确启动。\n\n以后可从欢迎页或帮助菜单继续引导，也可直接用正常工作台。", "已了解下一步操作；无开发板也可以结束引导。", "可选步骤 · 尚未进行实板验证", "help", "阅读下载与调试说明", p.Script ? "micropython" : "download")
        ];
    }
    private sealed record GuidePreference(bool Dismissed);
}
