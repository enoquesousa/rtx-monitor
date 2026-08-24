using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RtxMonitor.Managed;

internal enum NativeStatus
{
    Ok = 0,
    InvalidArgument = 1,
    OutOfMemory = 2,
    BackendNotFound = 3,
    BackendSymbolMissing = 4,
    DriverNotLoaded = 5,
    NoPermission = 6,
    GpuNotFound = 7,
    NotSupported = 8,
    GpuLost = 9,
    BackendError = 10,
    AbiMismatch = 11,
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
internal struct NativeGpuInfo
{
    internal uint StructSize;
    internal uint Index;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = NativeMethods.TextCapacity)]
    internal string Name;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = NativeMethods.TextCapacity)]
    internal string Uuid;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = NativeMethods.TextCapacity)]
    internal string DriverVersion;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = NativeMethods.TextCapacity)]
    internal string NvmlVersion;

    internal static NativeGpuInfo Create() => new()
    {
        StructSize = checked((uint)Marshal.SizeOf<NativeGpuInfo>()),
        Name = string.Empty,
        Uuid = string.Empty,
        DriverVersion = string.Empty,
        NvmlVersion = string.Empty,
    };
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeTemperatureSample
{
    internal uint StructSize;
    internal uint GpuIndex;
    internal int TemperatureC;
    internal uint SensorKind;
    internal uint Backend;
    internal uint Reserved;
    internal ulong TimestampUnixMilliseconds;

    internal static NativeTemperatureSample Create() => new()
    {
        StructSize = checked((uint)Marshal.SizeOf<NativeTemperatureSample>()),
    };
}

internal sealed class SafeRtxmonContext : SafeHandleZeroOrMinusOneIsInvalid
{
    internal SafeRtxmonContext(IntPtr handle)
        : base(ownsHandle: true)
    {
        SetHandle(handle);
    }

    protected override bool ReleaseHandle()
    {
        NativeMethods.ContextDestroy(handle);
        return true;
    }
}

internal static class NativeMethods
{
    internal const int TextCapacity = 96;
    internal const uint AbiVersion = 1;
    private const string Library = "rtxmon_native";

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern uint rtxmon_abi_version();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr rtxmon_status_string(NativeStatus status);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr rtxmon_temperature_backend_string(uint backend);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr rtxmon_last_error();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern NativeStatus rtxmon_context_create(out IntPtr context);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void rtxmon_context_destroy(IntPtr context);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern NativeStatus rtxmon_get_gpu_count(
        SafeRtxmonContext context,
        out uint count);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    internal static extern NativeStatus rtxmon_get_gpu_info(
        SafeRtxmonContext context,
        uint gpuIndex,
        ref NativeGpuInfo info);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern NativeStatus rtxmon_read_gpu_die_temperature(
        SafeRtxmonContext context,
        uint gpuIndex,
        ref NativeTemperatureSample sample);

    internal static void ContextDestroy(IntPtr context) => rtxmon_context_destroy(context);

    internal static string StatusString(NativeStatus status) =>
        Marshal.PtrToStringUTF8(rtxmon_status_string(status)) ?? "unknown status";

    internal static string BackendString(uint backend) =>
        Marshal.PtrToStringUTF8(rtxmon_temperature_backend_string(backend)) ?? "unknown backend";

    internal static string LastError() =>
        Marshal.PtrToStringUTF8(rtxmon_last_error()) ?? string.Empty;
}
