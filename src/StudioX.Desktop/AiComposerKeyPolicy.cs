namespace StudioX.Desktop;

using System.Windows.Input;

internal static class AiComposerKeyPolicy
{
    public static AiComposerKeyAction Resolve(Key key, ModifierKeys modifiers, bool leftShift,
        bool composing, bool repeat)
    {
        if (composing || key != Key.Return || (modifiers & (ModifierKeys.Alt | ModifierKeys.Windows)) != 0)
        {
            return AiComposerKeyAction.Ignore;
        }
        if (repeat)
        {
            return AiComposerKeyAction.Consume;
        }
        return leftShift ? AiComposerKeyAction.NewLine : AiComposerKeyAction.Send;
    }
}
