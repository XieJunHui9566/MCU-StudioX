namespace StudioX.MakerPlugins;

using System.Globalization;
using System.Text.Json;
using StudioX.Extensions.Abstractions;
using StudioX.LabPlugins;
using static StudioX.LabPlugins.LabPanel;

/// <summary>PID 具有独立分页状态，编辑参数和已完成试验明确分离。</summary>
public sealed class PidStudioPlugin : IStudioXPlugin
{
    private IPluginHost? host;
    private PidSettings settings = new();
    private PidRun run = PidSimulation.Run(new());
    private PidRun? baseline;
    private string page = "tuning";
    private string chart = "tracking";
    private long inputGeneration;
    private long outputGeneration;
    private static readonly string[] Pages = ["tuning","plant","limits","response","export"];
    private static readonly HashSet<string> Fields = PidSettings.Schema.GetProperty("properties").EnumerateObject().Select(p=>p.Name).ToHashSet(StringComparer.Ordinal);
    private string Id(string field) => $"v{inputGeneration}_{field}";
    private static JsonElement AgentSchema
    {
        get
        {
            var properties=PidSettings.Schema.GetProperty("properties").EnumerateObject().ToDictionary(x=>x.Name,x=>(object)x.Value.Clone());
            properties["offset"]=NumberSchema(0,5000,true);
            properties["limit"]=NumberSchema(1,1000,true);
            return LabPanel.Schema(properties);
        }
    }

    public PluginContribution Describe() => new(
        [new("open","打开 PID 调参沙盒","tools"),new("simulate","运行仿真","palette"),
         new("show","保存参数并切换页面","palette"),new("preset","载入实验预设","palette"),new("baseline","记为对比基线","palette")],
        [Panel()], [new("simulate","离线 PID 仿真，返回指标、C 控制器和采样分页。offset 默认 0，limit 默认 1000；按 nextOffset 用相同参数继续可取全部采样。不会连接设备或修改工程。积分使用秒，正目标阶跃。",AgentSchema)]);

    public async Task ActivateAsync(IPluginHost active,CancellationToken token)
    {
        host=active;
        await active.PublishPanelAsync(Panel(),token);
    }
    public Task DeactivateAsync(CancellationToken token) {host=null;return Task.CompletedTask;}

    public async Task<JsonElement> InvokeAsync(string kind,string id,JsonElement arguments,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var active=host??throw new InvalidOperationException("插件未激活。");
        if (kind is not ("command" or "agentTool") || (kind=="agentTool" && id!="simulate")) throw new ArgumentException("未知 PID 调用。");
        try
        {
            if (kind=="agentTool")
            {
                var computed=PidSimulation.Run(PidSettings.Parse(arguments));
                var offset=LabInput.Integer(arguments,"offset",0,0,5000);
                var limit=LabInput.Integer(arguments,"limit",1000,1,1000);
                if(offset>=computed.Samples.Length)throw new ArgumentException("offset 已超出本次试验采样数量。");
                var samples=computed.Samples.Skip(offset).Take(limit).ToArray();
                int? next=offset+samples.Length<computed.Samples.Length?offset+samples.Length:null;
                // Agent 计算返回自己的结果，不替换用户尚未保存的面板试验。
                return Json(new{ok=true,data=new{settings=computed.Settings.Values,metrics=computed.Metrics,samples,totalSamples=computed.Samples.Length,offset,nextOffset=next},copyText=PidCode.Generate(computed.Settings)});
            }
            if (id=="open") {await active.PublishPanelAsync(Panel(),token);return Json(new{ok=true});}
            if (arguments.ValueKind!=JsonValueKind.Object) throw new ArgumentException("表单参数必须是对象。");
            var values=arguments.TryGetProperty("values",out var form)?form:Json(new{});
            if(values.ValueKind!=JsonValueKind.Object)throw new ArgumentException("表单参数必须是对象。");
            if (id=="preset")
            {
                var selected=LabInput.Text(values,Id("preset"),"balanced");
                var next=selected switch
                {
                    "balanced"=>new PidSettings(),
                    "oscillation"=>new PidSettings(Kp:8,Ki:6,Kd:0,Model:"second",DelayMs:100,Seconds:12),
                    "windup"=>new PidSettings(Kp:2,Ki:4,Kd:0,AntiWindup:false,Maximum:1.2,Model:"second",Seconds:15),
                    "load"=>new PidSettings(Disturbance:-0.4,DisturbanceAt:4,Seconds:12),
                    "sensor"=>new PidSettings(Kd:0.2,FilterMs:0,Noise:0.05),
                    _=>throw new ArgumentException("未知预设。")
                };
                settings=next; run=PidSimulation.Run(settings); page="response"; inputGeneration++;
            }
            else
            {
                var merged=settings.Values.EnumerateObject().ToDictionary(p=>p.Name,p=>p.Value.Clone());
                foreach(var field in Fields)
                    if(values.TryGetProperty(Id(field),out var value)) merged[field]=value.Clone();
                var next=PidSettings.Parse(Json(merged));
                var nextPage=LabInput.Text(values,Id("page"),page);
                var nextChart=LabInput.Text(values,Id("chart"),chart);
                if(!Pages.Contains(nextPage) || nextChart is not ("tracking" or "terms" or "sensor" or "compare"))throw new ArgumentException("未知页面或曲线。");
                switch(id)
                {
                    case "show": settings=next;page=nextPage;chart=nextChart;break;
                    case "simulate": run=PidSimulation.Run(next);settings=next;page="response";chart=nextChart;break;
                    case "baseline":
                        if(next!=run.Settings)throw new ArgumentException("参数已修改，请先仿真，再保存结果作为基线。");
                        baseline=run;page="response";chart="compare";break;
                    default:throw new ArgumentException("未知 PID 命令。");
                }
            }
            // 页面选择必须反映程序切换；新一代字段也让载入预设真实更新所有参数。
            inputGeneration++;
            await active.PublishPanelAsync(Panel(),token);
            return Json(new{ok=true,page,data=run.Metrics,copyText=PidCode.Generate(run.Settings)});
        }
        catch(ArgumentException error) when(kind=="command")
        {
            await active.PublishPanelAsync(Panel(error.Message),token);
            return Json(new{ok=false,error=error.Message});
        }
    }

