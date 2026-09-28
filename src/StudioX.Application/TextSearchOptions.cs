namespace StudioX.Application;

public sealed record TextSearchOptions(string Pattern, bool MatchCase = false, bool WholeWord = false, bool RegularExpression = false);
