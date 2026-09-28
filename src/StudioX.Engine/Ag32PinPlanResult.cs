namespace StudioX.Engine;

/// <summary>厂商转换验证后保存的源与约束预览；最终物理 ASF 由普通编译生成。</summary>
public sealed record Ag32PinPlanResult(Ag32PinPlanSnapshot Snapshot, string ConstraintDirectory, string VePath,
    string VexPath, string SdcPath, string ConverterDiagnostics);
