namespace StudioX.Application;

/// <summary>保留原始诊断，并给出确定性处理步骤；不自动连接或改写设备。</summary>
public static class TroubleshootingService
{
    public static TroubleshootingAdvice Explain(string diagnostic)
    {
        bool Has(params string[] words) => words.Any(w => diagnostic.Contains(w, StringComparison.OrdinalIgnoreCase));
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
        if (Has("connect", "target", "DAP", "ST-Link", "J-Link", "调试连接"))
        {
            return new("目标连接未完成", "请依据原始日志核对目标身份与连接配置。", ["确认工程型号、烧录器和连接方式与实际硬件一致。", "检查供电、地线、SWD/JTAG 连线与复位。", "降低连接频率后手动重试；连接问题不应通过自动解锁或擦除处理。"], "log");
        }
        return new("检查操作诊断", "暂未匹配到专门的故障规则，原始信息完整保留。", ["从日志中最早出现的错误开始检查。", "确认当前工程、所选文件和配置。", "保留原始日志与复现步骤，修正后重新执行。"], "log");
    }
}
