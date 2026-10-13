namespace StudioX.Desktop;

using System.Diagnostics;
using StudioX.Foundation;
using StudioX.Packages;

public partial class MainWindow
{
    private async Task CheckPackCatalogLoadingAsync(string directory, string archive)
    {
        var checks = new List<string>();
        void Check(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
            checks.Add(message);
        }
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var invocations = 0;
        StartBundledPackCheck(async token =>
        {
            Interlocked.Increment(ref invocations);
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            await services.Packs.ImportAsync(archive, token);
            return new(1, 0, []);
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var watch = Stopwatch.StartNew();
        await RunAsync(token => BeginNewProjectAsync(token));
        var emptyMilliseconds = watch.Elapsed.TotalMilliseconds;
        Check(pendingOperation.IsCompleted && !bundledPackTask.IsCompleted && installedPacks.Length == 0,
            "empty catalog page finishes while bundled import is blocked");
        release.TrySetResult();
        await bundledPackTask.WaitAsync(TimeSpan.FromSeconds(10));
        Check(VendorPicker.IsEnabled && installedPacks.Length == 1,
            "first bundled import refreshes empty selector without reopening page");
        await RunAsync(token => BeginNewProjectAsync(token));
        Check(invocations == 1, "reopening page does not repeat completed bundled check");

        bundledPacksChecked = false;
        release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        StartBundledPackCheck(async token =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return new(1, 0, []);
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        watch.Restart();
        await RunAsync(token => BeginNewProjectAsync(token));
        var readyMilliseconds = watch.Elapsed.TotalMilliseconds;
        Check(pendingOperation.IsCompleted && !bundledPackTask.IsCompleted && VendorPicker.IsEnabled,
            "installed devices are usable while bundled verification is blocked");
        var pack = installedPacks.Single();
        SelectPack(pack);
        var device = pack.Manifest.Devices[0];
        DeviceSearch.Text = device.Id;
        DevicePicker.SelectedItem = device;
        var template = device.Templates[0];
        TemplatePicker.SelectedItem = template;
        ProjectName.Text = "catalog_selection_fixture";
        release.TrySetResult();
        await bundledPackTask.WaitAsync(TimeSpan.FromSeconds(10));
        Check(PackPicker.SelectedItem is InstalledPack selected && selected.Manifest.Id == pack.Manifest.Id &&
            selected.Manifest.Version == pack.Manifest.Version && DevicePicker.SelectedItem is DeviceDefinition selectedDevice &&
            selectedDevice.Id == device.Id && TemplatePicker.SelectedItem is ProjectTemplate selectedTemplate &&
            selectedTemplate.Id == template.Id && DeviceSearch.Text == device.Id && ProjectName.Text == "catalog_selection_fixture",
            "background completion preserves current device, template, search and project name");

        bundledPacksChecked = false;
        entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        StartBundledPackCheck(async token =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return new(0, 0, []);
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var selectedBefore = PackPicker.SelectedItem;
        await StopBundledPackCheckAsync();
        Check(bundledPackCancellation is null && !bundledPacksChecked && ReferenceEquals(PackPicker.SelectedItem, selectedBefore),
            "shutdown cancellation joins background check and preserves selection; incomplete check remains retryable");
        StartBundledPackCheck(_ => throw new InvalidOperationException("catalog fixture original failure"));
        await bundledPackTask.WaitAsync(TimeSpan.FromSeconds(10));
        Check(LocalPackStatus.Text.Contains("失败", StringComparison.Ordinal) &&
            BuildLog.Text.Contains("catalog fixture original failure", StringComparison.Ordinal) &&
            ReferenceEquals(PackPicker.SelectedItem, selectedBefore), "background failure retains raw diagnostic and usable selection");
        bundledPacksChecked = true;
        await JsonStore.WriteAsync(Path.Combine(directory, "loading-result.json"), new
        {
            success = true,
            emptyMilliseconds,
            readyMilliseconds,
            checks,
            hardware = false,
            isolatedData = true,
            blockedBackground = true
        });
    }
}
