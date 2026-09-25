namespace StudioX.Desktop;

using System.Windows;
using StudioX.Application;

public partial class AiSettingsWindow : Window
{
    private readonly AiSettingsService settingsService;
    private readonly AiCredentialStore credentials;
    private readonly WebCredentialStore webCredentials;
    private readonly AiSettings savedSettings;

    public AiSettingsWindow(AiSettingsService settingsService, AiCredentialStore credentials, WebCredentialStore webCredentials, AiSettings savedSettings)
    {
        InitializeComponent();
        this.settingsService = settingsService;
        this.credentials = credentials;
        this.webCredentials = webCredentials;
        this.savedSettings = savedSettings;
        BaseUrlInput.Text = savedSettings.BaseUrl;
        ModelInput.Text = savedSettings.Model;
        ContextWindowInput.Text = savedSettings.ContextWindowTokens?.ToString() ?? "";
        RefreshKeyState();
        RefreshWebKeyState();
    }

    private void RefreshKeyState()
    {
        var hasKey = credentials.HasApiKey(savedSettings.BaseUrl);
        KeyState.Text = hasKey ? "已保存 API Key；留空不会更改。" : "当前地址尚未保存 API Key。";
        DeleteKeyButton.IsEnabled = hasKey;
    }

    private void RefreshWebKeyState()
    {
        var hasKey = webCredentials.HasApiKey();
        var hasEnvironmentKey = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TAVILY_API_KEY"));
        WebKeyState.Text = hasKey ? "已保存 Tavily API Key；留空不会更改。" :
            hasEnvironmentKey ? "当前使用 TAVILY_API_KEY 环境变量；可在此保存密钥。" :
            "使用免密钥模式；可随时添加 API Key。";
        DeleteWebKeyButton.IsEnabled = hasKey;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        SaveButton.IsEnabled = false;
        try
        {
            var contextText = ContextWindowInput.Text.Trim();
            if (contextText.Length > 0 && !int.TryParse(contextText, out _))
                throw new ArgumentException("上下文窗口必须是正整数 token 数。");
            var settings = savedSettings with
            {
                BaseUrl = BaseUrlInput.Text.Trim(), Model = ModelInput.Text.Trim(),
                ContextWindowTokens = contextText.Length == 0 ? null : int.Parse(contextText)
            };
            AiSettingsService.Validate(settings);
            var key = KeyInput.Password.Trim();
            if (key.Length > 2560 || key.Any(c => c is < '!' or > '~'))
                throw new ArgumentException("API Key 格式无效。");
            var webKey = WebKeyInput.Password.Trim();
            if (webKey.Length > 2560 || webKey.Any(c => c is < '!' or > '~'))
                throw new ArgumentException("Tavily API Key 格式无效。");
            await settingsService.SaveAsync(settings);
            if (key.Length > 0) credentials.SetApiKey(key, settings.BaseUrl);
            if (webKey.Length > 0) webCredentials.SetApiKey(webKey);
            KeyInput.Clear();
            WebKeyInput.Clear();
            DialogResult = true;
        }
        catch (Exception ex)
        {
            ErrorText.Text = ex.Message;
            SaveButton.IsEnabled = true;
        }
    }

    private void DeleteKey_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            credentials.DeleteApiKey(savedSettings.BaseUrl);
            KeyInput.Clear();
            RefreshKeyState();
            ErrorText.Text = "已删除该地址保存的 API Key。";
        }
        catch (Exception ex) { ErrorText.Text = ex.Message; }
    }

    private void DeleteWebKey_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            webCredentials.DeleteApiKey();
            WebKeyInput.Clear();
            RefreshWebKeyState();
            ErrorText.Text = "已删除保存的 Tavily API Key。";
        }
        catch (Exception ex) { ErrorText.Text = ex.Message; }
    }
}
