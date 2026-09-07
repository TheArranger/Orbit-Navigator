using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using OrbitNavigator.Contracts.Common;

namespace OrbitNavigator.Foundation.Resources;

public sealed class WindowsResourceMeasurementSource : IWindowsResourceMeasurementSource
{
    public ControllerResult<SystemResourceMeasurement> ReadSystem()
    {
        var memory = new MemoryStatusEx();
        if (!GlobalMemoryStatusEx(ref memory) ||
            !GetSystemTimes(out var idle, out var kernel, out var user))
        {
            return ControllerResult<SystemResourceMeasurement>.Failure(Unavailable());
        }

        return ControllerResult<SystemResourceMeasurement>.Success(new(
            memory.TotalPhysical,
            memory.AvailablePhysical,
            idle.ToUInt64(),
            kernel.ToUInt64(),
            user.ToUInt64()));
    }

    public ControllerResult<ProcessResourceMeasurement> ReadProcess(int processId)
    {
        if (processId <= 0)
        {
            return ControllerResult<ProcessResourceMeasurement>.Failure(Invalid());
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            return ControllerResult<ProcessResourceMeasurement>.Success(new(
                processId,
                process.PrivateMemorySize64,
                process.TotalProcessorTime));
        }
        catch (Exception exception) when (exception is
            ArgumentException or
            InvalidOperationException or
            NotSupportedException or
            SystemException or
            Win32Exception)
        {
            return ControllerResult<ProcessResourceMeasurement>.Failure(Unavailable());
        }
    }

    private static ControllerError Invalid() =>
        ControllerError.Create(ControllerErrorCode.InvalidRequest, "error.resource.process_invalid");

    private static ControllerError Unavailable() =>
        ControllerError.Create(ControllerErrorCode.Unavailable, "error.resource.measurement_unavailable");

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(
        out FileTime idleTime,
        out FileTime kernelTime,
        out FileTime userTime);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        private uint Length;
        private uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        private ulong TotalPageFile;
        private ulong AvailablePageFile;
        private ulong TotalVirtual;
        private ulong AvailableVirtual;
        private ulong AvailableExtendedVirtual;

        public MemoryStatusEx()
        {
            Length = (uint)Marshal.SizeOf<MemoryStatusEx>();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct FileTime
    {
        private readonly uint Low;
        private readonly uint High;

        public ulong ToUInt64() => ((ulong)High << 32) | Low;
    }
}

public sealed class StopwatchTimestampSource : IMonotonicTimestampSource
{
    public long GetTimestamp() => Stopwatch.GetTimestamp();

    public long Frequency => Stopwatch.Frequency;
}
