namespace StudioX.Application.Editing;

public sealed record CodeTemplateLibrary(CodeTemplateStore User, CodeTemplateStore? Project)
{
    public IReadOnlyList<CodeTemplateEntry> Entries => BuiltInCodeTemplates.All.Select(t => new CodeTemplateEntry(t, CodeTemplateScope.BuiltIn))
        .Concat(User.Templates.Select(t => new CodeTemplateEntry(t, CodeTemplateScope.User)))
        .Concat(Project?.Templates.Select(t => new CodeTemplateEntry(t, CodeTemplateScope.Project)) ?? []).ToArray();
}
