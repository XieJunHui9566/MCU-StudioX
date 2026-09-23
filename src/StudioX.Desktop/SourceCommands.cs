namespace StudioX.Desktop;

using System.Windows.Input;

public static class SourceCommands
{
    public static RoutedUICommand Undo { get; } = new("撤销", nameof(Undo), typeof(SourceCommands));
    public static RoutedUICommand Redo { get; } = new("重做", nameof(Redo), typeof(SourceCommands));
    public static RoutedUICommand ToggleComment { get; } = new("切换行注释", nameof(ToggleComment), typeof(SourceCommands));
}
