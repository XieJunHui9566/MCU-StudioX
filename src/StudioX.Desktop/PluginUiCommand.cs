namespace StudioX.Desktop;

using System.Windows.Input;

/// <summary>把插件贡献绑定到宿主命令可用状态；插件的实际操作由应用会话执行。</summary>
internal sealed class PluginUiCommand(Func<Task> execute, Func<bool> canExecute) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
    public bool CanExecute(object? parameter) => canExecute();

    public async void Execute(object? parameter)
    {
        if (canExecute())
        {
            await execute();
        }
    }

    public void Refresh() => CommandManager.InvalidateRequerySuggested();
}
