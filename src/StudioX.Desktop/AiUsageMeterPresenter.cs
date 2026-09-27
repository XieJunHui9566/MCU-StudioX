namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using StudioX.Application;

/// <summary>展示 API 实际返回的最近请求用量；缺失缓存字段时不推算命中率。</summary>
internal sealed class AiUsageMeterPresenter(
    System.Windows.Shapes.Path contextArc,
    System.Windows.Shapes.Ellipse contextFull,
    FrameworkElement contextBadge,
    TextBlock cacheUsageText)
{
    public void Update(AiSettings settings, int? promptTokens, bool hasRequest,
        long? cacheHitTokens = null, long? cacheMissTokens = null)
    {
        var window = AiSettingsService.EffectiveContextWindowTokens(settings);
        contextArc.Visibility = contextFull.Visibility = Visibility.Collapsed;
        var description = "尚无上下文用量。";
        if (promptTokens is { } used && window is { } maximum)
        {
            var fraction = Math.Clamp((double)used / maximum, 0, 1);
            description = $"上下文已用 {fraction:P0}\n最近一次请求输入 {used:N0} / {maximum:N0} tokens";
            if (fraction >= 1)
            {
                contextFull.Visibility = Visibility.Visible;
            }
            else if (fraction > 0)
            {
                const double center = 11;
                const double radius = 8.5;
                var angle = fraction * 2 * Math.PI - Math.PI / 2;
                var end = new Point(center + radius * Math.Cos(angle), center + radius * Math.Sin(angle));
                var figure = new PathFigure
                {
                    StartPoint = new Point(center, center - radius),
                    IsClosed = false,
                    IsFilled = false
                };
                figure.Segments.Add(new ArcSegment(end, new Size(radius, radius), 0,
                    fraction > 0.5, SweepDirection.Clockwise, true));
                var geometry = new PathGeometry();
                geometry.Figures.Add(figure);
                contextArc.Data = geometry;
                contextArc.Visibility = Visibility.Visible;
            }
        }
        else if (promptTokens is { } count)
        {
            description = $"最近一次请求输入 {count:N0} tokens；上下文窗口容量未知。";
        }
        else if (hasRequest)
        {
            description = "API 未提供 token 用量。";
        }
        string cacheText;
        string cacheDetails;
        if (cacheHitTokens is { } hit && cacheMissTokens is { } miss)
        {
            var reportedTotal = (decimal)hit + miss;
            if (reportedTotal > 0)
            {
                var rate = (double)((decimal)hit / reportedTotal);
                cacheText = $"最近一轮缓存命中 {hit:N0} / {reportedTotal:N0} tokens · {rate:P0}";
                cacheDetails = $"最近一轮 API 已报告：命中 {hit:N0} tokens，未命中 {miss:N0} tokens，命中率 {rate:P1}。";
            }
            else
            {
                cacheText = "最近一轮缓存：API 返回 0 / 0 tokens";
                cacheDetails = "最近一轮 API 返回的缓存命中与未命中 token 均为 0，无法计算命中率。";
            }
        }
        else if (cacheHitTokens is { } reportedHit)
        {
            cacheText = $"最近一轮缓存命中 {reportedHit:N0} tokens · 未返回未命中量";
            cacheDetails = "API 只返回缓存命中 token，未返回未命中 token；无法计算命中率。";
        }
        else if (cacheMissTokens is { } reportedMiss)
        {
            cacheText = $"最近一轮缓存未命中 {reportedMiss:N0} tokens · 未返回命中量";
            cacheDetails = "API 只返回缓存未命中 token，未返回命中 token；无法计算命中率。";
        }
        else if (hasRequest)
        {
            cacheText = "缓存：这段对话暂无 API 用量记录";
            cacheDetails = "旧对话未保存缓存字段，或 API 未返回缓存用量。下次成功请求后会更新。";
        }
        else
        {
            cacheText = "缓存：发送后显示 API 用量";
            cacheDetails = "发送消息后显示 API 实际返回的缓存命中和未命中 token。";
        }
        cacheUsageText.Text = cacheText;
        cacheUsageText.ToolTip = cacheDetails;
        System.Windows.Automation.AutomationProperties.SetName(cacheUsageText, cacheDetails);
        description += "\n" + cacheDetails;
        contextBadge.ToolTip = description;
        System.Windows.Automation.AutomationProperties.SetName(contextBadge, description);
    }

}
