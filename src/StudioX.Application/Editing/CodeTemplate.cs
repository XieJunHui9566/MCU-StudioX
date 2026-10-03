namespace StudioX.Application.Editing;

/// <summary>模板只保存文本，不执行脚本；语言适用范围与器件、开发环境组件版本无关。</summary>
public sealed record CodeTemplate(string Id, string Name, string Shortcut, string Language, string Description, string Body);

public enum CodeTemplateScope { BuiltIn, User, Project }

public sealed record CodeTemplateEntry(CodeTemplate Template, CodeTemplateScope Scope)
{
    public string ScopeLabel => Scope switch { CodeTemplateScope.BuiltIn => "内置", CodeTemplateScope.User => "个人", _ => "工程" };
    public override string ToString() => $"{Template.Name}  ·  {Template.Shortcut}  ·  {ScopeLabel}";
}

public sealed record CodeTemplateStore(CodeTemplateScope Scope, string Path, string Revision, IReadOnlyList<CodeTemplate> Templates);
public sealed record CodeTemplateLibrary(CodeTemplateStore User, CodeTemplateStore? Project)
{
    public IReadOnlyList<CodeTemplateEntry> Entries => BuiltInCodeTemplates.All.Select(t => new CodeTemplateEntry(t, CodeTemplateScope.BuiltIn))
        .Concat(User.Templates.Select(t => new CodeTemplateEntry(t, CodeTemplateScope.User)))
        .Concat(Project?.Templates.Select(t => new CodeTemplateEntry(t, CodeTemplateScope.Project)) ?? []).ToArray();
}

public sealed record CodeTemplateParameter(string Name, string DefaultValue);
public sealed record CodeTemplateContext(string Selection, string FileName);
public sealed record CodeTemplateExpansion(string Text, int CaretOffset);
