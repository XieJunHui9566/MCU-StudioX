namespace StudioX.Desktop;

using System.Globalization;
using System.Text.Json;
using System.Windows.Threading;

internal partial class ProgrammerCalculatorView
{
    private readonly DispatcherTimer previewTimer = new() { Interval = TimeSpan.FromMilliseconds(45) };
    private readonly Queue<CalculatorInput> actions = [];
    private readonly Dictionary<int, long> submittedRevisions = [];
    private CalculatorInput? pendingPreview;
    private bool pumping;
    private bool previewTimerAttached;
    private int latestRequestId;
    private int appliedRequestId;
    private long consumedRevision = -1;
    private bool lastSucceeded;

    internal Func<JsonElement, Task<JsonElement>>? InvokeCalculatorAsync
    {
        get; set;
    }
    internal Task InputIdle { get; private set; } = Task.CompletedTask;

    private void SchedulePreview()
    {
        if (state is null || !state.Realtime)
        {
            return;
        }
        if (!previewTimerAttached)
        {
            previewTimer.Tick += (_, _) => { previewTimer.Stop(); _ = SendAsync("preview"); };
            previewTimerAttached = true;
        }
        previewTimer.Stop();
        previewTimer.Start();
        StatusText.Text = "正在更新进制…";
    }

    private Task SendAsync(string action, string? setting = null, string? choice = null, int? bit = null, string? operation = null)
    {
        if (!IsEnabled)
        {
            return Task.CompletedTask;
        }
        previewTimer.Stop();
        try
        {
            var start = 0;
            var count = 1;
            if (state.FieldMode && action != "clear" && (!int.TryParse(FieldStart.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out start) ||
                !int.TryParse(FieldCount.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out count)))
            {
                throw new ArgumentException("位域起始位和长度必须为整数。");
            }
            var input = new CalculatorInput(++latestRequestId, draftRevision, action, setting, choice, bit, operation,
                JsonSerializer.SerializeToElement(new
                {
                    width = state.Width.ToString(CultureInfo.InvariantCulture),
                    radix = state.Radix.ToString(CultureInfo.InvariantCulture),
                    signedMode = state.SignedMode,
                    value = string.IsNullOrWhiteSpace(Expression.Text) ? "0" : Expression.Text,
                    start,
                    count,
                    operand = FieldOperand.Text,
                    operation = action == "clear" ? "evaluate" : operation ?? (state.FieldMode ? "inspect" : "evaluate")
                }));
            submittedRevisions[input.Id] = input.Revision;
            if (action == "preview")
            {
                pendingPreview = input;
            }
            else
            {
                pendingPreview = null;
                if (actions.Count >= 256)
                {
                    throw new InvalidOperationException("计算器操作过快，请等待当前操作完成。");
                }
                actions.Enqueue(input);
            }
            if (!pumping)
            {
                InputIdle = PumpAsync();
            }
            return input.Completion.Task;
        }
        catch (Exception error) { diagnostic(error.ToString()); StatusText.Text = error.Message; return Task.CompletedTask; }
    }

    private async Task PumpAsync()
    {
        pumping = true;
        try
        {
            while (actions.Count > 0 || pendingPreview is not null)
            {
                var input = actions.Count > 0 ? actions.Dequeue() : pendingPreview!;
                if (ReferenceEquals(input, pendingPreview))
                {
                    pendingPreview = null;
                }
                try
                {
                    // 连续点击移位等操作沿用上次成功结果；输入改变后才重新提交文本，避免连点丢失。
                    var useCurrent = state.Realtime && input.Action is not ("preview" or "calculate" or "clear") && lastSucceeded && consumedRevision == input.Revision;
                    var args = new Dictionary<string, object?>
                    {
                        ["action"] = input.Action,
                        ["requestId"] = input.Id,
                        ["setting"] = input.Setting,
                        ["choice"] = input.Choice,
                        ["bit"] = input.Bit,
                        ["operation"] = input.Operation
                    };
                    if (!useCurrent)
                    {
                        args["values"] = input.Values;
                    }
                    var request = JsonSerializer.SerializeToElement(args);
                    if (InvokeCalculatorAsync is { } invoke)
                    {
                        var response = await invoke(request);
                        lastSucceeded = response.ValueKind == JsonValueKind.Object && response.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True;
                        if (response.ValueKind == JsonValueKind.Object && response.TryGetProperty("view", out var view))
                        {
                            Update(view);
                        }
                    }
                    else
                    {
                        await submit(request);
                        lastSucceeded = true;
                    }
                    consumedRevision = input.Revision;
                }
                catch (Exception error) { lastSucceeded = false; diagnostic(error.ToString()); StatusText.Text = error is OperationCanceledException ? "计算器响应超时或会话已结束，可继续编辑。" : error.Message; }
                finally { input.Completion.TrySetResult(); }
            }
        }
        finally
        {
            pumping = false;
            foreach (var id in submittedRevisions.Keys.Where(id => id < appliedRequestId || id < latestRequestId - 512).ToArray())
            {
                submittedRevisions.Remove(id);
            }
        }
    }

    private Task ClearAsync()
    {
        previewTimer.Stop();
        pendingPreview = null;
        while (actions.TryDequeue(out var discarded))
        {
            discarded.Completion.TrySetResult();
        }
        var id = latestRequestId + 1;
        applyingState = true;
        Expression.Text = "0";
        applyingState = false;
        draftRevision++;
        draftChanged = false;
        var cleared = state with
        {
            RequestId = 0,
            Preview = false,
            Pending = false,
            Error = null,
            Expression = "0",
            Hex = new string('0', state.Width / 4),
            DecimalText = "0",
            Octal = "0",
            Binary = new string('0', state.Width),
            Unsigned = "0",
            Signed = "0",
            FieldMode = false,
            Start = 0,
            Count = 1,
            FieldValue = "0",
            FieldMask = "0",
            Operand = "0",
            CopyText = "0",
            LittleEndian = string.Join(" ", Enumerable.Repeat("00", state.Width / 8)),
            BigEndian = string.Join(" ", Enumerable.Repeat("00", state.Width / 8))
        };
        // 清屏立即呈现，不等待 IPC；旧请求的结果随后由请求序号拦截。
        Update(JsonSerializer.SerializeToElement(cleared));
        var completion = SendAsync("clear");
        latestRequestId = Math.Max(latestRequestId, id);
        return completion;
    }

    private sealed record CalculatorInput(int Id, long Revision, string Action, string? Setting, string? Choice, int? Bit, string? Operation, JsonElement Values)
    {
        internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
