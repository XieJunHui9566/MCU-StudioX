namespace StudioX.Application.BuildConfiguration;

using StudioX.Application.Editing;

public sealed record SourceRegistrationPlan(SourceRegistrationContext Context, string Target,
    IReadOnlyList<SourceRegistrationOperation> Operations, WorkspaceFileChange Change);
