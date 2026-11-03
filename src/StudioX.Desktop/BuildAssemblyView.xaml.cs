namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Search;
using StudioX.Engine;

public partial class BuildAssemblyView : UserControl
{
    private BuildAssemblyDocument? document;
    private bool settingFiles;
    public event Action? CloseRequested;
    public event Action? RefreshRequested;
    public event Action<BuildAssemblyFile>? FileSelected;
    internal TextEditor Editor => AssemblyEditor;
    internal string StatusText => Hint.Text;
    internal int FileCount => Files.Items.Count;
    internal BuildAssemblyFile? SelectedFile => Files.SelectedItem as BuildAssemblyFile;
    public BuildAssemblyView()
    {
        InitializeComponent();
        AssemblyEditor.Document.UndoStack.SizeLimit = 0;
        AssemblyEditor.Options.EnableHyperlinks = false;
        AssemblyEditor.Options.EnableEmailHyperlinks = false;
        SearchPanel.Install(AssemblyEditor);
    }
    public void SetFiles(BuildAssemblyCatalog catalog, string? source)
    {
        settingFiles = true;
        try
        {
            Files.ItemsSource = catalog.Files;
            Files.SelectedItem = catalog.Files.FirstOrDefault(file => file.SourcePath == source) ?? catalog.Files.FirstOrDefault();
        }
        finally { settingFiles = false; }
    }
    public void SetMessage(string text)
    {
        settingFiles = true;
        try
        {
            Files.ItemsSource = null;
        }
        finally { settingFiles = false; }
        document = null;
        AssemblyEditor.Text = "";
        Hint.Text = text;
    }
    public void SelectSource(string source)
    {
        var matching = Files.Items.Cast<BuildAssemblyFile>().FirstOrDefault(file => file.SourcePath == source &&
            Path.GetExtension(file.RelativePath) == Path.GetExtension(SelectedFile?.RelativePath)) ??
            Files.Items.Cast<BuildAssemblyFile>().FirstOrDefault(file => file.SourcePath == source);
        if (matching is not null)
        {
            Files.SelectedItem = matching;
        }
    }
    public void SetDocument(BuildAssemblyDocument value, string device)
    {
        document = value;
        Hint.Text = device + " · " + value.File.Kind + "。只读构建产物，不读取实板。";
        Raw_Changed(this, new RoutedEventArgs());
    }
    public void RefreshTheme(bool dark) => AssemblyEditor.SyntaxHighlighting = CodeLanguage.Get("Assembly", dark);
    public void SetLoading()
    {
        document = null;
        AssemblyEditor.Text = "";
        Hint.Text = "正在读取所选汇编文件…";
    }
    private void Files_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!settingFiles && SelectedFile is { } file)
        {
            FileSelected?.Invoke(file);
        }
    }
    private void Raw_Changed(object sender, RoutedEventArgs e)
    {
        if (document is not null)
        {
            AssemblyEditor.ShowLineNumbers = Raw.IsChecked == true;
            AssemblyEditor.Text = Raw.IsChecked == true ? document.RawText : document.Text;
        }
    }
    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshRequested?.Invoke();
}
