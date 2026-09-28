namespace StudioX.Engine.Hdl;

/// <summary>可复核的 RTL 仿真输出，不表示实板信号或带 SDF 的布局后仿真。</summary>
public sealed record HdlSimulationResult(string ProjectDirectory, HdlSimulationSettings Settings, HdlWaveform Waveform,
    string VcdPath, string LogPath, Dictionary<string, string> Inputs, string ToolFingerprint, string[] Warnings);
