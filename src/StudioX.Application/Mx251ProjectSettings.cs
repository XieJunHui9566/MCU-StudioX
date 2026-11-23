namespace StudioX.Application;

public sealed record Mx251ProjectSettings(SourceDocument Configuration, string Model, string DefaultModel,
    string Optimization = "speed", string Standard = "sdcc11",
    string DefaultOptimization = "speed", string DefaultStandard = "sdcc11");
