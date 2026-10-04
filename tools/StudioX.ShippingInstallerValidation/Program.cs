using System.Reflection;
using System.Text;
using StudioX.ShippingInstallerValidation;

Console.OutputEncoding = new UTF8Encoding(false);
try
{
    switch (args)
    {
        case ["snapshot", var root, var output]:
            if (File.Exists(output))
            {
                FileEvidence.Compare(root, output, output + ".resume-check.json");
            }
            else
            {
                FileEvidence.Write(output, FileEvidence.Capture(root));
            }
            break;
        case ["compare", var root, var snapshot, var output]:
            FileEvidence.Compare(root, snapshot, output);
            break;
        case ["payload", var installed, var output, .. var preserved]:
            FileEvidence.VerifyPayload(installed, output, preserved);
            break;
        case ["bindings", var installed, var output]:
            FileEvidence.Write(output, new InstalledServices(installed).Bindings());
            break;
        case ["import", var installed, var archive, var data, var output]:
            await new InstalledServices(installed).ImportAsync(archive, data, output);
            break;
        case ["create", var installed, var pack, var device, var template, var name, var project, var data, var output]:
            await new InstalledServices(installed).CreateAsync(pack, device, template, name, project, data, output);
            break;
        case ["build", var installed, var project, var data, var output]:
            await new InstalledServices(installed).BuildAsync(project, data, output);
            break;
        case ["export", var root, var archive, var output]:
            FileEvidence.Export(root, archive, output);
            break;
        case ["echo", var output, .. var values]:
            FileEvidence.Write(output, values);
            break;
        default:
            throw new ArgumentException("Use snapshot, compare, payload, bindings, import, create, or build with explicit paths.");
    }
    return 0;
}
catch (Exception error)
{
    while (error is TargetInvocationException { InnerException: { } inner })
    {
        error = inner;
    }
    Console.Error.WriteLine(error);
    return 1;
}
