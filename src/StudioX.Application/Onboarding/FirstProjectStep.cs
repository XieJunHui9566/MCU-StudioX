namespace StudioX.Application.Onboarding;

public sealed record FirstProjectStep(string Title, string Instructions, string Expected, string Status,
    string Action, string ActionTitle, string HelpTopic, bool Complete = false, bool CanAct = true);
