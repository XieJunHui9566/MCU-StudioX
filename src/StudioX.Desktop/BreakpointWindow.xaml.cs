namespace StudioX.Desktop;

using System.Globalization;
using System.Windows;
using StudioX.Engine.Debugging;

public partial class BreakpointWindow : Window
{
    public BreakpointOptions? Options { get; private set; }
    public BreakpointWindow(string file, int line, BreakpointOptions options)
    {
        InitializeComponent();
        LocationLabel.Text = $"{file}  ·  第 {line} 行";
        ConditionInput.Text = options.Condition;
        IgnoreInput.Text = options.IgnoreCount.ToString(CultureInfo.InvariantCulture);
        TemporaryInput.IsChecked = options.Temporary;
        LogEnabledInput.IsChecked = options.LogMessage is not null;
        if (options.LogMessage is not null) LogInput.Text = options.LogMessage;
    }
    private void LogEnabled_Changed(object sender, RoutedEventArgs e)
    { if (LogInput is not null) LogInput.IsEnabled = LogEnabledInput.IsChecked == true; }
    internal bool ReadOptions()
    {
        try
        {
            if (!int.TryParse(IgnoreInput.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var count)) throw new ArgumentException("跳过次数请输入 0–1000000 的整数。");
            var options = new BreakpointOptions(ConditionInput.Text.Trim(), count, TemporaryInput.IsChecked == true, LogEnabledInput.IsChecked == true ? LogInput.Text : null);
            options.Validate(); Options = options; ErrorLabel.Text = ""; return true;
        }
        catch (ArgumentException ex) { Options = null; ErrorLabel.Text = ex.Message; return false; }
    }
    private void Accept_Click(object sender, RoutedEventArgs e) { if (ReadOptions()) DialogResult = true; }
}
