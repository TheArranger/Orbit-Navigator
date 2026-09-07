using System.Runtime.InteropServices;

namespace OrbitNavigator.App;

internal static class WindowsAppIdentity
{
    internal const string AppUserModelId = "OrbitNavigator.Desktop";
    internal static string? AppliedAppUserModelId { get; private set; }

    internal static void ApplyCurrentProcessIdentity()
    {
        var result = SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
        if (result < 0)
        {
            Marshal.ThrowExceptionForHR(result);
        }

        result = GetCurrentProcessExplicitAppUserModelID(out var value);
        if (result < 0)
        {
            Marshal.ThrowExceptionForHR(result);
        }
        try
        {
            AppliedAppUserModelId = Marshal.PtrToStringUni(value);
        }
        finally
        {
            Marshal.FreeCoTaskMem(value);
        }
        if (!string.Equals(AppliedAppUserModelId, AppUserModelId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The Windows application identity was not applied.");
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    [DllImport("shell32.dll")]
    private static extern int GetCurrentProcessExplicitAppUserModelID(out nint appId);
}
