namespace StudioX.Desktop;

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using StudioX.Application;
using StudioX.Engine;
using StudioX.Foundation;

public partial class Ag32PinPlanningView
{
    private Ag32TimingStatus? timing;
    private string? clockError;
    private bool externalDraft;
    internal Ag32TimingState DisplayedTimingState { get; private set; }

    public void SetTiming(Ag32TimingStatus status, bool hasUnsavedText = false)
    {
        timing = status;
        externalDraft = hasUnsavedText;
        UpdateTimingPresentation();
    }

    public void UseRecommendedClocks(Ag32ClockRecommendation recommendation)
    {
        if (busy || Snapshot is null || !Ag32PinPlanningService.ClockRecommendations(Snapshot.DeviceId, GetClocks(), GetAnalog()).Contains(recommendation))
            throw new StudioXException("AG32_CLOCK_RECOMMENDATION", "推荐组合已不适用于当前配置，请重新读取。");
        loading = true;
        try
        {
            SysClock.Text = ClockText(recommendation.Clocks.SysMhz);
            BusClock.Text = ClockText(recommendation.Clocks.BusMhz);
        }
        finally { loading = false; }
        MarkChanged();
    }

    private void RefreshClockPresentation()
    {
        if (Snapshot is null || RecommendedClocks is null || AnalogComparator is null) return;
        var selected = (RecommendedClocks.SelectedItem as Ag32ClockRecommendation)?.Name;
        clockError = null;
        try
        {
            var clocks = GetClocks();
            var analog = GetAnalog();
            clockError = Ag32PinPlanningService.ValidateClocks(Snapshot.DeviceId, clocks);
            ClockGuidanceText.Text = Ag32PinPlanningService.ClockGuidance(Snapshot.DeviceId, clocks, analog);
            RecommendedClocks.ItemsSource = Ag32PinPlanningService.ClockRecommendations(Snapshot.DeviceId, clocks, analog);
            RecommendedClocks.SelectedItem = RecommendedClocks.Items.Cast<Ag32ClockRecommendation>().FirstOrDefault(item => item.Name == selected)
                ?? RecommendedClocks.Items.Cast<Ag32ClockRecommendation>().FirstOrDefault();
        }
        catch (StudioXException error)
        {
            clockError = error.Message;
            ClockGuidanceText.Text = error.Message;
            RecommendedClocks.ItemsSource = Array.Empty<Ag32ClockRecommendation>();
        }
        ClockGuidanceText.SetResourceReference(TextBlock.ForegroundProperty, clockError is null ? "DiagnosticWarning" : "DiagnosticError");
        UpdateRecommendation();
        SetBusy(busy);
    }

    private void UpdateTimingPresentation()
    {
        if (TimingStateText is null) return;
        var state = timing?.State ?? Ag32TimingState.Unverified;
        var text = timing?.Message ?? "尚未验证 / Unverified：请保存配置后编译。";
        var hideEvidence = false;
        if (clockError is not null)
        {
            state = Ag32TimingState.Failed;
            text = "配置错误 / Invalid clocks：" + clockError;
            hideEvidence = true;
        }
        else if (HasChanges || externalDraft || busy)
        {
            state = Ag32TimingState.Unverified;
            text = busy ? "正在处理 / Checking：完成后刷新当前时序结果。"
                : "配置尚未保存 / Unverified：图形或 VE 编辑器有修改，旧结果不能用于当前草稿。";
            hideEvidence = true;
        }
        DisplayedTimingState = state;
        var brush = state == Ag32TimingState.Failed ? "DiagnosticError" : state == Ag32TimingState.Passed ? "DiagnosticSuccess" : "DiagnosticWarning";
        TimingStateText.Text = text;
        TimingStateText.SetResourceReference(TextBlock.ForegroundProperty, brush);
        TimingBorder.SetResourceReference(Border.BorderBrushProperty, brush);
        static string Number(decimal? value) => value?.ToString("0.###", CultureInfo.InvariantCulture) ?? "—";
        static string Bus(Ag32PinClockSettings? clocks) => clocks?.BusMhz is null or 0 ? "跟随 SYS" : Number(clocks.BusMhz) + " MHz";
        TimingSummaryText.Text = !hideEvidence && timing is { Total: > 0 } result
            ? $"SYS {Number(result.Clocks?.SysMhz)} MHz · BUS {Bus(result.Clocks)} · 建立 {Number(result.SetupSlackNs)} ns · 保持 {Number(result.HoldSlackNs)} ns · 覆盖 {result.Covered}/{result.Total}"
            : "保存只检查配置和生成约束；编译完成后才能确认当前逻辑是否满足时序。";
        TimingPathText.Text = !hideEvidence && timing is { Total: > 0 }
            ? "最差建立路径：" + (timing.SetupPath ?? "无可分析路径") + "\n最差保持路径：" + (timing.HoldPath ?? "无可分析路径")
            : "当前配置暂无有效关键路径结果。";
        TimingReportButton.IsEnabled = !hideEvidence && timing?.ReportPath is not null;
    }

    private void UpdateRecommendation()
    {
        if (RecommendationDetails is null) return;
        RecommendationDetails.Text = RecommendedClocks.SelectedItem is Ag32ClockRecommendation item
            ? item.Evidence + " 应用会保存当前整份图形草稿并同步系统参数。"
            : "启用模拟 IP 且实际 HSE 为 8 MHz 时提供离线验证组合；其它配置须单独编译验证。";
    }
    private void Recommendation_Changed(object sender, SelectionChangedEventArgs e) { UpdateRecommendation(); }
    private void ApplyRecommendedClock_Click(object sender, RoutedEventArgs e)
    {
        if (ApplyRecommendedClockButton.IsEnabled && RecommendedClocks.SelectedItem is Ag32ClockRecommendation item)
            RecommendedClockRequested?.Invoke(this, item);
    }
    private void TimingReport_Click(object sender, RoutedEventArgs e)
    {
        if (TimingReportButton.IsEnabled && timing?.ReportPath is { } path) OpenConstraintRequested?.Invoke(this, path);
    }
}
