namespace StudioX.Engine;

/// <summary>工程框架的能力边界；工具完好与探针配置仍由专项检查确认，不代表实板验收。</summary>
public sealed record ProjectCapabilities(
    bool Build, bool Configure, bool BuildSettings, bool SourceRegistration, bool Download, bool Debug,
    string Scope, string SourceWorkflow, string RuntimeWorkflow,
    string? BuildError = null, string? ConfigureError = null, string? SettingsError = null)
{
    public static ProjectCapabilities For(ProjectManifest? project) => project?.Kind switch
    {
        ProjectKind.Pack when Mx251SdkProfile.Matches(project) => new(true, true, true, true, Mx251IspService.Supports(project), false,
            Mx251IspService.Supports(project)
                ? "MX251 SDK 使用 CMake + Ninja 构建并导出 HEX/BIN；AI8051U 支持串口 ISP、芯片原生 USB 和 AI8H2K12G 下载桥；源码调试尚未接入。"
                : "MX251 SDK 使用组件声明的 CMake + Ninja 入口构建并导出 HEX/BIN；器件支持范围以所选组件为准，当前目标尚未接入 IDE 下载和调试。",
            "在 CMakeLists.txt 中管理源码、包含目录、宏和反馈开关；mx251.json 仅管理目标、ABI、模型与运行库。相同工程可独立构建。",
            Mx251IspService.Supports(project)
                ? "保存、编译后按下载设置写入，固定 32 位模式；串口/下载桥支持 IRC 校准，原生 USB 保留芯片现有时钟并核对工程目标。"
                : "固件可交给外部下载工具；准确器件、配置和硬件结果须独立核对。",
            SettingsError: "MX251_BUILD_SETTINGS"),
        ProjectKind.Pack when project.ToolsetId == Mx251Profile.ToolsetId => new(true, true, true, true, true, false,
            "MX251 支持 AI8051U-34K64 的 32 位编译、HEX 导出、串口 ISP、原生 USB 及下载桥；源码调试尚未接入。",
            "在 CMakeLists.txt 中登记源码、包含目录和宏；工程详情设置内存模型及 IRC。mx251.json 保留目标 ABI 和运行库选项；不能套用 GCC 专用参数。",
            "在下载设置中选择连接方式和 IRC 频率；串口/下载桥校准 IRC，原生 USB 保留当前时钟。下载固定 32 位，ACK 不代表 Flash 回读或程序功能验收。",
            ConfigureError: "MX251_CONFIGURE", SettingsError: "MX251_BUILD_SETTINGS"),
        ProjectKind.Pic when project.DeviceId == "PIC16F877A" => new(true, true, false, true, true, true,
            "PIC16F877A 使用 CMake + Ninja + XC8 构建，支持 PICkit3 下载及 MDB 源码调试；硬件限制为 1 个程序断点，实板结果独立记录。",
            "通过 CMakeLists.txt 管理 C 源码、包含目录和宏；Ninja 调用 XC8 编译链接。源码中须明确设置配置位。",
            "仅一个 PICkit3 时自动选择，多个时选择序列号；目标板独立供电。普通下载直接写入与校验，不备份原程序；调试退出恢复原程序。",
            ConfigureError: "PIC_CONFIGURE", SettingsError: "PIC_BUILD_SETTINGS"),
        ProjectKind.Pic => new(true, true, false, true, false, Pic.PicDebugTarget.Supports(project.DeviceId),
            "PIC 使用 CMake + Ninja + XC8 构建并导出 ELF/HEX；官方确认有片上调试能力的型号提供 PICkit3 / MDB 入口，实板结果独立记录。",
            "在 CMakeLists.txt 中登记 C 源码、包含目录和宏；CMake + Ninja 调用 XC8，支持独立命令行构建。",
            "仅一个 PICkit3 时自动选择，多个时选择序列号；需要专用 ICD 头或无片上调试能力的型号明确限制调试。实际时钟、供电与硬件结果需独立核对。",
            ConfigureError: "PIC_CONFIGURE", SettingsError: "PIC_BUILD_SETTINGS"),
        ProjectKind.Arduino => new(true, false, false, false, true, false,
            "Arduino 支持 sketch 编译、产物导出与明确选择串口后的上传；未接入源码调试，实板上传尚未验收。",
            "Arduino 由固定核心按 sketch 规则收集 .ino、同目录 C/C++ 和 src/ 下源码；无需 CMake 源码登记。板型、FQBN 选项与库在 Arduino 工程管理中锁定。",
            "上传前在 Arduino 工程管理核对准确板型、核心、FQBN 和 COM 串口，再预览当前固件并确认上传。上传会覆盖板上程序；当前未接入源码调试，软件编译通过不代表实板上传验收。",
            ConfigureError: "ARDUINO_CONFIGURE", SettingsError: "ARDUINO_BUILD_SETTINGS"),
        ProjectKind.MicroPython => new(false, false, false, false, true, false,
            "MicroPython 为脚本工程；支持脚本编辑与专用面板操作，不进行原生固件编译或源码调试。",
            "保存入口脚本及其依赖；无需 CMake 源码登记。",
            "使用 MicroPython 面板明确选择串口与板上解释器，再下载或运行脚本；连接可能中断板上程序。",
            "MICROPYTHON_NATIVE_OPERATION", "MICROPYTHON_NATIVE_OPERATION", "MICROPYTHON_NATIVE_OPERATION"),
        ProjectKind.Zephyr when project.Zephyr?.FormatVersion == 2 => new(true, true, false, false, false, false,
            "Zephyr 支持自定义板级工程的创建、编辑、配置和编译；下载与调试尚未接入，编译不代表实板验收。",
            "在 CMakeLists.txt 中维护 target_sources(app ...)，在 prj.conf、app.overlay 和板级 DTS 中配置内核、外设与接线；通用源码登记暂不适用。",
            "先按实际 PCB 核对 DTS、时钟与引脚。IDE 的 Zephyr 下载和调试尚未接入；保留构建产物供后续板级验收。",
            SettingsError: "ZEPHYR_BUILD_SETTINGS"),
        ProjectKind.Zephyr => new(false, false, false, false, false, false,
            "历史 Zephyr 实验工程仅支持打开和编辑；请通过准确器件的起始模板创建新版 Zephyr 工程。",
            "保留原有工程文件；历史实验工程不能使用原生构建或通用源码登记。",
            "历史实验工程未开放下载和调试。", "ZEPHYR_RUNTIME_UNAVAILABLE", "ZEPHYR_RUNTIME_UNAVAILABLE", "ZEPHYR_BUILD_SETTINGS"),
        ProjectKind.Pack when project.ToolsetId == "stc.sdcc" => new(true, true, true, true, true, StcMon51Targets.Supports(project.DeviceId),
            "STC SDCC 工程支持 CMake + Ninja 构建及串口 ISP 下载；Mon51 串口仿真仅支持 " + StcMon51Targets.DisplayNames + "。",
            "在 CMakeLists.txt 中管理源码、包含目录和宏；器件与模板中保存编译参数、时钟及下载串口。",
            StcMon51Targets.Supports(project.DeviceId)
                ? "调试复用已保存的下载串口和波特率，先握手；仅握手失败才制作对应型号的 Mon51 监控，制作后按输出提示重新上电。"
                : project.DeviceId + " 未接入 Mon51 串口仿真；可正常编译和 ISP 下载，调试入口不可用。"),
        ProjectKind.Pack or ProjectKind.CubeMx => new(true, true, true, project.Espressif is null or { Framework: "esp-idf" }, true, project.Espressif is null,
            project.Espressif is not null ? "SDK 工程支持配置、编译和串口下载；源码调试尚未接入，硬件行为以对应板卡验收为准。"
                : "原生工程支持配置、编译和源码登记；下载与调试还需器件包提供匹配配置，软件检查不代表实板验收。",
            project.Espressif is { Framework: not "esp-idf" } ? "当前 SDK 工程的源码列表需要在构建文件中维护；通用源码登记暂不适用。" : "新增、移除或改名 C/C++ 源文件后使用“源码登记与编译列表”，预览后保存并重新编译；出现在工程树中不等于参与编译。",
            "核对实板、供电、接口和当前固件，下载前决定是否备份。在下载设置选择实际探针或 COM；调试还需匹配 ELF 和板上固件，连接、下载和调试由你明确启动。"),
        _ => new(false, false, false, false, false, false, "尚未打开受支持的工程。", "请先打开工程。", "请先打开工程。")
    };
}
