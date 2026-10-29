using System.Security.Cryptography;
using System.Text.Json.Nodes;
using StudioX.Application.Health;
using StudioX.Engine;
using StudioX.Foundation;

internal static class SdkContractChecks
{
    internal static async Task RunAsync(string directory)
    {
        var root = Path.GetFullPath(directory);
        var fixture = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root, "fixture.json")))!;
        var tools = fixture["tools"]!.GetValue<string>();
        var component = Path.Combine(tools, "studiox.mx251/0.0.121");
        var sdkFile = Path.Combine(component, "sdk/sdk.json");
        var manifestFile = Path.Combine(component, "toolset.json");
        var sdkText = await File.ReadAllTextAsync(sdkFile);
        var manifestText = await File.ReadAllTextAsync(manifestFile);
        var checks = new List<string>();
        void Check(bool passed, string name)
        {
            if (!passed)
            {
                throw new InvalidOperationException(name);
            }
            checks.Add(name);
            Console.WriteLine("PASS " + name);
        }
        async Task Reject(string name, Action<JsonNode> change, string code = "MX251_SDK")
        {
            var sdk = JsonNode.Parse(sdkText)!;
            change(sdk);
            await File.WriteAllTextAsync(sdkFile, sdk.ToJsonString());
            var manifest = JsonNode.Parse(manifestText)!;
            manifest["sha256"]!["sdk/sdk.json"] = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(sdkFile))).ToLowerInvariant();
            await File.WriteAllTextAsync(manifestFile, manifest.ToJsonString());
            try
            {
                await new ToolsetCatalog(tools).ResolveAsync("studiox.mx251", "0.0.121", "mx251-v0.0121", forceVerification: true);
                throw new InvalidOperationException("Expected rejection: " + name);
            }
            catch (StudioXException error) when (error.Code == code) { Check(true, name); }
            finally
            {
                await File.WriteAllTextAsync(sdkFile, sdkText);
                await File.WriteAllTextAsync(manifestFile, manifestText);
            }
        }
        await new ToolsetCatalog(tools).ResolveAsync("studiox.mx251", "0.0.121", "mx251-v0.0121", forceVerification: true);
        Check(true, "current SDK contract and complete content lock");
        await Reject("unknown SDK schema rejected", sdk => sdk["schema"] = 999);
        await Reject("compiler identity mismatch rejected", sdk => sdk["version"] = "0.0999");
        await Reject("build chain mismatch rejected", sdk => sdk["buildChain"]![1] = "Make");
        await Reject("entry outside SDK rejected", sdk => sdk["buildScript"] = "../outside.ps1", "PATH_UNSAFE");
        await Reject("missing entry index rejected", sdk => sdk["files"]!.AsObject().Remove("build/qualified/Build.ps1"));
        await Reject("SDK/component content locks disagree", sdk => sdk["files"]!["build/qualified/Project.cmake"] = new string('0', 64));
        await Reject("mixed target profile rejected", sdk => sdk["targets"]!["STC32G12K128"]!["profileLockSha256"] = new string('0', 64));
        await Reject("compiler overlay hash mismatch rejected", sdk => sdk["targets"]!["AI8051U-34K64-32"]!["compilerSha256"] = new string('0', 64));
        await new ToolsetCatalog(tools).ResolveAsync("studiox.mx251", "0.0.121", "mx251-v0.0121", forceVerification: true);
        Check(true, "restored SDK passes complete validation");
        try
        {
            await new ToolsetCatalog(tools).ResolveAsync("studiox.mx251", "0.0.121", "mx251-v0.0999", forceVerification: true);
            throw new InvalidOperationException("Project compiler lock ignored");
        }
        catch (StudioXException error) when (error.Code == "TOOLSET_INCOMPATIBLE") { Check(true, "project exact version/identity lock remains enforced"); }
        // 仅改变私有夹具的版本元数据，EXE 和所有运行库不变；不宣称存在此编译器版本。
        var future = fixture["syntheticVersion"]!;
        var catalog = new ToolsetCatalog(tools);
        await catalog.ResolveAsync("studiox.mx251", future["componentVersion"]!.GetValue<string>(), future["compilerId"]!.GetValue<string>(), forceVerification: true);
        Check(true, "unlisted synthetic future version accepted by same SDK contract");
        var project = Path.Combine(root, "projects/synthetic-version");
        var builder = new BuildService(catalog);
        var report = await builder.BuildAsync(project);
        await File.WriteAllTextAsync(Path.Combine(root, "synthetic-version-build.log"), report.Log);
        Check(report.Success, "unlisted version invokes canonical CMake/Ninja build");
        var expected = fixture["cases"]!.AsArray().Single(c => c!["id"]!.GetValue<string>() == "calls-small")!["on"]!.GetValue<string>();
        Check(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(project, ".build/firmware.hex")))).Equals(expected, StringComparison.OrdinalIgnoreCase), "same compiler code yields same sealed firmware after metadata-only version change");
        var health = await new ProjectHealthService(catalog, builder).InspectAsync(project, deep: true);
        await JsonStore.WriteAsync(Path.Combine(root, "synthetic-version-health.json"), health);
        Check(health.CanBuild && health.Errors == 0, "unlisted version passes deep project health checks");
        await JsonStore.WriteAsync(Path.Combine(root, "sdk-contract-results.json"), new
        {
            complete = true,
            checks,
            hardware = false,
            syntheticVersionOnly = true
        });
    }
}
