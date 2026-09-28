namespace StudioX.Desktop;

using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;

/// <summary>核对本次双镜像下载；窗口关闭、取消或工程失效均不授予写入许可。</summary>
public partial class Ag32DownloadConfirmationWindow : Window
{
    private Func<bool>? isCurrent;
    private Window? confirmationOwner;
    private CancellationToken cancellationToken;
    private bool ownerClosing;
    private bool closed;

    public Ag32DownloadConfirmationWindow(string details)
    {
        InitializeComponent();
        DetailsText.Text = details;
        ContentRendered += (_, _) => CancelButton.Focus();
        Closed += (_, _) => closed = true;
    }

    internal bool Confirm(Func<bool> isCurrent, CancellationToken token)
    {
        this.isCurrent = isCurrent;
        cancellationToken = token;
        confirmationOwner = Owner;
        if (confirmationOwner is not { } owner || !CanApprove())
        {
            Close();
            return false;
        }

        var contextWatch = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        contextWatch.Tick += ContextWatch_Tick;
        owner.Closing += Owner_Closing;
        using var cancellation = token.Register(() =>
        {
            // 取消可来自工作线程，不能同步等待被模态窗口占用的 UI 线程。
            Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(CancelConfirmation));
        });
        contextWatch.Start();
        try
        {
            return ShowDialog() == true && CanApprove();
        }
        finally
        {
            contextWatch.Stop();
            contextWatch.Tick -= ContextWatch_Tick;
            owner.Closing -= Owner_Closing;
        }
    }

    private bool CanApprove() => !ownerClosing && !cancellationToken.IsCancellationRequested &&
        confirmationOwner is { IsVisible: true } && isCurrent?.Invoke() == true;

    private void ContextWatch_Tick(object? sender, EventArgs e)
    {
        if (!CanApprove())
        {
            CancelConfirmation();
        }
    }

    private void Owner_Closing(object? sender, CancelEventArgs e)
    {
        ownerClosing = true;
        CancelConfirmation();
    }

    private void CancelConfirmation()
    {
        if (!closed && IsVisible)
        {
            DialogResult = false;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => CancelConfirmation();

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (!closed && IsVisible)
        {
            DialogResult = CanApprove();
        }
    }
}
