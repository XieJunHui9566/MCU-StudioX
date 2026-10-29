namespace StudioX.WorkspaceEditingValidation;

using StudioX.Application.CodeIntelligence;

internal static class CompletionTriggerChecks
{
    internal static void Run(Action<bool, string> check)
    {
        foreach (var input in new[] { "P", "P1", "P11", "TIM2", "GPIO_Pin_1", "uint8", "a.P11", "_2", "ns::Timer2" })
        {
            check(CodeCompletionTrigger.ShouldComplete(input, input.Length, input[^1]), "identifier input triggers completion before a popup exists: " + input);
        }
        foreach (var input in new[] { "1", "42", "0x11", "1.25", "1 ", "P1;", "P1=", "a[2", "" })
        {
            check(!CodeCompletionTrigger.ShouldComplete(input, input.Length, input.Length == 0 ? '1' : input[^1]), "number or delimiter does not trigger identifier completion: " + input);
        }
        check(!CodeCompletionTrigger.ShouldComplete("P11", 4, '1') && !CodeCompletionTrigger.ShouldComplete("P11", 2, 'P'), "stale caret or input snapshot is rejected");
        foreach (var symbol in new[] { '.', '>', ':', '"', '<', '/' })
        {
            check(CodeCompletionTrigger.ShouldComplete("x" + symbol, 2, symbol), "existing member/include trigger is retained: " + symbol);
        }
    }
}
