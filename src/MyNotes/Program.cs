using Avalonia;
using System;

namespace MyNotes;

class Program
{
    // Avalonia and SynchronizationContext-dependent APIs are unavailable until AppMain starts.
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
