namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Input;

public partial class MainWindow
{
    private EditorFoldingController? sourceFolding;
    private EditorFoldingController? mirrorFolding;

    private void InitializeFolding()
    {
        sourceFolding = new(SourceEditor, error => Log("代码折叠：" + error));
        Closed += (_, _) =>
        {
            sourceFolding.Dispose();
            mirrorFolding?.Dispose();
        };
        SourceEditor.TextArea.PreviewKeyDown += async (_, e) =>
        {
            if (Keyboard.Modifiers != (ModifierKeys.Control | ModifierKeys.Shift) || !IsActiveSourceTab)
            {
                return;
            }
            if (e.Key is Key.OemOpenBrackets or Key.OemCloseBrackets or Key.OemMinus or Key.OemPlus)
            {
                e.Handled = true;
                await sourceFolding.SetFoldedAsync(e.Key is Key.OemOpenBrackets or Key.OemMinus, e.Key is Key.OemMinus or Key.OemPlus);
            }
        };
    }

    private async void FoldCurrent_Click(object sender, RoutedEventArgs e) => await SetSourceFoldingAsync(true, false);
    private async void UnfoldCurrent_Click(object sender, RoutedEventArgs e) => await SetSourceFoldingAsync(false, false);
    private async void FoldAll_Click(object sender, RoutedEventArgs e) => await SetSourceFoldingAsync(true, true);
    private async void UnfoldAll_Click(object sender, RoutedEventArgs e) => await SetSourceFoldingAsync(false, true);

    private Task SetSourceFoldingAsync(bool folded, bool all) => IsActiveSourceTab && sourceFolding is not null
        ? sourceFolding.SetFoldedAsync(folded, all) : Task.CompletedTask;
}
