using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using System;
using System.IO.Pipes;
using System.Runtime.InteropServices;

namespace BrainPending;

class Program
{
    // Per user, so two people signed in to one computer each get their own copy.
    private static readonly string InstanceName = "BrainPending-" + Environment.UserName;

    // Avalonia and SynchronizationContext-dependent APIs are unavailable until AppMain starts.
    [STAThread]
    public static void Main(string[] args)
    {
        // Held for the whole run, so a later copy can tell one is already open.
        using var running = new Mutex(false, InstanceName, out var first);
        if (!first && !BrainSettings.Read().AllowMultipleCopies && ShowOpenCopy()) return;
        ListenForCopies();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    // False when the open copy doesn't answer, so this one starts instead of doing nothing.
    private static bool ShowOpenCopy()
    {
        try
        {
            // Lets the open copy take the foreground; Windows only allows that with this process's say-so.
            if (OperatingSystem.IsWindows()) AllowSetForegroundWindow(-1);
            using var pipe = new NamedPipeClientStream(".", InstanceName, PipeDirection.Out);
            // Connecting is the whole message; the open copy hangs up straight away.
            pipe.Connect(2000);
            return true;
        }
        catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException) { return false; }
    }

    // Every copy listens, so turning the setting off works without closing extra copies first.
    private static void ListenForCopies() => new Thread(() =>
    {
        while (true)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(InstanceName, PipeDirection.In,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.CurrentUserOnly);
                pipe.WaitForConnection();
                Dispatcher.UIThread.Post(ShowMainWindow);
            }
            catch (IOException) { return; }
        }
    }) { IsBackground = true }.Start();

    private static void ShowMainWindow()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } window }) return;
        if (window.WindowState == WindowState.Minimized)
        {
            // Windows puts a minimized window back the way it was, maximized or not.
            if (OperatingSystem.IsWindows() && window.TryGetPlatformHandle() is { HandleDescriptor: "HWND" } handle)
                ShowWindow(handle.Handle, SwRestore);
            else
                window.WindowState = WindowState.Normal;
        }
        window.Activate();
    }

    private const int SwRestore = 9;

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);
}
