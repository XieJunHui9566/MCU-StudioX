namespace StudioX.Application.MicroPython;

/// <summary>保留板上解释器的原始标准输出与异常输出。</summary>
public sealed record MicroPythonResult(string Output, string Error);
