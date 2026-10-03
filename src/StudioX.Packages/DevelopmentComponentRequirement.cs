namespace StudioX.Packages;

/// <summary>器件包声明精确组件身份；归档地址、安装路径与可执行命令不属于器件包。</summary>
public sealed record DevelopmentComponentRequirement(string Id, string Version, string CompilerId,
    string Host = "win-x64", string Purpose = "工程编译");
