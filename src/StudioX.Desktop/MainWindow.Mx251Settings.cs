namespace StudioX.Desktop;

using StudioX.Application;
using StudioX.Engine;
using StudioX.Foundation;

public partial class MainWindow
{
    private Mx251ProjectSettings? loadedMx251Settings;
    private bool IsMx251Project => currentProjectManifest is { } project &&
        (project is { ToolsetId: Mx251Profile.ToolsetId, CompilerId: Mx251Profile.CompilerId } || Mx251SdkProfile.Matches(project));

    private void ApplyMx251Settings(Mx251ProjectSettings settings)
    {
        applyingBuildSettings = true;
        loadedMx251Settings = settings;
        Mx251ModelPicker.SelectedValue = settings.Model;
        Mx251OptimizationPicker.SelectedValue = settings.Optimization;
        Mx251StandardPicker.SelectedValue = settings.Standard;
        applyingBuildSettings = false;
        UpdateMx251SettingsControls();
        BuildSettingsStatus.Text = "修改后点击保存，下次编译生效。";
    }

    private bool Mx251SettingsChanged => loadedMx251Settings is { } saved &&
        (Mx251ModelPicker.SelectedValue as string != saved.Model ||
         Mx251OptimizationPicker.SelectedValue as string != saved.Optimization ||
         Mx251StandardPicker.SelectedValue as string != saved.Standard);

    private void UpdateMx251SettingsControls()
    {
        var enabled = projectDirectory is not null && loadedMx251Settings is not null && !projectActionsBusy && !services.Debugger.IsActive;
        BuildSettingsEditor.IsEnabled = enabled;
        StcBuildExtrasHost.IsEnabled = enabled;
        var model = Mx251ModelPicker.SelectedValue as string;
        var optimization = Mx251OptimizationPicker.SelectedValue as string;
        var standard = Mx251StandardPicker.SelectedValue as string;
        SaveBuildSettingsButton.IsEnabled = enabled && model is "small" or "large" &&
            optimization is "size" or "speed" && standard is not null && Mx251SettingsChanged;
        BuildSettingsSummary.Text = model is null ? "正在读取编译参数…" :
            $"MX251 · {model} · {(optimization == "size" ? "体积优先" : "速度优先")} · {standard}";
    }

    private async Task SaveMx251SettingsAsync(string directory, CancellationToken token)
    {
        if (FindEditor(Mx251Profile.ConfigurationFile) is { IsDirty: true })
        {
            throw new StudioXException("MX251_SETTINGS_UNSAVED", "mx251.json 有未保存的编辑，请先保存文件，再重新打开工程详情。");
        }
        var expected = loadedMx251Settings ?? throw new StudioXException("MX251_SETTINGS", "请先重新打开工程详情。");
        var model = Mx251ModelPicker.SelectedValue as string ?? throw new StudioXException("MX251_MODEL", "请选择内存模型。");
        var optimization = Mx251OptimizationPicker.SelectedValue as string ?? throw new StudioXException("MX251_OPTIMIZATION", "请选择优化方式。");
        var standard = Mx251StandardPicker.SelectedValue as string ?? throw new StudioXException("MX251_STANDARD", "请选择 C 语言标准。");
        var saved = await services.Mx251Settings.SaveAsync(directory, expected, model, optimization, standard, token);
        if (directory != projectDirectory || closing)
        {
            return;
        }
        if (FindEditor(Mx251Profile.ConfigurationFile) is { } editor)
        {
            EditorSynchronizer.Apply(editor, saved.Configuration);
        }
        ApplyMx251Settings(saved);
        BuildMemory.SetMessage("编译参数已修改，重新编译后更新占用。");
        BuildSettingsStatus.Text = Status.Text = "编译参数已保存，下次编译生效。";
    }
}
