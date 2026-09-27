namespace StudioX.Desktop;

using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Rendering;

/// <summary>隔离语法高亮故障，保留可编辑的文档并通知界面记录原始诊断。</summary>
public sealed class SafeTextEditor : TextEditor
{
    private GuardedHighlightingColorizer? currentColorizer;

    /// <summary>高亮已降级为纯文本后，在界面线程报告语言和原始异常。</summary>
    public event Action<string, Exception>? HighlightingFailed;

    protected override IVisualLineTransformer CreateColorizer(IHighlightingDefinition highlightingDefinition)
    {
        currentColorizer = new GuardedHighlightingColorizer(this, highlightingDefinition);
        return currentColorizer;
    }

    private void QueueHighlightingFailure(
        GuardedHighlightingColorizer colorizer,
        TextDocument document,
        long registration,
        Exception exception)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        // 渲染正在枚举着色器；延后移除，避免破坏当前 VisualLine 的构建过程。
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            // 文件或主题可能已经切换；旧文档的失败不能关闭新文档的高亮。
            if (currentColorizer != colorizer
                || Document != document
                || SyntaxHighlighting != colorizer.Definition
                || colorizer.Registration != registration)
            {
                return;
            }

            currentColorizer = null;
            SyntaxHighlighting = null;
            TextArea.TextView.Redraw();
            HighlightingFailed?.Invoke(colorizer.Definition.Name, exception);
        }));
    }

    private sealed class GuardedHighlightingColorizer : HighlightingColorizer
    {
        private readonly SafeTextEditor owner;
        private bool failed;

        internal IHighlightingDefinition Definition
        {
            get;
        }
        internal long Registration
        {
            get; private set;
        }

        internal GuardedHighlightingColorizer(SafeTextEditor owner, IHighlightingDefinition definition)
            : base(definition)
        {
            this.owner = owner;
            Definition = definition;
        }

        protected override void RegisterServices(TextView textView)
        {
            // 相同语言切换文档会复用着色器；新注册使旧故障通知失效并恢复着色。
            Registration++;
            failed = false;
            base.RegisterServices(textView);
        }

        protected override void Colorize(ITextRunConstructionContext context)
        {
            if (failed)
            {
                return;
            }

            try
            {
                // ColorizeLine 之外还会更新跨行状态，必须保护整个高亮边界。
                base.Colorize(context);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // 只停止这个着色器，保留文本和撤销栈；内存耗尽不能当作规则错误恢复。
                failed = true;
                owner.QueueHighlightingFailure(this, context.Document, Registration, exception);
            }
        }
    }
}
