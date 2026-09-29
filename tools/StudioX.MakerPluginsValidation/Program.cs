using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using StudioX.Application.Plugins;
using StudioX.Extensions.Abstractions;
using StudioX.LabPlugins;
using StudioX.MakerPlugins;

if(args is not [var archives,var runtime,var gcc,var output])throw new ArgumentException("Usage: <archives> <runtime> <existing-gcc> <new-output>");
output=Path.GetFullPath(output);
if(Directory.Exists(output))throw new ArgumentException("Use a new validation directory.");
Directory.CreateDirectory(output);
var checks=new List<string>();
JsonElement Json(object v)=>JsonSerializer.SerializeToElement(v);
JsonElement Data(LabPlugin plugin,object v)=>Json(plugin.Calculate(Json(v)).Data);
void Check(bool good,string name){if(!good)throw new InvalidOperationException(name);checks.Add(name);Console.WriteLine("PASS "+name);}
void Near(double a,double b,double tolerance,string name)=>Check(Math.Abs(a-b)<=tolerance,name+$" ({a:G9} ~ {b:G9})");
void Invalid(Action action,string name){try{action();}catch(ArgumentException){Check(true,name);return;}throw new InvalidOperationException("Expected rejection: "+name);}
var rgb=new RgbStudioPlugin();var melody=new MelodyStudioPlugin();var debounce=new DebounceStudioPlugin();
var colors=Data(rgb,new{count=6,brightness=100});
Check(colors.GetProperty("rgb565").EnumerateArray().Select(x=>x.GetInt32()).SequenceEqual(new[]{0xf800,0xffe0,0x07e0,0x07ff,0x001f,0xf81f}),"RGB full-brightness six hue boundaries and RGB565");
Check(Data(rgb,new{brightness=0}).GetProperty("frames").EnumerateArray().All(a=>a.EnumerateArray().All(x=>x.GetInt32()==0)),"RGB zero brightness");
colors=Data(rgb,new{mode="gradient",start="#FF0000",end="#0000FF",count=3,brightness=100,order="grb"});
Check(colors.GetProperty("frames")[1].EnumerateArray().Select(x=>x.GetInt32()).SequenceEqual(new[]{128,0,128}),"RGB gradient includes rounded midpoint");
Check(colors.GetProperty("bytes")[0].EnumerateArray().Select(x=>x.GetInt32()).SequenceEqual(new[]{0,255,0}),"GRB ordering does not change logical colors");
Near(Data(rgb,new{mode="gradient",start="#808080",end="#808080",gamma=2,brightness=100}).GetProperty("frames")[0][0].GetInt32(),64,0,"RGB gamma follows defined component power");
Check(Data(rgb,new{mode="breathe",count=4,start="#FFFFFF",brightness=100}).GetProperty("frames").EnumerateArray().Select(x=>x[0].GetInt32()).SequenceEqual(new[]{0,128,255,128}),"breathing half-code boundaries retain symmetry");
Invalid(()=>Data(rgb,new{mode="gradient",start="#GG0000"}),"RGB malformed color rejected");
Invalid(()=>Data(rgb,new{count=2.5}),"RGB fractional frame count rejected");
var tune=Data(melody,new{preset="custom",score="A4:1 A5:1 R:0.5 F#4:1 Gb4:1",bpm=120,gate=80});
var notes=tune.GetProperty("notes");
Check(notes[0].GetProperty("Hz").GetInt32()==440&&notes[1].GetProperty("Hz").GetInt32()==880,"melody A4 and octave");
Check(notes[0].GetProperty("DurationMs").GetInt32()==500&&notes[0].GetProperty("OnMs").GetInt32()==400,"melody beat duration and gate");
Check(notes[2].GetProperty("Hz").GetInt32()==0&&notes[2].GetProperty("OnMs").GetInt32()==0,"rest never sounds");
Check(notes[3].GetProperty("Hz").GetInt32()==notes[4].GetProperty("Hz").GetInt32(),"enharmonic sharp and flat");
tune=Data(melody,new{preset="custom",score=string.Join(" ",Enumerable.Repeat("C4:0.333",48)),bpm=137});
Near(tune.GetProperty("totalMs").GetInt32(),Math.Round(48*0.333*60000/137,MidpointRounding.AwayFromZero),0,"cumulative duration avoids rounding drift");
foreach(var bad in new[]{"C:1","R4:1","H4:1","A4:0","A4:9","R#:1",""})Invalid(()=>Data(melody,new{preset="custom",score=bad}),"reject melody token "+bad);
var db=Data(debounce,new{});
Check(db.GetProperty("events").EnumerateArray().Select(x=>x.GetProperty("TimeMs").GetInt32()).SequenceEqual(new[]{40,126}),"stable debounce press/release latency");
db=Data(debounce,new{algorithm="integrator"});
Check(db.GetProperty("events").EnumerateArray().Select(x=>x.GetProperty("TimeMs").GetInt32()).SequenceEqual(new[]{39,125}),"integrator press/release counting convention");
Check(Data(debounce,new{trace="0:0 10:1 11:0",activeLow=false}).GetProperty("events").GetArrayLength()==0,"short button pulse rejected");
Check(Data(debounce,new{trace="0:1",activeLow=false}).GetProperty("events").GetArrayLength()==0,"initial held key emits no synthetic event");
Check(Data(debounce,new{sampleMs=3,debounceMs=20}).GetProperty("quantizedMs").GetInt32()==21,"debounce threshold rounds upwards");
foreach(var bad in new[]{"1:0","0:1 10:0 9:1","0:1 10:2","0:1 9999:0"})Invalid(()=>Data(debounce,new{trace=bad}),"reject malformed trace "+bad);

