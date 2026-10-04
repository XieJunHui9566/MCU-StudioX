using StudioX.ComponentCatalogValidation;
using StudioX.Foundation;

var stcPack = args is ["--stc-pack", _, _, _, _];
var migration = args is ["--migration", _, _];
var junction = args is ["--check-junction", _, _, _];
var familyComponents = args is ["--family-components", _, _];
var sdkRevision = args is ["--sdk-revision", _, _];
var familyBoundaries = args is ["--family-boundaries", _, _];
var familyEntryPoints = args is ["--family-entry-points", _, _, _];
var idfVersions = args is ["--idf-versions", _, _, _];
var idfImports = args is ["--idf-imports", _, _, _];
var idfImportedCheck = args is ["--idf-imported-check", _, _, _];
var idfOriginal = args is ["--idf-original", _, _];
var idfProjectCheck = args is ["--idf-project-check", _, _, _];
var acquisition = args is ["--acquisition", _, _] or ["--acquisition", _, _, "--online"];
var families = args.Length is 4 or 5 && args[0] == "--families";
if (!acquisition && !stcPack && !migration && !junction && !families && !familyComponents && !sdkRevision && !familyBoundaries && !familyEntryPoints && !idfVersions && !idfImports && !idfImportedCheck && !idfOriginal && !idfProjectCheck && args.Length is not (2 or 3)) { throw new ArgumentException("Use a documented validation mode and new output directory."); }
var output = Path.GetFullPath(args[acquisition || stcPack || migration || junction || families || familyComponents || sdkRevision || familyBoundaries || familyEntryPoints || idfVersions || idfImports || idfImportedCheck || idfOriginal || idfProjectCheck ? 1 : 0]);
if (Directory.Exists(output) || File.Exists(output)) { throw new ArgumentException("Output must be new."); }
Directory.CreateDirectory(output);
var checks = new List<string>();
void Check(bool value, string text) { if (!value) { throw new InvalidOperationException(text); } checks.Add(text); Console.WriteLine("PASS " + text); }
try
{
    if (acquisition)
    {
        await AcquisitionChecks.RunAsync(output, Path.GetFullPath(args[2]), args.Length == 4, Check);
    }
    else if (idfProjectCheck)
    {
        await IdfVersionChecks.CompleteMatrixAsync(output, Path.GetFullPath(args[2]), Path.GetFullPath(args[3]), Check);
    }
    else if (idfOriginal)
    {
        await IdfVersionChecks.OriginalAsync(output, Path.GetFullPath(args[2]), Check);
    }
    else if (idfVersions)
    {
        await IdfVersionChecks.RunAsync(output, Path.GetFullPath(args[2]), Path.GetFullPath(args[3]), Check);
    }
    else if (idfImportedCheck)
    {
        await IdfVersionChecks.CheckImportedAsync(output, Path.GetFullPath(args[2]), Path.GetFullPath(args[3]), Check);
    }
    else if (idfImports)
    {
        await IdfVersionChecks.ImportAndSelectAsync(output, Path.GetFullPath(args[2]), Path.GetFullPath(args[3]), Check);
    }
    else if (familyEntryPoints)
    {
        await FamilyEntryPointChecks.RunAsync(output, Path.GetFullPath(args[2]), Path.GetFullPath(args[3]), Check);
    }
    else if (familyBoundaries)
    {
        await FamilyMigrationChecks.ExistingBoundariesAsync(output, Path.GetFullPath(args[2]), Check);
    }
    else if (sdkRevision)
    {
        await FamilyComponentChecks.SdkRevisionAsync(output, Path.GetFullPath(args[2]), Check);
    }
    else if (familyComponents)
    {
        await FamilyComponentChecks.RunAsync(output, Path.GetFullPath(args[2]), Check);
    }
    else if (families)
    {
        await FamilyMigrationChecks.RunAsync(output, Path.GetFullPath(args[2]), Path.GetFullPath(args[3]), args.Length == 5 ? args[4].Split(',') : null, Check);
    }
    else if (stcPack)
    {
        await StcComponentPackChecks.RunAsync(output, Path.GetFullPath(args[2]), Path.GetFullPath(args[3]), Path.GetFullPath(args[4]), Check);
    }
    else if (migration)
    {
        await MigrationChecks.RunAsync(output, Path.GetFullPath(args[2]), Check);
    }
    else if (junction)
    {
        var target = Path.Combine(Path.GetFullPath(args[3]), "must-not-create");
        var service = new StudioX.Application.Tools.ComponentMigrationService(new(Path.Combine(output, "packs")), new(new(Path.Combine(output, "empty-tools"))));
        var placeholder = new StudioX.Packages.InstalledPack(new(1, "test.placeholder", "1.0.0", "Fixture", "Fixture", []), output, "fixture");
        try
        {
            await service.PreviewAsync(Path.GetFullPath(args[2]), placeholder, target);
            throw new InvalidOperationException("Junction accepted");
        }
        catch (StudioXException error) { Check(error.Code == "PATH_LINK" && !Directory.Exists(target), "junction parent cannot alias a migration destination into the original project"); }
    }
    else
    {
        await CatalogTrustChecks.RunAsync(output, Path.GetFullPath(args[1]), args.Length == 3 && args[2] == "--online", Check);
        await CompatibilityChecks.RunAsync(output, Check);
    }
    await JsonStore.WriteAsync(Path.Combine(output, "result.json"), new
    {
        success = true,
        hardware = false,
        checks
    });
    return 0;
}
catch (Exception error)
{
    await JsonStore.WriteAsync(Path.Combine(output, "result.json"), new
    {
        success = false,
        hardware = false,
        checks,
        diagnostic = error.ToString()
    });
    throw;
}
