using System.Diagnostics;
using System.Runtime.InteropServices;
using OrbitNavigator.Contracts.Common;

namespace OrbitNavigator.Updates;

public sealed class WindowsAuthenticodeTrustInspector : IUpdatePublisherTrustInspector
{
    private const uint WtdUiNone = 2;
    private const uint WtdRevokeNone = 0;
    private const uint WtdChoiceFile = 1;
    private const uint WtdStateActionIgnore = 0;
    private const uint WtdCacheOnlyUrlRetrieval = 0x1000;
    private const int TrustENoSignature = unchecked((int)0x800B0100);
    private const int TrustEProviderUnknown = unchecked((int)0x800B0001);
    private const int TrustESubjectFormUnknown = unchecked((int)0x800B0003);
    private static readonly Guid ActionGenericVerifyV2 =
        new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    public ValueTask<UpdatePublisherTrust> InspectAsync(
        string absolutePackagePath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(absolutePackagePath) || !File.Exists(absolutePackagePath))
            return ValueTask.FromResult(UpdatePublisherTrust.VerificationUnavailable);

        var fileInfo = new WinTrustFileInfo(absolutePackagePath);
        var filePointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(fileInfo, filePointer, false);
            var data = new WinTrustData(filePointer);
            var action = ActionGenericVerifyV2;
            var status = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            return ValueTask.FromResult(status switch
            {
                0 => UpdatePublisherTrust.UnexpectedPublisher,
                TrustENoSignature or TrustEProviderUnknown or TrustESubjectFormUnknown =>
                    UpdatePublisherTrust.Unsigned,
                _ => UpdatePublisherTrust.InvalidSignature,
            });
        }
        catch
        {
            return ValueTask.FromResult(UpdatePublisherTrust.VerificationUnavailable);
        }
        finally
        {
            Marshal.DestroyStructure<WinTrustFileInfo>(filePointer);
            Marshal.FreeHGlobal(filePointer);
        }
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true)]
    private static extern int WinVerifyTrust(
        IntPtr window,
        [In] ref Guid actionId,
        [In] ref WinTrustData data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public WinTrustFileInfo(string path)
        {
            StructureSize = checked((uint)Marshal.SizeOf<WinTrustFileInfo>());
            FilePath = path;
            FileHandle = IntPtr.Zero;
            KnownSubject = IntPtr.Zero;
        }

        public uint StructureSize;
        [MarshalAs(UnmanagedType.LPWStr)] public string FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        public WinTrustData(IntPtr fileInfo)
        {
            StructureSize = checked((uint)Marshal.SizeOf<WinTrustData>());
            PolicyCallbackData = IntPtr.Zero;
            SipClientData = IntPtr.Zero;
            UiChoice = WtdUiNone;
            RevocationChecks = WtdRevokeNone;
            UnionChoice = WtdChoiceFile;
            FileInfo = fileInfo;
            StateAction = WtdStateActionIgnore;
            StateData = IntPtr.Zero;
            UrlReference = IntPtr.Zero;
            ProviderFlags = WtdCacheOnlyUrlRetrieval;
            UiContext = 0;
            SignatureSettings = IntPtr.Zero;
        }

        public uint StructureSize;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfo;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }
}

public sealed class VisibleUpdateInstallerLauncher : IVisibleUpdateInstallerLauncher
{
    public ValueTask<ControllerResult> LaunchVisibleAsync(
        StagedUpdatePackage package,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = package.AbsolutePath,
                WorkingDirectory = Path.GetDirectoryName(package.AbsolutePath)!,
                UseShellExecute = true,
                // Deliberately no silent arguments. Windows and Setup remain visible.
            });
            return ValueTask.FromResult(ControllerResult.Success());
        }
        catch
        {
            return ValueTask.FromResult(ControllerResult.Failure(ControllerError.Create(
                ControllerErrorCode.Unavailable,
                "error.update.installer_launch_failed")));
        }
    }
}
