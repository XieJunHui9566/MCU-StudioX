namespace StudioX.MakerPlugins;

/// <summary>位置式并联 PID；微分作用于测量，采样周期单位为秒。</summary>
public sealed class PidController(PidSettings settings)
{
    private double integral;
    private double derivative;
    private double previous;
    private bool initialized;

    public PidTerms Step(double target, double measurement)
    {
        if (!double.IsFinite(target) || !double.IsFinite(measurement))
        {
            throw new ArgumentException("PID 输入必须是有限数值。");
        }
        if (!initialized)
        {
            previous = measurement;
            initialized = true;
        }
        var error = target - measurement;
        var rawDerivative = (measurement - previous) / settings.Dt;
        derivative += settings.Dt / (settings.FilterMs / 1000 + settings.Dt) * (rawDerivative - derivative);
        previous = measurement;
        var p = settings.Kp * error;
        var d = -settings.Kd * derivative;
        var candidate = integral + settings.Ki * settings.Dt * error;
        var proposed = p + candidate + d;
        // 条件积分：只阻止把输出推向更深饱和的积分，允许反向误差使积分退饱和。
        if (!settings.AntiWindup || !((proposed > settings.Maximum && error > 0) || (proposed < settings.Minimum && error < 0)))
        {
            integral = candidate;
        }
        var raw = p + integral + d;
        var output = Math.Clamp(raw, settings.Minimum, settings.Maximum);
        return new(p, integral, d, output, raw != output);
    }
}
