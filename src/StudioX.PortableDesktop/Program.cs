namespace StudioX.PortableDesktop;

using Avalonia;

internal static class Program
{
    internal static StartupOptions Options { get; private set; } = null!;

    [STAThread]
    public static int Main(string[] arguments)
    {
        try
        {
            Options = StartupOptions.Parse(arguments);
            return AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace().StartWithClassicDesktopLifetime([]);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
