namespace StudioX.Application.Skills;

public sealed record AgentSkillDiscovery(
    IReadOnlyList<AgentSkillMetadata> Skills,
    IReadOnlyList<string> Diagnostics);
