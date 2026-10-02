using System.Text.Json;
using StudioX.Application.Plugins;
using StudioX.Extensions;
using StudioX.Foundation;
using StudioX.LabPlugins;

if (args is not [var archives, var runtime, var output]) throw new ArgumentException("Usage: <archives> <runtime> <new-output>");
output = Path.GetFullPath(output);
if (Directory.Exists(output)) throw new ArgumentException("Use a new validation directory.");
Directory.CreateDirectory(output);
var checks = new List<string>();
JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
JsonElement Data(LabPlugin plugin, object value) => Json(plugin.Calculate(Json(value)).Data);
void Check(bool passed, string name)
{
    if (!passed) throw new InvalidOperationException(name);
    checks.Add(name);
    Console.WriteLine("PASS " + name);
}
void Invalid(Action action, string name)
{
    try { action(); } catch (ArgumentException) { Check(true, name); return; }
    throw new InvalidOperationException("Expected rejected input: " + name);
}
void InvalidActivity(Action action, string name)
{
    try { action(); }
    catch (StudioXException error) when (error.Code.StartsWith("PLUGIN_ACTIVITY", StringComparison.Ordinal)) { Check(true, name); return; }
    throw new InvalidOperationException("Expected rejected activity: " + name);
}
new PluginActivityDefinition(Icon: new([[0,0,24,24]])).Validate();
Check(true, "generic entry accepts plugin-owned drawing and optional title");
InvalidActivity(() => new PluginActivityDefinition(Version: 2).Validate(), "reject unknown activity version");
InvalidActivity(() => new PluginActivityDefinition(Title: "\n").Validate(), "reject blank or control-character title");
InvalidActivity(() => new PluginActivityIcon([]).Validate(), "reject empty vector drawing");
InvalidActivity(() => new PluginActivityIcon([[0,0,1,1,2]]).Validate(), "reject unmatched coordinate pairs");
InvalidActivity(() => new PluginActivityIcon([[0,0,double.NaN,1]]).Validate(), "reject non-finite coordinates");
InvalidActivity(() => new PluginActivityIcon([[0,0,-1,1]]).Validate(), "reject coordinates outside viewport");
InvalidActivity(() => new PluginActivityIcon(Enumerable.Repeat(new double[256],9).ToArray()).Validate(), "reject excessive vector complexity");
var bits = new BitLabPlugin();
await ProgrammerChecks.RunAsync(bits, Check, Invalid);
var protocol = new ProtocolLabPlugin();
var wave = new WaveLabPlugin();
var pixel = new PixelLabPlugin();
var b = Data(bits, new { width = "32", value = "0xFFFFFFFF", start = 0, count = 32, operation = "inspect" });
Check(b.GetProperty("signed").GetInt64() == -1 && b.GetProperty("mask").GetUInt64() == uint.MaxValue, "32-bit boundary and signed interpretation");
b = Data(bits, new { value = "0x12345678", start = 8, count = 8, operation = "replace", operand = "0xAB" });
Check(b.GetProperty("updated").GetUInt64() == 0x1234ab78 && b.GetProperty("extracted").GetUInt64() == 0xab, "field replacement preserves adjacent bits");
Check(b.GetProperty("littleEndian").GetString() == "78 AB 34 12" && b.GetProperty("bigEndian").GetString() == "12 34 AB 78", "explicit low-to-high memory byte ordering");
b = Data(bits, new { width = "8", value = "0b10000001", start = 0, count = 1, operation = "toggle" });
Check(b.GetProperty("updated").GetInt32() == 128 && b.GetProperty("signed").GetInt32() == -128, "binary input and negative int8 boundary");
Check(Data(bits, new { width="16",value="0",start=15,count=1,operation="set" }).GetProperty("updated").GetUInt64()==0x8000, "top-bit set in 16-bit word");
Check(Data(bits, new { width="8",value="255",start=0,count=8,operation="clear" }).GetProperty("updated").GetInt32()==0, "full-word clear");
Invalid(() => Data(bits, new { width = "8", value = "256", start = 0, count = 1 }), "reject oversized word without truncation");
Invalid(() => Data(bits, new { start = 31, count = 2 }), "reject crossed word boundary");
Invalid(() => Data(bits, new { start = 0, count = 1, operation = "replace", operand = "2" }), "reject oversized replacement");
Invalid(() => Data(bits, new { value = "0b102" }), "reject malformed binary");
var p = Data(protocol, new { encoding = "utf8", data = "123456789" });
Check(p.GetProperty("modbus").GetUInt32() == 0x4b37, "CRC-16/MODBUS published check vector");
Check(p.GetProperty("ccittFalse").GetUInt32() == 0x29b1, "CRC-16/IBM-3740 published check vector");
Check(p.GetProperty("crc32").GetUInt32() == 0xcbf43926, "CRC-32/ISO-HDLC published check vector");
p = Data(protocol, new { data = "01 03 00 00 00 0A" });
Check(p.GetProperty("modbusTail").GetString() == "C5 CD", "Modbus request checksum transmitted low byte first");
p = Data(protocol, new { data = "0xFF,00;01" });
Check(p.GetProperty("sum8").GetInt32() == 0 && p.GetProperty("lrc8").GetInt32() == 0 && p.GetProperty("xor8").GetInt32() == 254, "SUM8 wraparound and HEX separators");
Check(Data(protocol, new { encoding = "utf8", data = "灯" }).GetProperty("byteCount").GetInt32() == 3, "UTF-8 byte count differs from character count");
Check(Data(protocol, new { data = new string('A', 1024) }).GetProperty("byteCount").GetInt32() == 512, "maximum-length packet accepted");
Invalid(() => Data(protocol, new { data = "ABC" }), "reject odd nibble count");
Invalid(() => Data(protocol, new { data = "01 GG" }), "reject malformed hex");
Invalid(() => Data(protocol, new { data = "" }), "reject empty packet");
Invalid(() => Data(protocol, new { data = new string('A', 1026) }), "reject oversized packet");
var w = Data(wave, new { shape = "sine", samples = 8, maximum = 100, frequency = 10 });
var samples = w.GetProperty("samples").EnumerateArray().Select(x => x.GetInt32()).ToArray();
Check(samples.SequenceEqual(new[] { 0, 15, 50, 85, 100, 85, 50, 15 }), "sine quarter-cycle points and half-up quantization");
Check(w.GetProperty("sampleRate").GetDouble() == 80, "sample rate equals frequency times samples");
w = Data(wave, new { shape = "pwm", samples = 8, duty = 30 });
Check(w.GetProperty("quantizedDuty").GetDouble() == 25 && w.GetProperty("samples").EnumerateArray().Count(x => x.GetInt32() > 0) == 2, "PWM shows actual quantized duty");
Check(Data(wave, new { shape = "pwm", duty = 0 }).GetProperty("samples").EnumerateArray().All(x => x.GetInt32() == 0), "zero-percent PWM constant low");
Check(Data(wave, new { shape = "pwm", duty = 100 }).GetProperty("samples").EnumerateArray().All(x => x.GetInt32() == 4095), "hundred-percent PWM constant high");
Check(Data(wave, new { shape="triangle",samples=8,maximum=4 }).GetProperty("samples").EnumerateArray().Select(x=>x.GetInt32()).SequenceEqual(new[]{0,1,2,3,4,3,2,1}), "triangle avoids repeating next-period sample");
Invalid(() => Data(wave, new { samples = 8.5 }), "reject fractional sample count");
Invalid(() => Data(wave, new { frequency = 0 }), "reject zero frequency");
Invalid(() => Data(wave, new { samples = 257 }), "reject oversized waveform");
var px = Data(pixel, new { pattern = "custom", pixels = "100000001/010000000", packing = "row-msb" });
Check(px.GetProperty("bytes").EnumerateArray().Select(x=>x.GetInt32()).SequenceEqual(new[] { 128,128,64,0 }), "row-MSB odd width and right zero padding");
px = Data(pixel, new { pattern = "custom", pixels = "10/01", packing = "page-lsb" });
Check(px.GetProperty("bytes").EnumerateArray().Select(x=>x.GetInt32()).SequenceEqual(new[] {1,2}), "page-LSB upper pixel is bit0");
px = Data(pixel, new { pattern = "custom", pixels = "10/01", packing = "page-lsb", invert = true, flip = true });
Check(px.GetProperty("bytes").EnumerateArray().Select(x=>x.GetInt32()).SequenceEqual(new[] {1,2}), "flip and invert affect only valid pixels");
px = Data(pixel, new { pattern = "custom", pixels = "1/0/0/0/0/0/0/0/1", packing = "page-lsb" });
Check(px.GetProperty("bytes").EnumerateArray().Select(x=>x.GetInt32()).SequenceEqual(new[] {1,1}), "page order across nine rows");
Check(Data(pixel, new {pattern="custom",pixels="0",invert=true,packing="row-msb"}).GetProperty("bytes")[0].GetInt32()==128, "single inverted pixel retains seven zero padding bits");
Invalid(() => Data(pixel, new { pattern="custom",pixels="10/1" }), "reject unequal pixel rows");
Invalid(() => Data(pixel, new { pattern="custom",pixels="12" }), "reject nonbinary pixel");
Invalid(() => Data(pixel, new { pattern="custom",pixels=new string('1',33) }), "reject oversized bitmap");

