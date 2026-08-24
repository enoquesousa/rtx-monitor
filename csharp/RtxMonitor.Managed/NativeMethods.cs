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

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
internal struct NativeBoardIdentity
{
    internal uint StructSize;
    internal uint GpuIndex;
    internal uint PciVendorId;
    internal uint PciDeviceId;
    internal uint PciSubsystemVendorId;
    internal uint PciSubsystemDeviceId;
    internal uint PciDomain;
    internal uint PciBus;
    internal uint PciDevice;
    internal uint PciFunction;
    internal uint Flags;
    internal uint Reserved;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = NativeMethods.TextCapacity)]
    internal string PciBusId;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = NativeMethods.TextCapacity)]
    internal string VbiosVersion;

    internal static NativeBoardIdentity Create() => new()
    {
        StructSize = checked((uint)Marshal.SizeOf<NativeBoardIdentity>()),
        PciBusId = string.Empty,
        VbiosVersion = string.Empty,
    };
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeThermalProviderResult
{
    internal uint Provider;
    internal uint State;
    internal int NativeStatus;
    internal uint CapabilityCount;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeThermalCapability
{
    internal uint Provider;
    internal uint Target;
    internal uint Controller;
    internal uint State;
    internal uint Confidence;
    internal uint ValueFlags;
    internal int CurrentTemperatureC;
    internal int DefaultMinimumTemperatureC;
    internal int DefaultMaximumTemperatureC;
    internal int NativeStatus;
    internal uint ProviderNativeId;
    internal uint Reserved;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeThermalReport
{
    internal uint StructSize;
    internal uint GpuIndex;
    internal uint ProviderCount;
    internal uint CapabilityCount;
    internal ulong TimestampUnixMilliseconds;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = NativeMethods.MaxThermalProviders)]
    internal NativeThermalProviderResult[] Providers;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = NativeMethods.MaxThermalCapabilities)]
    internal NativeThermalCapability[] Capabilities;

    internal static NativeThermalReport Create() => new()
    {
        StructSize = checked((uint)Marshal.SizeOf<NativeThermalReport>()),
        Providers = new NativeThermalProviderResult[NativeMethods.MaxThermalProviders],
        Capabilities = new NativeThermalCapability[NativeMethods.MaxThermalCapabilities],
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
    internal const int MaxThermalProviders = 3;
    internal const int MaxThermalCapabilities = 8;
    internal const uint AbiVersion = 2;
    internal const uint ThermalValueCurrentValid = 1U << 0;
    internal const uint ThermalValueDefaultMinimumValid = 1U << 1;
    internal const uint ThermalValueDefaultMaximumValid = 1U << 2;
    private const string Library = "rtxmon_native";

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern uint rtxmon_abi_version();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr rtxmon_status_string(NativeStatus status);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr rtxmon_temperature_backend_string(uint backend);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr rtxmon_thermal_provider_string(uint provider);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr rtxmon_capability_state_string(uint state);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr rtxmon_thermal_target_string(uint target);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr rtxmon_thermal_controller_string(uint controller);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr rtxmon_sensor_confidence_string(uint confidence);

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

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    internal static extern NativeStatus rtxmon_get_board_identity(
        SafeRtxmonContext context,
        uint gpuIndex,
        ref NativeBoardIdentity identity);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern NativeStatus rtxmon_scan_thermal_capabilities(
        SafeRtxmonContext context,
        uint gpuIndex,
        ref NativeThermalReport report);

    internal static void ContextDestroy(IntPtr context) => rtxmon_context_destroy(context);

    internal static string StatusString(NativeStatus status) =>
        Marshal.PtrToStringUTF8(rtxmon_status_string(status)) ?? "unknown status";

    internal static string BackendString(uint backend) =>
        Marshal.PtrToStringUTF8(rtxmon_temperature_backend_string(backend)) ?? "unknown backend";

    internal static string ProviderString(uint provider) =>
        Marshal.PtrToStringUTF8(rtxmon_thermal_provider_string(provider)) ?? "unknown_provider";

    internal static string CapabilityStateString(uint state) =>
        Marshal.PtrToStringUTF8(rtxmon_capability_state_string(state)) ?? "unknown";

    internal static string ThermalTargetString(uint target) =>
        Marshal.PtrToStringUTF8(rtxmon_thermal_target_string(target)) ?? "unknown";

    internal static string ThermalControllerString(uint controller) =>
        Marshal.PtrToStringUTF8(rtxmon_thermal_controller_string(controller)) ?? "unknown";

    internal static string SensorConfidenceString(uint confidence) =>
        Marshal.PtrToStringUTF8(rtxmon_sensor_confidence_string(confidence)) ?? "unknown";

    internal static string LastError() =>
        Marshal.PtrToStringUTF8(rtxmon_last_error()) ?? string.Empty;
}