var simple=new PidSettings(Kp:1,Ki:0,Kd:0,Minimum:-10,Maximum:10,Tau:1,SampleMs:20,Seconds:2);
var pr=PidSimulation.Run(simple);
var expected=0.5*(1-Math.Pow(2*Math.Exp(-simple.Dt)-1,simple.Steps));
Near(pr.Samples[^1].Y,expected,1e-12,"PID sampled proportional loop matches analytic recurrence");
Check(pr.Metrics.RiseSeconds is null&&pr.Metrics.SettlingSeconds is null,"permanent proportional error not reported as settled");
var held=new PidSettings(Kp:50,Ki:0,Kd:0,Maximum:1,Target:5,Tau:1,Seconds:1);
Near(PidSimulation.Run(held).Samples[^1].Y,1-Math.Exp(-1),1e-12,"first-order zero-order-hold exact step response");
Near(PidSimulation.Run(held with{Model="second"}).Samples[^1].Y,1-2*Math.Exp(-1),1e-12,"second-order cascade exact step response");
pr=PidSimulation.Run(held with{DelayMs=100});
Near(pr.Samples[5].Y,0,0,"pure delay retains zero initial output");
Near(pr.Samples[^1].Y,1-Math.Exp(-0.9),1e-12,"pure delay offsets analytic response");
Check(PidSettings.Parse(Json(new{delayMs=31,sampleMs=20})).DelaySteps==2,"delay displayed at nearest sample");
pr=PidSimulation.Run(new());
Check(pr.Metrics.SettlingSeconds.HasValue&&Math.Abs(pr.Metrics.TailMeanError)<0.02,"default PID settles inside two-percent band");
pr=PidSimulation.Run(new(Maximum:0.5));
Check(pr.Metrics.SettlingSeconds is null&&pr.Metrics.TailMeanError>0.49,"unreachable target never claims convergence");
var noDkick=new PidController(new(Kp:0,Ki:0,Kd:1,Minimum:-10,Maximum:10));
Near(noDkick.Step(1,0).D,0,0,"D initializes without boot kick");
Near(noDkick.Step(5,0).D,0,0,"target step produces no derivative kick");
Check(noDkick.Step(5,0.1).D<0,"rising measurement yields negative derivative");
var limits=new PidSettings(Kp:0,Ki:1,Kd:0,SampleMs:100,Minimum:-10,Maximum:1);
var clamp=new PidController(limits);var free=new PidController(limits with{AntiWindup=false});
PidTerms ct=new(0,0,0,0,false),ft=ct;
for(var i=0;i<30;i++){ct=clamp.Step(1,0);ft=free.Step(1,0);}
Check(ct.I<=1.00001&&ft.I>2.99,"conditional integration prevents deep windup");
Check(clamp.Step(-1,0).I<ct.I,"reverse error unwinds integral");
var noiseSettings=new PidSettings(Kd:0.2,Noise:0.05,FilterMs:0);
var noisy=PidSimulation.Run(noiseSettings);
Check(Json(noisy).GetRawText()==Json(PidSimulation.Run(noiseSettings)).GetRawText(),"noise experiment deterministic");
var filtered=PidSimulation.Run(noiseSettings with{FilterMs=100});
double Rms(PidRun r)=>Math.Sqrt(r.Samples.Average(s=>s.D*s.D));
Check(Rms(filtered)<Rms(noisy)*0.5,"D low-pass filter reduces noise amplification");
pr=PidSimulation.Run(new(Disturbance:-0.4,DisturbanceAt:4,Seconds:12));
Check(pr.Metrics.StepWindowSeconds==4&&pr.Metrics.RecoverySeconds.HasValue,"load recovery and pre-disturbance metrics separated");
Check(pr.Samples.All(x=>double.IsFinite(x.Y)&&x.Output>=0&&x.Output<=3),"all outputs finite and within actuator range");
Invalid(()=>PidSettings.Parse(Json(new{sampleMs=1,seconds=30})),"oversized PID run rejected");
Invalid(()=>PidSettings.Parse(Json(new{kp=-1})),"negative PID gain rejected");
Invalid(()=>PidSettings.Parse(Json(new{disturbance=1,disturbanceAt=8,seconds=8})),"unobserved load step rejected");
Invalid(()=>PidSettings.Parse(Json(new{sampleMs=1.5})),"fractional sample period rejected");

