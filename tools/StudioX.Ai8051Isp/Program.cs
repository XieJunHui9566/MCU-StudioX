using System.Text.Json;

namespace StudioX.Ai8051Isp;

internal static class Program
{
    private static void Emit(object value) => Console.WriteLine(JsonSerializer.Serialize(value));
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        using var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
        try
        {
            if (args.Length == 0)
            {
                throw new ArgumentException("Commands: list; plan --hex PATH [--config JSON]; inspect --log NEW-PATH; flash --hex PATH --sha256 HASH --log NEW-PATH; configure --hex PATH --sha256 HASH --config JSON --log NEW-PATH; selftest; verify-capture --capture NDJSON --hex PATH.");
            }
            string command = args[0];
            var options = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 1; i < args.Length; i += 2)
            {
                if (i + 1 >= args.Length || !args[i].StartsWith("--") || !options.TryAdd(args[i], args[i + 1]))
                {
                    throw new ArgumentException("Invalid/repeated option.");
                }
            }
            string Required(string key) => options.TryGetValue(key, out var value) ? value : throw new ArgumentException("Required " + key);
            string[] accepted = command switch
            {
                "list" or "selftest" => [],
                "verify-capture" => ["--capture", "--hex"],
                "plan" => ["--hex", "--config"],
                "inspect" => ["--log", "--device"],
                "flash" => ["--hex", "--sha256", "--log", "--device", "--config"],
                "configure" => ["--hex", "--sha256", "--log", "--device", "--config", "--recover-erased-config"],
                _ => throw new ArgumentException("Unknown command.")
            };
            foreach (string key in options.Keys)
            {
                if (!accepted.Contains(key))
                {
                    throw new ArgumentException($"Unknown option for {command}: {key}");
                }
            }
            if (command == "verify-capture")
            {
                Emit(ProtocolChecks.Verify(Required("--capture"), Required("--hex")));
                return 0;
            }
            if (command == "selftest")
            {
                Emit(await GuardChecks.Run());
                return 0;
            }
            if (command == "list")
            {
                Emit(new
                {
                    devices = HidTransport.Enumerate()
                });
                return 0;
            }
            DeviceConfiguration? configuration = options.TryGetValue("--config", out var configPath) ? DeviceConfiguration.Load(configPath) : null;
            if (command == "configure" && configuration == null)
            {
                throw new ArgumentException("configure requires --config.");
            }
            if (command == "inspect" && configuration != null)
            {
                throw new ArgumentException("inspect does not accept a configuration write.");
            }
            bool recoverErased = options.TryGetValue("--recover-erased-config", out var recovery) && recovery == "true";
            if (options.ContainsKey("--recover-erased-config") && (!recoverErased || configuration == null || command != "configure"))
            {
                throw new ArgumentException("Erased-config recovery requires configure, a verified profile and true.");
            }
            FirmwareImage? firmware = command is "plan" or "flash" or "configure" ? FirmwareImage.Load(Required("--hex")) : null;
            if (command == "plan")
            {
                Emit(new
                {
                    stage = "plan",
                    model = "AI8051U-34K64",
                    cpuMode = 32,
                    codeBytes = firmware!.Code.Length,
                    sha256 = firmware.Sha256,
                    baseAddress = "FF0000",
                    configuration,
                    options = configuration == null ? "preserve" : "write-verified-profile",
                    flashReadback = false,
                    eepromMayBeErased = true,
                    hardwareOpened = false
                });
                return 0;
            }
            if (firmware != null && !firmware.Sha256.Equals(Required("--sha256"), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Firmware SHA-256 differs from plan.");
            }
            var devices = HidTransport.Enumerate().Where(x => !options.TryGetValue("--device", out var selector) || x.Path.Equals(selector, StringComparison.OrdinalIgnoreCase)).ToList();
            if (devices.Count != 1)
            {
                throw new InvalidOperationException($"Expected one matching STC bridge; found {devices.Count}. Use --device for an exact path.");
            }
            using var log = new StreamWriter(new FileStream(Required("--log"), FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
            long sequence = 0;
            void Event(object value)
            {
                log.WriteLine(JsonSerializer.Serialize(new
                {
                    sequence = ++sequence,
                    hostUtc = DateTimeOffset.UtcNow,
                    value
                }));
                Emit(value);
            }
            using var transport = new HidTransport(devices[0], (direction, report) => log.WriteLine(JsonSerializer.Serialize(new { sequence = ++sequence, hostUtc = DateTimeOffset.UtcNow, direction, hex = Convert.ToHexString(report) })));
            var bridge = new BridgeProtocol(transport, Event);
            Event(new
            {
                stage = "open",
                command,
                device = devices[0],
                firmwareSha256 = firmware?.Sha256,
                configuration,
                options = configuration == null ? "preserve" : "write-verified-profile"
            });
            var result = await IspSession.ExecuteAsync(bridge, firmware, configuration, Event, cancel.Token, recoverErased);
            Event(new
            {
                stage = "complete",
                command,
                target = result.Target,
                programAcknowledged = result.ProgramAcknowledged,
                configurationReadback = result.ConfigurationReadback,
                flashReadback = result.FlashReadback,
                runtimeVerified = result.RuntimeVerified
            });
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return error is OperationCanceledException ? 130 : 1; }
    }
}
