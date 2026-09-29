namespace StudioX.Desktop;

using System.Windows.Input;

public static class SourceCommands
{
    public static RoutedUICommand Undo { get; } = new("撤销", nameof(Undo), typeof(SourceCommands));
    public static RoutedUICommand Redo { get; } = new("重做", nameof(Redo), typeof(SourceCommands));
    public static RoutedUICommand ToggleComment { get; } = new("切换行注释", nameof(ToggleComment), typeof(SourceCommands));
    public static RoutedUICommand Find { get; } = new("查找", nameof(Find), typeof(SourceCommands));
    public static RoutedUICommand Replace { get; } = new("替换", nameof(Replace), typeof(SourceCommands));
    public static RoutedUICommand ReplaceAll { get; } = new("全部替换（当前文件）", nameof(ReplaceAll), typeof(SourceCommands));
    public static RoutedUICommand WorkspaceFind { get; } = new("在工程中查找", nameof(WorkspaceFind), typeof(SourceCommands));
    public static RoutedUICommand WorkspaceReplace { get; } = new("在工程中替换", nameof(WorkspaceReplace), typeof(SourceCommands));
    public static RoutedUICommand RenameSymbol { get; } = new("重命名符号", nameof(RenameSymbol), typeof(SourceCommands));
}
