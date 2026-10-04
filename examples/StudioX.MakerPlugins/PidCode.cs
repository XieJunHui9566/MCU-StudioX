namespace StudioX.MakerPlugins;

using System.Globalization;

public static class PidCode
{
    public static string Generate(PidSettings s)
    {
        static string F(double value)
        {
            var text = value.ToString("G9", CultureInfo.InvariantCulture);
            return (text.Contains('.') || text.Contains('E') ? text : text + ".0") + "f";
        }
        return $$"""
            #include <math.h>
            /* Parallel PID, derivative on measurement, backward-Euler D filter.
               Call every {{s.SampleMs}} ms. Gains use seconds, NOT milliseconds.
               Return value is in actuator units [{{F(s.Minimum)}}, {{F(s.Maximum)}}].
               Map it to your PWM/DAC driver; this file does not access hardware. */
            typedef struct { float i, d, previous; int ready; } StudioXPid;
            static void pid_reset(StudioXPid *p, float measurement)
            {
                p->i = 0.0f; p->d = 0.0f;
                p->previous = measurement; p->ready = 1;
            }
            static float pid_step(StudioXPid *p, float target, float measured)
            {
                const float dt={{F(s.Dt)}}, kp={{F(s.Kp)}}, ki={{F(s.Ki)}}, kd={{F(s.Kd)}};
                const float tf={{F(s.FilterMs / 1000)}}, lo={{F(s.Minimum)}}, hi={{F(s.Maximum)}};
                float error, proportional, derivative, candidate, proposed, raw;
                if (!isfinite(target) || !isfinite(measured)) return 0.0f;
                if (!p->ready) pid_reset(p, measured);
                error = target - measured;
                p->d += dt / (tf + dt) * ((measured - p->previous) / dt - p->d);
                p->previous = measured;
                proportional = kp * error;
                derivative = -kd * p->d;
                candidate = p->i + ki * dt * error;
                proposed = proportional + candidate + derivative;
                if (!{{(s.AntiWindup ? "1" : "0")}} || !((proposed > hi && error > 0.0f) ||
                            (proposed < lo && error < 0.0f))) p->i = candidate;
                raw = proportional + p->i + derivative;
                return raw > hi ? hi : (raw < lo ? lo : raw);
            }
            /* Example ownership: one controller instance per loop, initialized as
               StudioXPid controller = {0};
               output = pid_step(&controller, target, measurement);
               Reset on a deliberate restart. Serialize access to its state.
               Offline model parameters do not prove these gains suit your device. */
            """;
    }
}