var project = Path.Combine(output,"fixture-project");
Directory.CreateDirectory(project);
await using var manager = new PluginManagerService(runtime, Path.Combine(output,"user-data"));
var imported = new List<PluginCatalogEntry>();
foreach (var archive in Directory.GetFiles(archives,"*.studioxplugin").Order(StringComparer.Ordinal))
{
    var entry = await manager.ImportAsync(archive);
    Check(!entry.Enabled && entry.CanEnable && entry.Manifest?.HostTools?.Length == 0, entry.Id + " imports disabled, validated, no host tools");
    Check(entry.Manifest!.Activity is { Version: 1, Icon.Strokes.Length: > 0 }, entry.Id + " preserves plugin-owned entry and vector drawing through archive");
    imported.Add(entry);
    await manager.SetEnabledAsync(entry.Id,true);
}
Check(imported.Count==4,"four separate installable archives");
await using(var workspace = await manager.OpenWorkspaceAsync(project, (_,_,_,_) => throw new InvalidOperationException("Unexpected host call")))
{
    async Task AwaitPanel(string id, Func<StudioX.Extensions.Abstractions.PluginPanelDefinition,bool> ready)
    {
        // 面板事件与命令响应是两条独立协议消息；等到期望的发布到达再判断 UI 状态。
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!ready(workspace.LatestPanels[id+"/lab"])) await Task.Delay(15,timeout.Token);
    }
    Check(workspace.Contributions.Count==4 && workspace.LatestPanels.Count==4,"four isolated hosts activate and publish validated panels");
    foreach (var entry in imported)
    {
        var valid = await workspace.InvokeAsync(entry.Id,"agentTool","calculate",Json(new{}));
        Check(valid.GetProperty("ok").GetBoolean() && valid.GetProperty("copyText").GetString()!.Length <= 4096, entry.Id + " agent calculation and bounded copyable output");
        var before=workspace.LatestPanels[entry.Id+"/lab"];
        var bad = entry.Id switch {"studiox.bit-lab"=>Json(new{value="bad"}),"studiox.protocol-lab"=>Json(new{data="GG"}),"studiox.wave-lab"=>Json(new{samples=-1}),_=>Json(new{pattern="custom",pixels="x"})};
        var invalid=await workspace.InvokeAsync(entry.Id,"command","calculate",Json(new{values=bad}));
        await AwaitPanel(entry.Id,panel=>panel.Widgets.Any(x=>x.Id=="error"));
        Check(!invalid.GetProperty("ok").GetBoolean() && workspace.LatestPanels[entry.Id+"/lab"].Widgets.Any(x=>x.Id=="error"),entry.Id+" malformed form displays an error without killing host");
        var after=workspace.LatestPanels[entry.Id+"/lab"];
        Check(before.Widgets.Last().Value!.Value.GetString()==after.Widgets.Last().Value!.Value.GetString(),entry.Id+" keeps previous successful output on failure");
        await workspace.InvokeAsync(entry.Id,"agentTool","calculate",Json(new{}));
        await AwaitPanel(entry.Id,panel=>!panel.Widgets.Any(x=>x.Id=="error"));
        Check(!workspace.LatestPanels[entry.Id+"/lab"].Widgets.Any(x=>x.Id=="error"),entry.Id+" recovers after bad input");
    }
    var manifestPath=imported[0].ManifestPath;
    var manifest=await PluginManifest.ReadAsync(manifestPath);
    await JsonStore.WriteAsync(manifestPath,manifest with{Activity=new(Icon:new([[0,0,25,1]]))});
    try { await PluginManifest.ReadAsync(manifestPath); throw new InvalidOperationException("Invalid icon accepted"); }
    catch(StudioXException error) when(error.Code=="PLUGIN_ACTIVITY_ICON") {Check(true,"manifest loading validates icon coordinates");}
    await JsonStore.WriteAsync(manifestPath,manifest);
    await manager.SetEnabledAsync(imported[0].Id,false);
    Check(!workspace.IsPluginRunning(imported[0].Id),"disable terminates plugin host");
}
await File.WriteAllTextAsync(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new {status="passed",checks,hardwareConnected=false},new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine($"PASS {checks.Count}; known vectors, boundary cases, real archives and isolated plugin hosts; no hardware.");
