namespace StudioX.Application;

/// <summary>保留原始诊断，并给出确定性处理步骤；不自动连接或改写设备。</summary>
public static class TroubleshootingService
{
    public static TroubleshootingAdvice Explain(string diagnostic)
    {
        bool Has(params string[] words) => words.Any(w => diagnostic.Contains(w, StringComparison.OrdinalIgnoreCase));
        if (Has("TOOLS_BUSY", "TOOLS_SESSION_ACTIVE", "TOOLS_PROTECTED", "TOOLS_CHANGED", "TOOLS_VERSION_EXISTS", "TOOLS_RETIREMENT", "TOOLS_ARCHIVE"))
        {
            return new("工具版本管理需要核对", "占用、依赖、归档或确认之后的内容变化阻止了本次维护。",
                ["打开工具占用与升级管理，刷新依赖和版本状态。", "结束正在使用工具的会话；登记其他需要保留依赖的工程。", "同一版本不覆盖；检查所选离线包的明确 ID、版本和原始诊断。", "永久删除中断时刷新后重试剩余清理；已进入删除的版本不能恢复。"], "tool-management");
        }
        if (Has("TOOLCHAIN_LOCK"))
        {
            return new("工程工具锁与安装内容不同", "同一版本号的工具清单与工程记录的指纹不一致。",
                ["打开工程健康检查，核对所需工具 ID、版本和原始诊断。", "在工具环境中选中工程锁定版本，用相同 ID、版本、内容的离线包恢复。", "保留 .studiox/toolchain.lock.json，不通过删除锁定或改用系统工具绕过检查。", "修复后重新检查并构建。"], "tools");
        }
        if (Has("HEALTH_CACHE_", "CMakeCache.txt directory", "does not match the source", "CMAKE_HOME_DIRECTORY"))
        {
            return new("CMake 配置缓存需要核对", "已有生成缓存与当前工程目录或工具环境不一致。",
                ["打开工程健康检查，查看缓存记录的目录、编译器与 target。", "选择相应检查项，点击“预览并重建配置缓存”。", "核对将移入备份的生成文件后确认；源码、sdkconfig 和工程工具锁保留。", "重新编译以生成新缓存与构建凭据；不要手改 Ninja 文件。"], "health");
        }
        if (Has("XTENSA_GNU_CONFIG", "pointed different files", "dynconfig"))
        {
            return new("Xtensa 动态配置冲突", "编译器启动路径或继承的配置与当前目标的动态配置不一致。",
                ["重新打开更新后的 IDE，核对工程 SDK 和 target 后再编译。", "查看原始命令：目标编译器应保留完整文件名，不能只出现 XT34AB~1.EXE 等短文件名。", "在工具环境管理中校验锁定工具版本，保留 SDK 与 sdkconfig。", "查看完整 ESP-IDF 排查手册；不要通过修改 SDK 或生成的 Ninja 文件处理。"], "tools");
        }
        if (Has("git-data/head-ref", "git-data\\head-ref"))
        {
            return new("Git 初始仓库版本探测失败", "工程可能已初始化 Git，但当前分支还没有首次提交。",
                ["使用已更新的 IDE 构建引擎重新配置和编译。", "核对日志中的版本来源；显式 PROJECT_VER、version.txt 和 project(VERSION) 应继续生效。", "保留 .git 和已有源码，无需为解决这个错误制造提交。", "若还有其它 CMake 错误，继续从最早的实际诊断排查。"], "log");
        }
        if (Has("undefined reference", "multiple definition"))
        {
            return new("链接符号缺失或重复", "编译完成后，链接器没有找到唯一匹配的符号定义。",
                ["核对诊断中的函数或变量，以及它的声明和实际定义。", "检查实现文件是否加入目标，所需库是否链接，条件宏是否排除了实现。", "C/C++ 混用时核对 extern C；重复定义时检查同一源码或全局对象是否加入两次。", "保存用户维护的 CMake 后重新构建，完整日志继续保留。"], "problems");
        }
        if (Has("overflowed", "will not fit in region"))
        {
            return new("固件内存区域超限", "链接布局中的 Flash 或 RAM 无法容纳本次固件。",
                ["核对工程完整料号与实际 Flash/RAM 容量。", "查看 MAP 和占用统计，定位大数组、堆栈、常量或重复组件。", "按应用需要减少内容或选择合适优化，再验证行为。", "不要把链接脚本容量改成超出真实芯片的值。"], "log");
        }
        if (Has("TOOL_", "TOOLSET_", "LANGUAGE_MISSING", "工具集缺失", "工具文件缺失"))
        {
            return new("工具环境不完整", "工程需要的内置工具未通过检查。", ["打开工具环境管理，找到标为工程需要的版本。", "执行完整性校验，查看具体缺失或变化的文件。", "使用同 ID、同版本的离线工具包修复后重试。"], "tools");
        }
        if (Has("file not found", "No such file", "找不到头文件"))
        {
            return new("源码或头文件未找到", "编译器未能在当前工程和包含目录中找到所引用文件。", ["核对诊断中的文件名、大小写与工程目录。", "检查 #include 路径和工程包含目录。", "若编辑器提示与实际编译不同，重新构建后核对分析配置。"], "problems");
        }
        if (Has("port is busy", "端口", "串口被"))
        {
            return new("连接资源不可用", "端口可能被其它会话占用，或当前连接已失效。", ["检查 IDE 中串口、MicroPython 和其它终端是否占用同一端口。", "结束已有会话，再刷新端口列表。", "核对设备连接和驱动状态后手动重试。"], "serial");
        }
        if (Has("access denied", "UnauthorizedAccess", "拒绝访问"))
        {
            return new("文件访问受限", "本次操作未能读取或写入所需文件。", ["根据原始诊断确认具体路径。", "检查文件是否只读，或正在被其它程序占用。", "保留未保存内容，选择可写的工程目录后重试。"], "log");
        }
        if (Has("EDITOR_FILE_CHANGED", "WORKSPACE_EDIT_STALE", "RENAME_VERSION", "FIX_VERSION"))
        {
            return new("文件或预览已经变化", "为了保留外部或后续编辑，本次修改没有应用。", ["先通过本地历史或复制保留当前草稿。", "核对磁盘版本与编辑内容。", "重新搜索或生成修改预览，再选择要应用的内容。"], "history");
        }
        if (Has("unable to connect", "target not examined", "DAP", "ST-Link", "J-Link", "调试连接"))
        {
            return new("目标连接未完成", "请依据原始日志核对目标身份与连接配置。", ["确认工程型号、烧录器和连接方式与实际硬件一致。", "检查供电、地线、SWD/JTAG 连线与复位。", "降低连接频率后手动重试；连接问题不应通过自动解锁或擦除处理。"], "log");
        }
        return new("检查操作诊断", "暂未匹配到专门的故障规则，原始信息完整保留。", ["从日志中最早出现的错误开始检查。", "确认当前工程、所选文件和配置。", "保留原始日志与复现步骤，修正后重新执行。"], "log");
    }
}
