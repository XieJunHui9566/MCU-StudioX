namespace StudioX.Engine.Debugging;

/// <summary>GDB/MI 的 tuple/list 保留重复字段（例如 stack=[frame=...,frame=...]）。</summary>
public sealed record MiValue(string? Text, IReadOnlyList<KeyValuePair<string, MiValue>> Children)
{
    public MiValue? Get(string name) => Children.FirstOrDefault(x => x.Key == name).Value;
    public string String(string name, string fallback = "") => Get(name)?.Text ?? fallback;
    public IEnumerable<MiValue> Values => Children.Select(x => x.Value);
    public static MiValue Scalar(string text) => new(text, []);
}
