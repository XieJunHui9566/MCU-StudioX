namespace StudioX.Application.StcDebugging;

public sealed record Mon51Symbol(string Key, string Scope, string Name, string Level, int Block, int Size, string TypeChain,
    char Space, bool OnStack, int StackOffset, string[] Registers, ushort? Address = null, string Module = "")
{
    public bool IsFunction => TypeChain.StartsWith("DF,", StringComparison.Ordinal);
    public bool IsSigned => TypeChain.EndsWith(":S", StringComparison.Ordinal);
    public string TypeName => TypeChain;
}
