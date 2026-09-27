namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using StudioX.Application;

/// <summary>创建聊天气泡；工程状态和历史选择由控制器管理，气泡仅绑定主题资源。</summary>
internal static class AiTranscriptRenderer
{
    public static Grid CreateBubble(string role, string content, AiAgentTurn? turn = null,
        string? heading = null)
    {
        var isUser = role == "你";
        var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = isUser ? new GridLength(28) : new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = isUser ? new GridLength(1, GridUnitType.Star) : new GridLength(28) });
        var bubble = new Border
        {
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(11, 8, 11, 9)
        };
        bubble.SetResourceReference(Border.BackgroundProperty, isUser ? "Accent" : "ChromeSurface");
        bubble.SetResourceReference(Border.BorderBrushProperty, isUser ? "Accent" : "Border");
        var body = new StackPanel();
        var label = new TextBlock { Text = heading ?? role, FontSize = 10, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) };
        if (isUser)
        {
            label.Foreground = Brushes.White;
        }
        else
        {
            label.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        }
        var message = new TextBlock { Text = content, TextWrapping = TextWrapping.Wrap, FontSize = 12.5 };
        if (isUser)
        {
            message.Foreground = Brushes.White;
        }
        else
        {
            message.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        }
        body.Children.Add(label);
        body.Children.Add(message);
        if (role == "AI" && turn is not null)
        {
            AiTurnDetailRenderer.AddTurnDetails(body, turn);
        }
        bubble.Child = body;
        Grid.SetColumn(bubble, isUser ? 1 : 0);
        row.Children.Add(bubble);
        return row;
    }

}
