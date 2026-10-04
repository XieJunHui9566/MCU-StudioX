using System.Text;
using StudioX.Application;
using StudioX.Application.Peripherals;
using StudioX.Engine;
using StudioX.Engine.Svd;
using StudioX.Foundation;

if (args is ["--f407-restore", var hardwareProject, var hardwareTools, var hardwareSvd, var hardwareOutput])
{
    await F407Acceptance.RunAsync(hardwareProject, hardwareTools, hardwareSvd, hardwareOutput);
    return;
}

if (args.Length is not (1 or 2 or 4)) { throw new ArgumentException("<new output> [vendor SVD] [toolsets official-coredump-fixtures]"); }
var root = Path.GetFullPath(args[0]);
if (Directory.Exists(root)) { throw new InvalidOperationException("Use a new output directory."); }
Directory.CreateDirectory(root);
var checks = new List<string>();
void Check(bool value, string label) { if (!value) { throw new InvalidOperationException(label); } checks.Add(label); Console.WriteLine("PASS " + label); }
async Task Reject(Func<Task> action, string label)
{
    try
    {
        await action();
    }
    catch (Exception e) when (e is StudioXException or System.Xml.XmlException or OverflowException) { Check(true, label); return; }
    throw new InvalidOperationException("Expected rejection: " + label);
}
var xml = """
<device schemaVersion="1.3"><name>OfflineFixture</name><addressUnitBits>8</addressUnitBits><size>32</size><access>read-write</access><resetValue>0</resetValue><peripherals>
<peripheral><name>GPIO</name><baseAddress>0x40020000</baseAddress><registers>
<register><name>CONTROL</name><addressOffset>0</addressOffset><fields><field><name>MODE</name><bitRange>[3:0]</bitRange><enumeratedValues><enumeratedValue><name>Enabled</name><value>1</value></enumeratedValue></enumeratedValues></field></fields></register>
<register derivedFrom="CONTROL"><name>COPY</name><addressOffset>4</addressOffset></register>
<register><name>CLEAR</name><addressOffset>8</addressOffset><readAction>clear</readAction><modifiedWriteValues>oneToClear</modifiedWriteValues></register>
<cluster><name>CHANNEL%s</name><dim>2</dim><dimIncrement>16</dimIncrement><dimIndex>A-B</dimIndex><addressOffset>16</addressOffset><register><name>VALUE%s</name><dim>2</dim><dimIncrement>4</dimIncrement><addressOffset>0</addressOffset><access>read-only</access></register></cluster>
</registers></peripheral><peripheral derivedFrom="GPIO"><name>PORT2</name><baseAddress>0x40020400</baseAddress></peripheral>
</peripherals></device>
""";
SvdDevice Parse(string text) => SvdParser.Parse(Encoding.UTF8.GetBytes(text));
var device = Parse(xml);
Check(device.Registers.Count == 14, "arrays, clusters and inherited peripherals expand exactly");
Check(device.Registers.Single(r => r.Path == "PORT2.COPY").Fields.Single().Enumerations[1] == "Enabled ", "derived registers inherit fields and enumerations");
Check(device.Registers.Single(r => r.Path == "GPIO.CHANNELB.VALUE1").Address == 0x40020024, "cluster and register array offsets compose");
Check(device.Registers.Single(r => r.Path == "GPIO.CLEAR") is { HasReadSideEffects: true, ModifiedWriteValues: "oneToClear" }, "read and modified-write side effects retained");
Check(!device.Registers.Single(r => r.Path == "GPIO.CHANNELA.VALUE0").CanWrite, "read-only registers never offer writes");
Check(device.Registers[0].Fields[0].Extract(0xf1) == 1, "bit-field value decoding");
await Reject(() => Task.FromResult(Parse("<!DOCTYPE device [<!ENTITY x SYSTEM 'file:///C:/Windows/win.ini'>]>" + xml.Replace("OfflineFixture", "&x;"))), "DTD and external entities prohibited");
await Reject(() => Task.FromResult(Parse(xml.Replace("derivedFrom=\"CONTROL\"", "derivedFrom=\"COPY\""))), "inheritance cycles rejected");
await Reject(() => Task.FromResult(Parse(xml.Replace("[3:0]", "[33:0]"))), "field boundaries enforced");
await Reject(() => Task.FromResult(Parse(xml.Replace("0x40020400", "0x100000000"))), "32-bit address overflow rejected");
await Reject(() => Task.FromResult(Parse(xml.Replace("<dim>2</dim>", "<dim>2000</dim>"))), "array expansion bounded");
Check(!Parse(xml.Replace("<access>read-write</access>", "<access>unknown</access>")).Registers[0].CanRead, "unknown access remains disabled");
var file = Path.Combine(root, "fixture.svd");
await File.WriteAllTextAsync(file, xml);
var project = Path.Combine(root, "project");
Directory.CreateDirectory(Path.Combine(project, ".studiox"));
await JsonStore.WriteAsync(Path.Combine(project, ".studiox/project.json"), new ProjectManifest(1, "fixture", "fixture", "1.0.0", new string('a', 64), "STM32F407ZG", "hal", "arm.gnu", "1.0.0", "arm-gnu-15.2.rel1"));
await using var debug = new DebugSessionService(Path.Combine(root, "data"));
await debug.OpenProjectAsync(project);
var peripherals = new PeripheralService(debug, Path.Combine(root, "data"));
await Reject(() => peripherals.ImportAsync(project, file, "STM32F407ZGT6"), "SVD explicit device confirmation must match project");
var doc = await peripherals.ImportAsync(project, file, "STM32F407ZG");
Check((await peripherals.OpenAsync(project))!.Device.Sha256 == doc.Device.Sha256, "content-addressed binding survives reopen with hash validation");
await Reject(() => peripherals.ReadAsync(doc, doc.Device.Registers[0]), "disconnected session never reads MMIO");
await Reject(() => peripherals.WriteAsync(peripherals.PreviewWrite(doc, doc.Device.Registers[0], 1)), "disconnected session never writes MMIO");
await File.WriteAllTextAsync(Path.Combine(root, "data/svd", Directory.GetDirectories(Path.Combine(root, "data/svd")).Select(Path.GetFileName).Single()!, doc.Binding.File), xml + " ");
await Reject(() => peripherals.OpenAsync(project), "corrupted saved SVD binding rejected");
var faults = new FaultAnalysisService(new ToolsetCatalog(Path.Combine(root, "unused-tools")), debug);
var originalReport = faults.Analyze("ESP32-S3", "fixture");
originalReport = originalReport with
{
    Evidence = originalReport.Evidence with
    {
        FirmwareMatched = true,
        Raw = new string('x', 3 * 1024 * 1024)
    },
    CoreDump = new(new string('a', 64), "b64", "esp32s3", "5.5.4", "fixture", "abcdef1234", true, [], null, null, "fixture")
};
var reportFile = Path.Combine(root, "fault-report.json");
await faults.ExportAsync(originalReport, reportFile);
var importedReport = await faults.ImportAsync(reportFile);
Check(importedReport.Evidence.Raw == originalReport.Evidence.Raw && !importedReport.Evidence.FirmwareMatched &&
    importedReport.CoreDump is { HashMatches: false } && importedReport.Findings.Single().Contains("未重新验证", StringComparison.Ordinal),
    "exported diagnostic above 2 MiB reimports without inheriting firmware trust");
var oversized = Path.Combine(root, "oversized-report.json");
await using (var sizeFixture = File.Create(oversized)) { sizeFixture.SetLength(16 * 1024 * 1024 + 1); }
await Reject(() => faults.ImportAsync(oversized), "oversized imported report rejected before deserialization");
if (args.Length >= 2)
{
    var vendor = await SvdParser.LoadAsync(args[1]);
    Check(vendor.Registers.Count > 100 && vendor.Registers.Any(r => r.Path == "RCC.AHB1ENR"), "real vendor SVD parsed with RCC register map");
    await JsonStore.WriteAsync(Path.Combine(root, "vendor.json"), new
    {
        vendor.Name,
        vendor.Sha256,
        registerCount = vendor.Registers.Count
    });
}
if (args.Length == 4)
{
    await CoreDumpChecks.RunAsync(root, args[2], args[3], Check);
}
await JsonStore.WriteAsync(Path.Combine(root, "result.json"), new { success = true, hardware = false, checks });
