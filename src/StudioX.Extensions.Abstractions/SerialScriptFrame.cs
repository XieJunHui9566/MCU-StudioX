namespace StudioX.Extensions.Abstractions;

public sealed record SerialScriptFrame(int Offset, int Length, string Summary, Dictionary<string, string>? Fields = null,
    string Status = "ok");
