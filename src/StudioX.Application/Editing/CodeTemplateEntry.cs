namespace StudioX.Application.Editing;

public sealed record CodeTemplateEntry(CodeTemplate Template, CodeTemplateScope Scope)
{
    public string ScopeLabel => Scope switch { CodeTemplateScope.BuiltIn => "内置", CodeTemplateScope.User => "个人", _ => "工程" };
    public override string ToString() => $"{Template.Name}  ·  {Template.Shortcut}  ·  {ScopeLabel}";
}
