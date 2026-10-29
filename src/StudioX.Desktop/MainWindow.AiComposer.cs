namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Input;

public partial class MainWindow
{
    private bool aiImeComposing;

    private void AiPrompt_CompositionStarted(object sender, TextCompositionEventArgs e) => aiImeComposing = e.TextComposition.CompositionText.Length > 0;
    private void AiPrompt_CompositionCompleted(object sender, TextCompositionEventArgs e) => aiImeComposing = false;
    private void AiPrompt_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => aiImeComposing = false;

    private void AiPrompt_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // 中文输入法的确认键用于提交候选字，不能顺带发送整条消息。
        if (e.Key == Key.Escape)
        {
            aiImeComposing = false;
        }
        var action = AiComposerKeyPolicy.Resolve(e.Key, Keyboard.Modifiers,
            Keyboard.IsKeyDown(Key.LeftShift), aiImeComposing, e.IsRepeat);
        if (action == AiComposerKeyAction.Ignore)
        {
            return;
        }
        e.Handled = true;
        if (action == AiComposerKeyAction.NewLine)
        {
            InsertAiComposerNewLine();
            return;
        }
        if (action == AiComposerKeyAction.Send && AiSendButton.IsEnabled)
        {
            AiSend_Click(sender, new RoutedEventArgs());
        }
    }

    private void InsertAiComposerNewLine()
    {
        var caret = AiPromptInput.SelectionStart;
        AiPromptInput.SelectedText = Environment.NewLine;
        AiPromptInput.CaretIndex = caret + Environment.NewLine.Length;
        AiPromptInput.SelectionLength = 0;
    }

    private void AiSkills_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new AiSkillsWindow(services.AiSkills, projectDirectory ?? services.DataDirectory) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.SelectedSkill is { } name)
        {
            AiPromptInput.Text = $"使用技能 ${name}：" + AiPromptInput.Text;
            AiPromptInput.CaretIndex = AiPromptInput.Text.Length;
            AiPromptInput.Focus();
        }
    }
}
