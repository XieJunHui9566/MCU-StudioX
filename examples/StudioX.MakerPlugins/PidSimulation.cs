namespace StudioX.MakerPlugins;

public static class PidSimulation
{
    public static PidRun Run(PidSettings settings)
    {
        settings.Validate();
        var controller = new PidController(settings);
        var samples = new List<PidSample>(settings.Steps + 1);
        var commands = new double[settings.Steps];
        double state1 = 0, y = 0, iae = 0, ise = 0;
        var saturated = 0;
        uint noiseState = 0x12345678;
        var a = Math.Exp(-settings.Dt / settings.Tau);
        PidTerms last = new(0, 0, 0, 0, false);
        for (var k = 0; k < settings.Steps; k++)
        {
            // 固定种子便于比较两次调参；噪声仅进入传感器，指标使用无噪声的对象输出。
            noiseState ^= noiseState << 13;
            noiseState ^= noiseState >> 17;
            noiseState ^= noiseState << 5;
            var measured = y + settings.Noise * (2d * noiseState / uint.MaxValue - 1);
            var terms = controller.Step(settings.Target, measured);
            samples.Add(new(k * settings.Dt, y, measured, terms.P, terms.I, terms.D, terms.Output));
            commands[k] = terms.Output;
            if (terms.Saturated)
            {
                saturated++;
            }
            var delayed = k >= settings.DelaySteps ? commands[k - settings.DelaySteps] : 0;
            var load = k >= settings.DisturbanceStep ? settings.Disturbance : 0;
            var drive = settings.Gain * (delayed + load);
            var before = y;
            // 零阶保持下的解析递推；二阶为两个同时间常数环节串联，避免 Euler 误差冒充振荡。
            if (settings.Model == "second")
            {
                y = drive + (y - drive + (state1 - drive) * settings.Dt / settings.Tau) * a;
                state1 = drive + (state1 - drive) * a;
            }
            else
            {
                y = drive + (y - drive) * a;
            }
            if (!double.IsFinite(y) || !double.IsFinite(terms.I))
            {
                throw new ArgumentException("仿真数值超出范围，请降低参数。");
            }
            iae += (Math.Abs(settings.Target - before) + Math.Abs(settings.Target - y)) * settings.Dt / 2;
            ise += (Math.Pow(settings.Target - before, 2) + Math.Pow(settings.Target - y, 2)) * settings.Dt / 2;
            last = terms;
        }
        samples.Add(new(settings.Steps * settings.Dt, y, y, last.P, last.I, last.D, last.Output));
        var array = samples.ToArray();
        // 扰动开始后不再属于原阶跃试验；将阶跃指标限制在扰动前，单列扰动后的恢复时间。
        var stepEnd = settings.Disturbance == 0 ? settings.Steps : settings.DisturbanceStep;
        var step = array.Take(stepEnd + 1).ToArray();
        var rise10 = Crossing(step, settings.Target * 0.1);
        var rise90 = Crossing(step, settings.Target * 0.9);
        var settling = Settling(step, settings.Target, settings.Dt);
        var afterLoad = settings.Disturbance == 0 ? [] : array.Skip(settings.DisturbanceStep).ToArray();
        var recovered = afterLoad.Length == 0 ? null : Settling(afterLoad, settings.Target, settings.Dt);
        var endTail = array.TakeLast(Math.Max(1, (int)Math.Ceiling(settings.Steps * 0.1))).ToArray();
        var metrics = new PidMetrics(
            Math.Max(0, (step.Max(s => s.Y) - settings.Target) / settings.Target * 100),
            rise10.HasValue && rise90.HasValue ? rise90.Value - rise10.Value : null,
            settling, settings.Target - endTail.Average(s => s.Y), iae, ise, 100d * saturated / settings.Steps,
            recovered.HasValue ? recovered.Value - settings.DisturbanceStep * settings.Dt : null,
            stepEnd * settings.Dt, settings.Steps * settings.Dt);
        return new(settings, array, metrics);
    }

    private static double? Crossing(PidSample[] samples, double threshold)
    {
        for (var i = 1; i < samples.Length; i++)
        {
            if (samples[i - 1].Y < threshold && samples[i].Y >= threshold)
            {
                return samples[i - 1].Time + (samples[i].Time - samples[i - 1].Time) * (threshold - samples[i - 1].Y) / (samples[i].Y - samples[i - 1].Y);
            }
        }
        return null;
    }

    private static double? Settling(PidSample[] samples, double target, double dt)
    {
        var lastOutside = Array.FindLastIndex(samples, s => Math.Abs(s.Y - target) > Math.Abs(target) * 0.02);
        var candidate = lastOutside + 1;
        // 最后一个点碰巧落入误差带不能宣称稳定：尾部至少观察 max(0.5s, 5 个采样周期)。
        if (candidate >= samples.Length || samples[^1].Time - samples[candidate].Time + 1e-10 < Math.Max(0.5, 5 * dt))
        {
            return null;
        }
        return samples[candidate].Time;
    }
}