// 真正编译并执行导出的 C，防止只验证 C# 沙盒而交付不一致的控制器。
async Task<string> Exec(string file,params string[] arguments)
{
    var psi=new ProcessStartInfo(file){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true,WorkingDirectory=output};
    foreach(var argument in arguments)psi.ArgumentList.Add(argument);
    using var process=Process.Start(psi)??throw new InvalidOperationException("Process failed");
    var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();var message=await stdout;var errors=await stderr;
    if(process.ExitCode!=0)throw new InvalidOperationException(file+"\n"+message+"\n"+errors);
    return message;
}
var variants=new[]{new PidSettings(),new PidSettings(Kp:3,Ki:5,Kd:0.3,FilterMs:0,Minimum:-2,Maximum:1),new PidSettings(AntiWindup:false),new PidSettings(SampleMs:1,FilterMs:200),new PidSettings(Kp:0,Ki:0,Kd:0)};
for(var n=0;n<variants.Length;n++)
{
    var settings=variants[n];
    var source=Path.Combine(output,$"pid-{n}.c");var exe=Path.Combine(output,$"pid-{n}.exe");
    var code=PidCode.Generate(settings)+"\n#include <stdio.h>\nint main(void) { StudioXPid p={0}; for(int k=0;k<400;k++) { float y=(float)(sin(k*0.04)*0.3+(k>120?1.2:0)); float r=k<100?1.0f:0.5f; float u=pid_step(&p,r,y); printf(\"%.9g %.9g %.9g\\n\",u,p.i,p.d); } return 0; }\n";
    await File.WriteAllTextAsync(source,code);
    await Exec(gcc,"-std=c99","-Wall","-Wextra","-Werror","-O2",source,"-o",exe);
    var lines=(await Exec(exe)).Split('\n',StringSplitOptions.RemoveEmptyEntries);
    var csharp=new PidController(settings);var maximumError=0d;
    for(var k=0;k<400;k++)
    {
        var y=(float)(Math.Sin(k*0.04)*0.3+(k>120?1.2:0));var r=k<100?1:0.5;
        var terms=csharp.Step(r,y);var fields=lines[k].Split(' ').Select(x=>double.Parse(x,CultureInfo.InvariantCulture)).ToArray();
        maximumError=Math.Max(maximumError,Math.Max(Math.Abs(fields[0]-terms.Output),Math.Abs(fields[1]-terms.I)));
        maximumError=Math.Max(maximumError,Math.Abs(-fields[2]*settings.Kd-terms.D));
    }
    Check(maximumError<0.0002,$"exported C variant {n}: 400 updates match P/I/D (max error {maximumError:G4})");
    Check(PidCode.Generate(settings).Length<=4096,"C controller fits host copy field "+n);
}
foreach(var algorithm in new[]{"stable","integrator"})
{
    var source=Path.Combine(output,algorithm+".c");var exe=Path.Combine(output,algorithm+".exe");
    var code=debounce.Calculate(Json(new{algorithm})).CopyText+"\n#include <stdio.h>\nint main(void) { Debounce d; db_init(&d,1); for(int t=1;t<=160;t++){ unsigned char high=t<10 || (t>=12&&t<15) || (t>=17&&t<20) || (t>=100&&t<102) || t>=106; int e=db_step(&d,high); if(e) printf(\"%d %d\\n\",t,e); } return 0;}\n";
    await File.WriteAllTextAsync(source,code);await Exec(gcc,"-std=c99","-Wall","-Wextra","-Werror","-O2",source,"-o",exe);
    var actual=(await Exec(exe)).Replace("\r","").Trim();
    Check(actual==(algorithm=="stable"?"40 1\n126 -1":"39 1\n125 -1"),algorithm+" exported C matches physical trace events");
}
foreach(var (plugin,config,main) in new (LabPlugin,object,string)[]{(rgb,new{count=32,brightness=100},"return led_frames[0][0]==255 && palette565[0]==0xf800 ? 0 : 1;"),(melody,new{preset="custom",score=string.Join(" ",Enumerable.Repeat("B8:8",48)),bpm=30},"return melody[0].duration_ms==16000 ? 0 : 1;")})
{
    var result=plugin.Calculate(Json(config));Check(result.CopyText.Length<=4096,plugin.Title+" maximum output fits copy field");
    var source=Path.Combine(output,plugin.GetType().Name+".c");var exe=Path.ChangeExtension(source,".exe");
    await File.WriteAllTextAsync(source,result.CopyText+"\nint main(void){"+main+"}\n");
    await Exec(gcc,"-std=c99","-Wall","-Wextra","-Werror",source,"-o",exe);await Exec(exe);Check(true,plugin.Title+" C export compiles and runs");
}

