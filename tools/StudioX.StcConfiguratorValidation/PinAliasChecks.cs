namespace StudioX.StcConfiguratorValidation;

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using StudioX.StcConfigurator;

internal static class PinAliasChecks
{
    internal static void Run(string configuration, string headerPath, Action<bool, string> check)
    {
        var model = new StcConfigurationModel(configuration);
        void Apply(object value) => model.Apply(JsonSerializer.SerializeToElement(value));
        void Name(string pinId, string label) => Apply(new
        {
            action = "pinLabel",
            pinId,
            label
        });
        string Header() => model.GenerateFiles().Single(file => file.Path == "include/generated/mcuconfig/gpio.h").Content;
        var assembly = typeof(StcConfigurationModel).Assembly;
        using var stream = assembly.GetManifestResourceStream(assembly.GetManifestResourceNames().Single(name => name.EndsWith("stc15-register-symbols.json", StringComparison.Ordinal)))!;
        var symbols = JsonNode.Parse(stream)!;
        var declared = Regex.Matches(File.ReadAllText(headerPath), @"(?m)^__(?:sfr|sbit)\s+__at\([^\r\n]+\)\s+(\w+);|^#define\s+(\w+)")
            .Select(match => match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value).ToHashSet(StringComparer.Ordinal);
        check(symbols["sha256"]!.GetValue<string>() == Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(headerPath))) &&
            declared.SetEquals(symbols["symbols"]!.AsArray().Select(symbol => symbol!.GetValue<string>())), "reserved register symbols match the exact packaged STC15 header and SHA-256");
        Apply(new
        {
            action = "assignPin",
            pinId = "pad-4",
            functionId = "gpio"
        });
        Apply(new
        {
            action = "parameters",
            values = new Dictionary<string, string> { ["clockSource"] = "IRC", ["clockMHz"] = "24", ["reserve"] = "none", ["uartRoute"] = "p36" }
        });
        Name("pad-4", "LED");
        var uartPin = model.View("", true).Pins.Single(pin => pin.Name == "P3.6").Id;
        Name(uartPin, "CONSOLE_RX");
        check(Header().Contains("#define LED P10\n", StringComparison.Ordinal) && Header().Contains("#define CONSOLE_RX P36\n", StringComparison.Ordinal) &&
            model.GenerateCode().Contains("#define LED P10\n", StringComparison.Ordinal), "GPIO and peripheral pin labels generate direct 8051 bit aliases in split and legacy code");
        var reloaded = new StcConfigurationModel(model.Document.ToJsonString());
        check(reloaded.GenerateFiles().Single(file => file.Path == "include/generated/mcuconfig/gpio.h").Content == Header(), "saved labels regenerate identical aliases after reload");
        foreach (var label in new[] { "1LED", "LED-1", "LED\nP0", "if", "uint8_t", "EA", "P10", "PWM2T1", "saved_ea", "sx_gpio_init", "STUDIOX_CLOCK_HZ", "__sbit", new string('L', 33) })
        {
            var before = model.Document.ToJsonString();
            try
            {
                Name("pad-4", label);
                throw new InvalidOperationException("Invalid alias accepted: " + label);
            }
            catch (ArgumentException)
            {
                check(model.Document.ToJsonString() == before, "invalid or reserved alias is rejected without changing configuration: " + label.Replace("\n", "\\n", StringComparison.Ordinal));
            }
        }
        Name(uartPin, "LED");
        check(model.Errors().Any(error => error.Contains("重复", StringComparison.Ordinal)) && !model.View("", true).CanGenerate, "duplicate pin aliases disable code generation");
        try
        {
            model.GenerateFiles();
            throw new ArgumentException("Duplicate aliases generated.");
        }
        catch (InvalidOperationException) { check(true, "generator refuses duplicate aliases even without the UI"); }
        Name(uartPin, "led");
        check(model.Errors().Count == 0 && Header().Contains("#define led P36\n", StringComparison.Ordinal), "C aliases retain case-sensitive spelling");
        Name("pad-4", "STATUS_LED");
        check(!Header().Contains("#define LED ", StringComparison.Ordinal) && Header().Contains("#define STATUS_LED P10\n", StringComparison.Ordinal), "renaming replaces the old macro");
        Name("pad-4", "");
        check(!Header().Contains("STATUS_LED", StringComparison.Ordinal), "clearing a pin label removes its macro");
        Apply(new
        {
            action = "parameters",
            values = new Dictionary<string, string> { ["uartRoute"] = "off" }
        });
        check(!Header().Contains("#define led ", StringComparison.Ordinal), "releasing a peripheral pin removes its alias");
        var legacy = model.Document.DeepClone();
        legacy["configuration"]!["pins"]![0]!["label"] = "1OLD";
        var old = new StcConfigurationModel(legacy.ToJsonString());
        check(old.Errors().Any(error => error.Contains("C 标识符", StringComparison.Ordinal)) && !old.View("", true).CanGenerate, "old display-only labels remain readable and require correction before generation");
        old.Apply(JsonSerializer.SerializeToElement(new
        {
            action = "pinLabel",
            pinId = "pad-4",
            label = "FIXED"
        }));
        check(old.View("", true).CanGenerate, "old invalid label can be corrected through the normal naming action");
    }
}
