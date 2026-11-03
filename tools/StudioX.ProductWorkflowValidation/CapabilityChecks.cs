namespace StudioX.ProductWorkflowValidation;

using StudioX.Application.Onboarding;
using StudioX.Engine;

internal static class CapabilityChecks
{
    public static void Run(Action<bool, string> check)
    {
        var project = new ProjectManifest(1, "fixture", "validation.pack", "1.0.0", new string('A', 64),
            "STM32F407ZGT6", "blank", "arm.gnu", "1.0.0", "arm-gnu-15.2.rel1");
        foreach (var kind in Enum.GetValues<ProjectKind>())
        {
            var current = project with
            {
                Kind = kind
            };
            var capabilities = ProjectCapabilities.For(current);
            var steps = FirstProjectGuideService.Steps(new("fixture", Capabilities: capabilities));
            check(steps[4].Action == (capabilities.Build ? "build" : "help") && steps[2].Status.Contains(capabilities.Scope, StringComparison.Ordinal),
                kind + " guide follows the same build eligibility and scope used by menus and health");
            check(steps[3].Instructions.Contains(capabilities.SourceWorkflow, StringComparison.Ordinal) &&
                steps[^1].Instructions.Contains(capabilities.RuntimeWorkflow, StringComparison.Ordinal), kind + " guide uses framework source and runtime instructions");
        }
        var pic = ProjectCapabilities.For(project with
        {
            Kind = ProjectKind.Pic
        });
        check(pic.Build && pic.Configure && !pic.BuildSettings && pic.SourceRegistration && !pic.Download && !pic.Debug,
            "PIC uses CMake source registration while unknown debug targets stay disabled");
        var pic877 = ProjectCapabilities.For(project with
        {
            Kind = ProjectKind.Pic,
            DeviceId = "PIC16F877A"
        });
        check(pic877.Debug && pic877.Download && pic877.Configure, "PIC16F877A offers precise PICkit3 / MDB debug and CMake capabilities");
        var picHeader = ProjectCapabilities.For(project with
        {
            Kind = ProjectKind.Pic,
            DeviceId = "PIC16F628A"
        });
        check(!picHeader.Debug && picHeader.Build, "devices requiring an ICD header keep compilation but do not claim native debugging");
        var arduino = ProjectCapabilities.For(project with
        {
            Kind = ProjectKind.Arduino
        });
        check(arduino.Build && arduino.Download && !arduino.Debug && !arduino.Configure && !arduino.SourceRegistration && arduino.Scope.Contains("尚未验收"), "Arduino separates supported upload workflow from unverified board acceptance");
        var zephyr = ProjectCapabilities.For(project with
        {
            Kind = ProjectKind.Zephyr,
            Zephyr = new(2, "fixture", "fixture", "", "4.4.2", false)
        });
        check(zephyr.Build && zephyr.Configure && !zephyr.Download && !zephyr.Debug && !zephyr.BuildSettings, "modern Zephyr builds while preserving custom board hardware boundaries");
        foreach (var device in new[] { "IAP15F2K61S2", "IAP15L2K61S2", "STC89C52RC", "STC15F2K60S2", "STC8H8K64U", "STC32G12K128", "IAP15F2K61S2-extra", "UNKNOWN" })
        {
            var stc = ProjectCapabilities.For(project with
            {
                DeviceId = device,
                ToolsetId = "stc.sdcc",
                CompilerId = "sdcc-4.5.0-15242"
            });
            var supported = device is "IAP15F2K61S2" or "IAP15L2K61S2";
            check(stc.Debug == supported && stc.Build && stc.Download && stc.BuildSettings && stc.SourceRegistration,
                device + " keeps STC build/ISP capabilities and exposes Mon51 only for supported exact targets");
        }
        check(!ProjectCapabilities.For(null).Build, "closed workspace has no project build capability");
    }
}