var fixture=Path.Combine(output,"fixture");Directory.CreateDirectory(fixture);
await using var manager=new PluginManagerService(runtime,Path.Combine(output,"user-data"));
var installed=new List<string>();
foreach(var archive in Directory.GetFiles(archives,"*.studioxplugin"))
{
    var entry=await manager.ImportAsync(archive);
    Check(!entry.Enabled&&entry.CanEnable&&entry.Manifest?.HostTools?.Length==0,entry.Id+" imports with validated content, no host privileges");
    Check(entry.Manifest!.Activity?.Icon?.Strokes.Length>0,entry.Id+" owns its activity icon");
    await manager.SetEnabledAsync(entry.Id,true);installed.Add(entry.Id);
}
await using(var workspace=await manager.OpenWorkspaceAsync(fixture,(_,_,_,_)=>throw new InvalidOperationException("Unexpected host tool")))
{
    Check(installed.Count==4&&workspace.Contributions.Count==4,"four independent archives activate in existing isolated host");
    foreach(var id in installed)
    {
        var result=await workspace.InvokeAsync(id,"agentTool",id=="studiox.pid-studio"?"simulate":"calculate",Json(new{}));
        Check(result.GetProperty("ok").GetBoolean()&&result.GetProperty("copyText").GetString()!.Length<=4096,id+" Agent calculation succeeds");
    }
    const string pid="studiox.pid-studio";
    PluginPanelDefinition Panel()=>workspace.LatestPanels[pid+"/lab"];
    IEnumerable<PluginPanelWidget> All(PluginPanelWidget[] widgets)=>widgets.SelectMany(w=>new[]{w}.Concat(All(w.Children??[])));
    string Key(string field)=>All(Panel().Widgets).Single(w=>w.Id.EndsWith("_"+field,StringComparison.Ordinal)).Id;
    async Task Wait(Func<bool> ready)
    {using var timeout=new CancellationTokenSource(3000);while(!ready())await Task.Delay(10,timeout.Token);}
    async Task<JsonElement> Command(string command,Dictionary<string,object> values)
    {
        var before=Panel();
        var reply=await workspace.InvokeAsync(pid,"command",command,Json(new{values}));
        await Wait(()=>
        {
            if(!workspace.IsPluginRunning(pid))throw new InvalidOperationException(string.Join("\n",workspace.Diagnostics));
            return !ReferenceEquals(before,Panel());
        });return reply;
    }
    var changed=await Command("show",new(){[Key("kp")]=3,[Key("page")]="plant"});
    Check(changed.GetProperty("ok").GetBoolean()&&Panel().Widgets.Any(x=>x.Id=="dirty"),"PID page switch persists edited gains and marks stale results");
    await Command("show",new(){[Key("page")]="tuning"});
    Check(All(Panel().Widgets).Single(w=>w.Id==Key("kp")).Value!.Value.GetDouble()==3,"PID values survive round-trip page navigation");
    var bad=await Command("simulate",new(){[Key("kp")]=-1});
    Check(!bad.GetProperty("ok").GetBoolean()&&Panel().Widgets.Any(x=>x.Id=="error"),"PID invalid form preserves running plugin and previous results");
    await Command("simulate",new(){[Key("kp")]=3});
    Check(Panel().Widgets.Any(x=>x.Id=="metrics")&&!Panel().Widgets.Any(x=>x.Id=="dirty"),"PID simulate refreshes metrics and clears stale marker");
    await Command("baseline",new());
    Check(Panel().Widgets.Any(x=>x.Id=="compare"),"PID completed result can become baseline");
    await Command("show",new(){[Key("page")]="export"});
    var previous=All(Panel().Widgets).Single(x=>x.Id.StartsWith("code_",StringComparison.Ordinal)).Value!.Value.GetString();
    await workspace.InvokeAsync(pid,"agentTool","simulate",Json(new{kp=9}));
    Check(previous==All(Panel().Widgets).Single(x=>x.Id.StartsWith("code_",StringComparison.Ordinal)).Value!.Value.GetString(),"Agent independent run does not overwrite panel experiment");
    var longest=await workspace.InvokeAsync(pid,"agentTool","simulate",Json(new{sampleMs=1,seconds=5,noise=0.5}));
    Check(longest.GetProperty("data").GetProperty("samples").GetArrayLength()==1000&&longest.GetProperty("data").GetProperty("totalSamples").GetInt32()==5001,"maximum PID history uses bounded host-compatible pages");
    var nextOffset=longest.GetProperty("data").GetProperty("nextOffset").GetInt32();
    var paged=await workspace.InvokeAsync(pid,"agentTool","simulate",Json(new{sampleMs=1,seconds=5,noise=0.5,offset=nextOffset}));
    Near(paged.GetProperty("data").GetProperty("samples")[0].GetProperty("Time").GetDouble(),1,1e-12,"PID second page preserves time ordering");
    var lastPage=await workspace.InvokeAsync(pid,"agentTool","simulate",Json(new{sampleMs=1,seconds=5,noise=0.5,offset=5000}));
    Check(lastPage.GetProperty("data").GetProperty("samples").GetArrayLength()==1&&lastPage.GetProperty("data").GetProperty("nextOffset").ValueKind==JsonValueKind.Null,"PID final sample is retained and pagination terminates");
    Check(All(Panel().Widgets).Where(x=>x.Kind=="input").All(x=>x.Value!.Value.GetString()!.Length<=4096),"PID C and CSV exports fit host input limits");
    await Command("show",new(){[Key("page")]="tuning"});
    foreach(var preset in new[]{"balanced","oscillation","windup","load","sensor"})
    {
        await Command("preset",new(){[Key("preset")]=preset});
        Check(!Panel().Widgets.Any(x=>x.Id=="error")&&All(Panel().Widgets).Where(x=>x.Kind=="plot").All(x=>x.Value!.Value.GetArrayLength()<=500),preset+" preset produces bounded plots");
        await Command("show",new(){[Key("page")]="tuning"});
    }
    await manager.SetEnabledAsync(pid,false);Check(!workspace.IsPluginRunning(pid),"disable PID terminates its isolated host");
}
await File.WriteAllTextAsync(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new{status="passed",checks,hardwareConnected=false,coreRebuilt=false},new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine($"PASS {checks.Count} checks; generated C compiled/executed; real plugin hosts; no hardware.");
