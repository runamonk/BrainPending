using System.Runtime.InteropServices;

namespace BrainPending;

internal static class MotionSettings
{
    public static bool AnimationsEnabled => !OperatingSystem.IsWindows() ||
        !SystemParametersInfo(0x1042, 0, out var enabled, 0) || enabled;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint parameter,
        [MarshalAs(UnmanagedType.Bool)] out bool value, uint flags);
}
