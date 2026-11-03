using System.Buffers.Binary;
using System.Text.Json;
using StudioX.Engine;
using StudioX.Engine.StcUsb;
using StudioX.Ai8051Isp;
using StudioX.Foundation;
using Image = StudioX.Ai8051Isp.FirmwareImage;
var root = args[0];
var image = Image.Load(Path.Combine(root,"AI8051U-MX251-0.0170-test.hex"));
var usb = Fixture.Load(root,"usb"); var serial = Fixture.Load(root,"serial");
var replay = new UsbReplay(usb);
var protocol = new Mx251NativeUsbProtocol(replay, _=>{});
await protocol.IdentifyAsync(default);
var result = await protocol.ProgramAsync(image.Code,new(DownloadMode:Mx251DownloadMode.NativeUsb),default);
Check(result.ProgramAcknowledged && !result.ConfigurationReadback && !result.FlashReadback && replay.Done,"USB captured frames / image / preserving options / honest completion");
var uart = new SerialReplay(serial);
var sp = new Mx251SerialProtocol(uart,_=>{});
await sp.IdentifyAsync(default);
var sr = await sp.ProgramAsync(image.Code,new(DownloadMode:Mx251DownloadMode.Serial,SerialPort:"COM6"),default);
Check(uart.Done && uart.Bauds.SequenceEqual(new[]{2400,115200}) && sr.ProgramAcknowledged && !sr.ConfigurationReadback,"serial capture, fragmented RX, calibration, baud switch, 4 blocks, options and run");
// Reject a different model before calibration/erase; do not replay unchanged legacy protocols.
var wrong = new UsbReplay(usb, wrongModel:true); var bad = new Mx251NativeUsbProtocol(wrong,_=>{});
await Reject(()=>bad.IdentifyAsync(default)); Check(wrong.Writes==1,"wrong USB model rejected after identification only");
var ack = new UsbReplay(usb,badAck:true); var ap = new Mx251NativeUsbProtocol(ack,_=>{});
await ap.IdentifyAsync(default); await Reject(()=>ap.ProgramAsync(image.Code,new(DownloadMode:Mx251DownloadMode.NativeUsb),default));
Check(!ack.SentOptions && !ack.SentRun,"failed block ACK stops options and run");
var changed = new UsbReplay(usb); var cp = new Mx251NativeUsbProtocol(changed,_=>{});
await cp.IdentifyAsync(default); await Reject(()=>cp.ProgramAsync(image.Code,new(IrcHz:24000000,DownloadMode:Mx251DownloadMode.NativeUsb),default));
Check(changed.Writes==1,"USB clock mismatch rejected before erase");
// A full address window catches overflow at the last 128/64-byte packet; ACK fixture is synthetic.
var edge = new EdgeUsb(usb.First(e=>e.Direction=="rx").Payload);var ep=new Mx251NativeUsbProtocol(edge,_=>{});
await ep.IdentifyAsync(default);await ep.ProgramAsync(new byte[65536],new(DownloadMode:Mx251DownloadMode.NativeUsb),default);
Check(edge.Blocks==512 && edge.LastAddress==0xff80,"USB 64 KiB boundary offset and no wrap");
Console.WriteLine("PASS: captured USB/serial image and programming flows, UART calibration/fragments, wrong target, ACK failure, clock mismatch, full USB code window. No hardware opened.");
static void Check(bool ok,string label){if(!ok)throw new Exception(label);Console.WriteLine("PASS "+label);}
static async Task Reject(Func<Task> f){try{await f();}catch(StudioXException){return;}throw new Exception("expected rejection");}
record Entry(string Direction,byte[] Frame,byte[] Payload);
static class Fixture {
 public static List<Entry> Load(string root,string mode)=>JsonDocument.Parse(File.ReadAllText(Path.Combine(root,mode+"-decoded.json"))).RootElement.EnumerateArray().Where(e=>e.GetProperty("checksum").GetBoolean()).Select(e=>new Entry(e.GetProperty("direction").GetString()!,Convert.FromHexString(e.GetProperty("frame").GetString()!),Convert.FromHexString(e.GetProperty("payload").GetString()!))).ToList();
 public static byte[] Reply(byte[] payload){var f=StcUsbFrames.EncodeFrame(payload);f[2]=0x68;BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(f.Length-3),(ushort)(f.AsSpan(2,f.Length-5).ToArray().Sum(b=>b)&65535));return f;}
 public static byte[] Report(byte[] frame){var r=new byte[65];frame.CopyTo(r,1);return r;}
}
sealed class UsbReplay(List<Entry> data,bool wrongModel=false,bool badAck=false):IHidReportTransport {
 int cursor; readonly List<byte> pending=[]; public int Writes;public bool SentOptions,SentRun; public bool Done=>cursor==data.Count;
 public Task Write(byte[] report,CancellationToken token){Writes++;pending.AddRange(report[1..]);if(pending.Count<5)return Task.CompletedTask;var n=(pending[3]<<8|pending[4])+2;if(pending.Count<n)return Task.CompletedTask;
 var frame=pending.Take(n).ToArray();pending.Clear();var expected=data[cursor++];if(expected.Direction!="tx")throw new Exception("USB direction");
 if(frame[5]==4){SentOptions=true; var source=data.First(e=>e.Direction=="rx").Payload;var payload=expected.Payload.ToArray();source.AsSpan(1,7).CopyTo(payload.AsSpan(29));expected=expected with{Frame=StcUsbFrames.EncodeFrame(payload)};}
 SentRun|=frame[5]==255;if(!frame.AsSpan().SequenceEqual(expected.Frame))throw new Exception("USB frame mismatch "+Convert.ToHexString(frame));return Task.CompletedTask; }
 public Task<byte[]> Read(int timeout,CancellationToken token){var e=data[cursor++];if(e.Direction!="rx")throw new Exception("USB RX order");var p=e.Payload.ToArray();if(wrongModel&&p.Length==54)p[21]++;if(badAck&&p.Length==2&&p[0]==2)p[1]=0;return Task.FromResult(Fixture.Report(Fixture.Reply(p)));}
}
sealed class SerialReplay(List<Entry> data):IMx251SerialTransport {
 int cursor;readonly Queue<byte> pending=new();public List<int> Bauds=[]; public bool Done=>cursor==data.Count;
 public void SetBaudRate(int baud)=>Bauds.Add(baud);
 public Task WriteAsync(byte[] bytes,CancellationToken token){
 if(bytes.Length==1){if(cursor==0){pending.Enqueue(0);Enqueue(data[cursor++].Frame);}return Task.CompletedTask;}
 var expected=data[cursor++];var p=bytes.AsSpan(5,bytes.Length-8).ToArray();if(expected.Direction!="tx")throw new Exception("serial order");
 if(p[0]==0&&p[1]==12){
  // Shared bridge algorithm centers one trim above the official tool. Replay known measurements by trim;
  // the one extra high endpoint is synthetic monotonic data and is not the chosen minimum.
  var reply=data[cursor++].Payload.ToArray();var measured=reply.ToArray();var known=expected.Payload;
  for(int i=0;i<12;i++){int trim=p[2+2*i];int at=Array.FindIndex(Enumerable.Range(0,12).ToArray(),j=>known[2+2*j]==trim);ushort count=at>=0?BinaryPrimitives.ReadUInt16BigEndian(measured.AsSpan(2+2*at)):(ushort)0xffff;BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(2+2*i),count);}
  Enqueue(Fixture.Reply(reply));return Task.CompletedTask;
 }
 if(!bytes.AsSpan().SequenceEqual(expected.Frame))throw new Exception("serial TX mismatch "+Convert.ToHexString(bytes));
 if(cursor<data.Count&&data[cursor].Direction=="rx")Enqueue(data[cursor++].Frame);
 return Task.CompletedTask; }
 void Enqueue(byte[] frame){foreach(var b in frame)pending.Enqueue(b);}
 public Task<byte[]> ReadAsync(int timeout,CancellationToken token)=>Task.FromResult(pending.Count>0?new[]{pending.Dequeue()}:Array.Empty<byte>());
 public void Dispose(){}
}
sealed class EdgeUsb(byte[] status):IHidReportTransport {
 readonly List<byte> packet=[]; byte command;public int Blocks,LastAddress;
 public Task Write(byte[] report,CancellationToken token){packet.AddRange(report[1..]);int n=(packet[3]<<8|packet[4])+2;if(packet.Count>=n){command=packet[5];if(command is 0x32 or 0x12){Blocks++;LastAddress=packet[6]<<8|packet[7];}packet.Clear();}return Task.CompletedTask;}
 public Task<byte[]> Read(int ms,CancellationToken token)=>Task.FromResult(Fixture.Report(Fixture.Reply(command switch{0=>status,1=>[1],5=>[5],3=>[3,0x78,0xb4,0,0,0,0,0],0x32 or 0x12=>[2,0x54],4=>[4,0x54],_=>throw new Exception("unexpected") })));
}
