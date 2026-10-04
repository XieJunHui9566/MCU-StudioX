namespace StudioX.MakerPlugins;

using System.Text.Json;
using StudioX.LabPlugins;
using static StudioX.LabPlugins.LabPanel;

public sealed record PidSettings(
    double Kp = 2, double Ki = 2, double Kd = 0.05, double FilterMs = 30,
    bool AntiWindup = true, double Minimum = 0, double Maximum = 3,
    int SampleMs = 20, double Seconds = 8, double Target = 1,
    string Model = "first", double Gain = 1, double Tau = 0.8, double DelayMs = 0,
    double Disturbance = 0, double DisturbanceAt = 4, double Noise = 0)
{
    public static JsonElement Schema => LabPanel.Schema(new()
    {
        ["kp"] = NumberSchema(0, 50),
        ["ki"] = NumberSchema(0, 50),
        ["kd"] = NumberSchema(0, 10),
        ["filterMs"] = NumberSchema(0, 1000),
        ["antiWindup"] = new
        {
            type = "boolean"
        },
        ["minimum"] = NumberSchema(-10, 0),
        ["maximum"] = NumberSchema(0.01, 10),
        ["sampleMs"] = NumberSchema(1, 200, true),
        ["seconds"] = NumberSchema(1, 30),
        ["target"] = NumberSchema(0.1, 5),
        ["model"] = EnumSchema("first", "second"),
        ["gain"] = NumberSchema(0.1, 5),
        ["tau"] = NumberSchema(0.02, 10),
        ["delayMs"] = NumberSchema(0, 2000),
        ["disturbance"] = NumberSchema(-5, 5),
        ["disturbanceAt"] = NumberSchema(0, 30),
        ["noise"] = NumberSchema(0, 0.5)
    });

    public static PidSettings Parse(JsonElement v)
    {
        if (v.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("PID 参数必须是对象。");
        }
        var s = new PidSettings(
            LabInput.Number(v, "kp", 2, 0, 50), LabInput.Number(v, "ki", 2, 0, 50), LabInput.Number(v, "kd", 0.05, 0, 10),
            LabInput.Number(v, "filterMs", 30, 0, 1000), LabInput.Boolean(v, "antiWindup", true),
            LabInput.Number(v, "minimum", 0, -10, 0), LabInput.Number(v, "maximum", 3, 0.01, 10),
            LabInput.Integer(v, "sampleMs", 20, 1, 200), LabInput.Number(v, "seconds", 8, 1, 30), LabInput.Number(v, "target", 1, 0.1, 5),
            LabInput.Text(v, "model", "first"), LabInput.Number(v, "gain", 1, 0.1, 5), LabInput.Number(v, "tau", 0.8, 0.02, 10),
            LabInput.Number(v, "delayMs", 0, 0, 2000), LabInput.Number(v, "disturbance", 0, -5, 5),
            LabInput.Number(v, "disturbanceAt", 4, 0, 30), LabInput.Number(v, "noise", 0, 0, 0.5));
        s.Validate();
        return s;
    }

    public void Validate()
    {
        if (Model is not ("first" or "second"))
        {
            throw new ArgumentException("对象须为一阶或二阶串联模型。");
        }
        if (Steps is < 5 or > 5000)
        {
            throw new ArgumentException("本轮需要 5–5000 个采样步，请增大采样周期或缩短仿真时间。");
        }
        if (Disturbance != 0 && DisturbanceStep >= Steps)
        {
            throw new ArgumentException("扰动开始时间必须早于仿真结束（按采样网格向上取整）。");
        }
    }

    public double Dt => SampleMs / 1000d;
    public int Steps => (int)Math.Round(Seconds / Dt, MidpointRounding.AwayFromZero);
    public int DelaySteps => (int)Math.Round(DelayMs / SampleMs, MidpointRounding.AwayFromZero);
    public int DisturbanceStep => (int)Math.Ceiling(DisturbanceAt / Dt - 1e-10);
    public JsonElement Values => Json(new
    {
        kp = Kp,
        ki = Ki,
        kd = Kd,
        filterMs = FilterMs,
        antiWindup = AntiWindup,
        minimum = Minimum,
        maximum = Maximum,
        sampleMs = SampleMs,
        seconds = Seconds,
        target = Target,
        model = Model,
        gain = Gain,
        tau = Tau,
        delayMs = DelayMs,
        disturbance = Disturbance,
        disturbanceAt = DisturbanceAt,
        noise = Noise
    });
}