    private PluginPanelDefinition Panel(string? error=null)
    {
        var widgets=new List<PluginPanelWidget>
        {
            Text("intro","","离线闭环实验：调参数 → 运行仿真 → 查看指标 → 复制 C 控制器。初始输出为 0，t=0 加入正目标阶跃；模型不代表实测器件。"),
            new("navigation","form","页面",Children:
            [Select(Id("page"),"选择页面",page,("tuning","1 · PID 参数"),("plant","2 · 对象与采样"),("limits","3 · 限幅与扰动"),("response","4 · 曲线与指标"),("export","5 · C 代码与数据")),
             new("show","button","保存参数并切换页面 / 刷新曲线",CommandId:"show")])
        };
        if(error is not null)widgets.Add(Text("error","输入错误 / Error",error+" 已完成的试验保留。"));
        if(settings!=run.Settings)widgets.Add(Text("dirty","待重新仿真","参数已保存；曲线、指标和导出仍对应上次已完成的试验。点击运行仿真更新。"));
        if(page=="tuning")
        {
            widgets.AddRange([
                Text("equation","算法","u=Kp·e + I − Kd·滤波后的测量变化率；I 每步增加 Ki·e·Ts。Ts 以秒为单位；微分不作用于设定值，避免目标跳变引起微分冲击。"),
                Number(Id("kp"),"Kp 比例（0–50）",settings.Kp),Number(Id("ki"),"Ki 积分 / s⁻¹（0–50）",settings.Ki),
                Number(Id("kd"),"Kd 微分 / s（0–10）",settings.Kd),Number(Id("filterMs"),"微分滤波时间常数 / ms（0 关闭滤波）",settings.FilterMs),
                Check(Id("antiWindup"),"抗积分饱和：饱和时禁止积分继续推向限幅方向",settings.AntiWindup),
                Text("tuninghelp","实验顺序","先令 Ki、Kd 为 0 调 Kp；增加 Ki 观察残余误差；需要阻尼时尝试 Kd，并结合采样周期、噪声与滤波观察变化。"),
                Select(Id("preset"),"载入预设会替换当前参数并重新仿真","balanced",("balanced","平稳 PI/PID 起点"),("oscillation","二阶 + 延迟：观察振荡"),("windup","对比实验：积分饱和"),("load","负载扰动恢复"),("sensor","测量噪声与微分滤波")),
                new("preset","button","载入所选预设",CommandId:"preset")]);
        }
        else if(page=="plant")
            widgets.AddRange([
                Select(Id("model"),"对象传递函数（K 为增益，τ 单位秒）",settings.Model,("first","一阶 K/(τs+1)"),("second","二阶串联 K/(τs+1)²")),
                Number(Id("gain"),"对象增益 K",settings.Gain),Number(Id("tau"),"时间常数 τ / s（每级）",settings.Tau),
                Number(Id("delayMs"),"执行器纯延迟 / ms（量化至最近采样周期）",settings.DelayMs),
                Number(Id("target"),"目标阶跃幅度（初始值 0）",settings.Target),Number(Id("sampleMs"),"采样周期 Ts / ms（1–200 的整数）",settings.SampleMs),
                Number(Id("seconds"),"仿真时间 / s（最多 5000 步）",settings.Seconds),
                Text("modelhelp","数值模型","输入每个采样周期保持不变，对象使用解析递推。二阶为两个相同 τ 的惯性环节；不包含摩擦、死区或器件非线性。")]);
        else if(page=="limits")
            widgets.AddRange([
                Number(Id("minimum"),"执行器下限（−10–0）",settings.Minimum),Number(Id("maximum"),"执行器上限（0.01–10）",settings.Maximum),
                Number(Id("disturbance"),"负载阶跃幅度（执行器单位，0 关闭）",settings.Disturbance),Number(Id("disturbanceAt"),"负载阶跃开始 / s",settings.DisturbanceAt),
                Number(Id("noise"),"传感器均匀噪声 ±幅度（0 关闭）",settings.Noise),
                Text("loadhelp","作用位置","负载加在执行器延迟之后、对象增益之前。噪声只影响测量反馈，固定种子以便比较；指标按对象真实输出计算。")]);
        else if(page=="response")AddResponse(widgets);
        else AddExport(widgets);
        widgets.Add(new("simulate","button","运行仿真并查看结果",CommandId:"simulate"));
        return new("lab","PID 调参沙盒",widgets.ToArray());
    }

