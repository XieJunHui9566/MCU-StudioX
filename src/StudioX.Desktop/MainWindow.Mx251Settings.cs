namespace StudioX.Desktop;

using StudioX.Application;
using StudioX.Engine;
using StudioX.Foundation;

public partial class MainWindow
{
    private Mx251ProjectSettings? loadedMx251Settings;
    private bool IsMx251Project => currentProjectManifest is { ToolsetId: Mx251Profile.ToolsetId, CompilerId: Mx251Profile.CompilerId };

    private void ApplyMx251Settings(Mx251ProjectSettings settings)
    {
        applyingBuildSettings = true;
        loadedMx251Settings = settings;
        Mx251ModelPicker.SelectedValue = settings.Model;
        applyingBuildSettings = false;
        UpdateMx251SettingsControls();
        BuildSettingsStatus.Text = "修改后点击保存，下次编译生效。";
    }

    private void UpdateMx251SettingsControls()
    {
        var enabled = projectDirectory is not null && loadedMx251Settings is not null && !projectActionsBusy && !services.Debugger.IsActive;
        BuildSettingsEditor.IsEnabled = enabled;
        var model = Mx251ModelPicker.SelectedValue as string;
        SaveBuildSettingsButton.IsEnabled = enabled && model is "small" or "large" && model != loadedMx251Settings?.Model;
        BuildSettingsSummary.Text = model is null ? "正在读取内存模型…" : "MX251 · 32 位模式 · " + model;
    }

    private async Task SaveMx251SettingsAsync(string directory, CancellationToken token)
    {
        if (FindEditor(Mx251Profile.ConfigurationFile) is { IsDirty: true })
        {
            throw new StudioXException("MX251_SETTINGS_UNSAVED", "mx251.json 有未保存的编辑，请先保存文件，再重新打开工程详情。");
        }
        var expected = loadedMx251Settings ?? throw new StudioXException("MX251_SETTINGS", "请先重新打开工程详情。");
        var model = Mx251ModelPicker.SelectedValue as string ?? throw new StudioXException("MX251_MODEL", "请选择内存模型。");
        var saved = await services.Mx251Settings.SaveModelAsync(directory, expected, model, token);
        if (directory != projectDirectory || closing)
        {
            return;
        }
        if (FindEditor(Mx251Profile.ConfigurationFile) is { } editor)
        {
            EditorSynchronizer.Apply(editor, saved.Configuration);
        }
        ApplyMx251Settings(saved);
        BuildMemory.SetMessage("内存模型已修改，重新编译后更新占用。");
        BuildSettingsStatus.Text = Status.Text = "内存模型已保存，下次编译生效。";
    }
}
