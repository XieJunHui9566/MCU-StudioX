namespace StudioX.KeilImporter;

/// <summary>保留 CLI 原始输出和退出码，编译失败不转换为成功。</summary>
public sealed record CliOutput(int ExitCode, string StandardOutput, string StandardError);