    private void AddResponse(List<PluginPanelWidget> widgets)
    {
        var s=run.Settings;var m=run.Metrics;
        widgets.Add(Text("summary","本次试验",$"Kp={F(s.Kp)}，Ki={F(s.Ki)}，Kd={F(s.Kd)}；目标={F(s.Target)}，Ts={s.SampleMs}ms，{s.Model}，实际延迟={s.DelaySteps*s.SampleMs}ms，时长={F(m.SimulatedSeconds)}s。"));
        widgets.Add(Select(Id("chart"),"选择曲线后点页面刷新",chart,("tracking","响应 / 误差 / 控制量"),("terms","P / I / D 分量"),("sensor","真实输出 / 测量值"),("compare","当前与基线（分别显示）")));
        if(chart=="terms")
        {
            Plot(widgets,"p","P 比例分量",run,x=>x.P);Plot(widgets,"i","I 积分分量",run,x=>x.I);Plot(widgets,"d","D 微分分量",run,x=>x.D);
        }
        else if(chart=="sensor")
        {
            Plot(widgets,"actual","真实输出 y",run,x=>x.Y);Plot(widgets,"measured","传感器测量",run,x=>x.Measured);
        }
        else if(chart=="compare")
        {
            Plot(widgets,"current","当前输出 y",run,x=>x.Y);
            if(baseline is not null)
            {
                Plot(widgets,"baselinecurve","基线输出 y（独立自动纵轴；数值比较见表）",baseline,x=>x.Y);
                widgets.Add(Text("baselineinfo","基线参数",$"Kp={F(baseline.Settings.Kp)}，Ki={F(baseline.Settings.Ki)}，Kd={F(baseline.Settings.Kd)}；Ts={baseline.Settings.SampleMs}ms；目标={F(baseline.Settings.Target)}。"));
                if(s with { Kp=0,Ki=0,Kd=0,FilterMs=0,AntiWindup=false } != baseline.Settings with { Kp=0,Ki=0,Kd=0,FilterMs=0,AntiWindup=false })
                    widgets.Add(Text("comparisonhint","比较范围","对象、采样、限幅或试验条件不同；本表保留数值，不能把差异只归因于 PID 参数。"));
                widgets.Add(Table("compare","当前 / 基线",["指标","当前","基线"],MetricRows(m).Zip(MetricRows(baseline.Metrics),(a,b)=>new[]{a[0],a[1],b[1]})));
            }
            else widgets.Add(Text("nobaseline","","尚无基线。先运行一个试验，再点击下方“记为对比基线”。"));
        }
        else
        {
            Plot(widgets,"y","响应 y；目标 = "+F(s.Target),run,x=>x.Y);
            Plot(widgets,"errorplot","误差 e = 目标 − y；收敛目标为 0",run,x=>s.Target-x.Y);
            Plot(widgets,"u","控制量 u；限幅 ["+F(s.Minimum)+", "+F(s.Maximum)+"]",run,x=>x.Output);
        }
        widgets.Add(Table("metrics","试验指标",["指标","结果"],MetricRows(m)));
        widgets.Add(Text("definitions","指标口径",$"上升时间 10%→90%；稳定带 ±2% 目标值，尾部至少观察 max(0.5s,5Ts)。阶跃指标窗口为 0–{F(m.StepWindowSeconds)}s（扰动前）；末段误差取完整试验最后 10% 均值。未达到或观察不足显示“未确认”。"));
        widgets.Add(Text("plotnote","曲线采样","长试验按时间桶保留局部极值，最多 500 点；指标使用全部采样。末点控制量沿用最后一次输出。"));
        var load=s.Disturbance==0?0:s.Disturbance;
        if(s.Target>s.Gain*(s.Maximum+load)||s.Target<s.Gain*(s.Minimum+load))widgets.Add(Text("unreachable","目标不可达 / Warning","当前限幅、增益及最终负载下无法达到目标，增加 Ki 也无法消除最终误差。"));
        if(s.Dt>s.Tau/10)widgets.Add(Text("coarse","采样偏粗 / Notice","Ts 大于 τ/10，调参时建议缩短采样周期并比较结果；对象解析计算不等于控制采样足够快。"));
        widgets.Add(new("savebaseline","button","将本次已完成试验记为对比基线",CommandId:"baseline"));
    }

