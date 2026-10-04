namespace StudioX.Application.Editing;

public sealed record CodeTemplateStore(CodeTemplateScope Scope, string Path, string Revision, IReadOnlyList<CodeTemplate> Templates);
