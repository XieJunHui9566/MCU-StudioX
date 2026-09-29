namespace StudioX.Application.Editing;

using StudioX.Foundation;

public sealed class WorkbenchLayoutService(string dataDirectory)
{
    private readonly string path = Path.Combine(dataDirectory, "workbench-layout.json");
    public async Task<WorkbenchLayout> LoadAsync(CancellationToken token = default) => File.Exists(path)
        ? Normalize(await JsonStore.ReadAsync<WorkbenchLayout>(path, token).ConfigureAwait(false)) : new();
    public Task SaveAsync(WorkbenchLayout layout, CancellationToken token = default) => JsonStore.WriteAsync(path, Normalize(layout), token);
    private static WorkbenchLayout Normalize(WorkbenchLayout layout)
    {
        double Range(double value, double min, double max, double fallback) => double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
        return layout with
        {
            Width = Range(layout.Width, 1000, 5000, 1460),
            Height = Range(layout.Height, 650, 3000, 920),
            ProjectWidth = Range(layout.ProjectWidth, 180, 600, 230),
            BottomHeight = Range(layout.BottomHeight, 100, 800, 150),
            SplitRatio = Range(layout.SplitRatio, .2, .8, .5)
        };
    }
}