    private void AddExport(List<PluginPanelWidget> widgets)
    {
        var id=++outputGeneration;
        widgets.Add(Text("exporthelp","C99 控制器","参数来自上次完成的试验；固定周期调用，先用 {0} 初始化状态，按实际量纲接入测量和执行器。只导出 PID，不包含仿真对象或设备驱动。"));
        widgets.Add(Input("code_"+id,"复制 C：点入后 Ctrl+A / Ctrl+C",PidCode.Generate(run.Settings)));
        // 宿主单文本框上限 4096；显式导出均匀选择的 40 行，完整采样可从 Agent 工具结果获得。
        var rows=Enumerable.Range(0,Math.Min(40,run.Samples.Length)).Select(i=>run.Samples[(int)Math.Round(i*(run.Samples.Length-1d)/(Math.Min(40,run.Samples.Length)-1))]);
        var csv="time_s,target,y,u,p,i,d\n"+string.Join("\n",rows.Select(x=>string.Join(",",new[]{x.Time,run.Settings.Target,x.Y,x.Output,x.P,x.I,x.D}.Select(F))));
        widgets.Add(Input("csv_"+id,"CSV 摘要（最多 40 行；Agent simulate 可分页读取完整数据）",csv));
        widgets.Add(Text("methods","算法资料","并联 PID、条件积分、后向欧拉微分滤波；指标定义与实现细节见插件 README。"));
    }

    private static IEnumerable<string[]> MetricRows(PidMetrics m) =>
    [
        new[]{"超调 / %",F(m.OvershootPercent)},new[]{"上升时间 / s",Optional(m.RiseSeconds)},
        new[]{"±2% 稳定时间 / s",Optional(m.SettlingSeconds)},new[]{"末 10% 均值误差",F(m.TailMeanError)},
        new[]{"全程 IAE / 值·s",F(m.Iae)},new[]{"全程 ISE / 值²·s",F(m.Ise)},new[]{"限幅占比 / %",F(m.SaturatedPercent)},
        new[]{"扰动后恢复时间 / s",Optional(m.RecoverySeconds)}
    ];
    private static string F(double v)=>v.ToString("G6",CultureInfo.InvariantCulture);
    private static string Optional(double? v)=>v.HasValue?F(v.Value):"未确认 / 不适用";
    private static void Plot(List<PluginPanelWidget> widgets,string id,string title,PidRun data,Func<PidSample,double> value)
    {
        var selected=new SortedSet<int>{0,data.Samples.Length-1};
        var width=(int)Math.Ceiling(data.Samples.Length/248d);
        for(var start=0;start<data.Samples.Length;start+=width)
        {
            var indexes=Enumerable.Range(start,Math.Min(width,data.Samples.Length-start));
            selected.Add(indexes.MinBy(i=>value(data.Samples[i])));
            selected.Add(indexes.MaxBy(i=>value(data.Samples[i])));
        }
        widgets.Add(new(id,"plot",title+$"；X=0–{F(data.Metrics.SimulatedSeconds)}s，Y范围 [{F(data.Samples.Min(value))}, {F(data.Samples.Max(value))}]",Json(selected.Select(i=>new{x=data.Samples[i].Time,y=value(data.Samples[i])}))));
    }
}
