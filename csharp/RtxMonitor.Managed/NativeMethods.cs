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

[StructLayout(LayoutKind.Sequential)]
internal struct NativePrivateThermalSample
{
    internal uint StructSize;
    internal uint GpuIndex;
    internal uint ValueFlags;
    internal int NativeStatus;
    internal int GpuDieTemperatureMillic;
    internal int GpuHotspotTemperatureMillic;
    internal int Reserved;
    internal ulong TimestampUnixMilliseconds;

    internal static NativePrivateThermalSample Create() => new()
    {
        StructSize = checked((uint)Marshal.SizeOf<NativePrivateThermalSample>()),
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

[StructLayout(LayoutKind.Sequential)]
internal struct NativePublicTelemetryValue
{
    internal uint Field;
    internal uint Provider;
    internal uint State;
    internal uint Origin;
    internal uint ValueType;
    internal uint Unit;
    internal int NativeStatus;
    internal uint ProviderNativeId;
    internal ulong ValueU64;
    internal long ValueI64;
    internal double ValueF64;
    internal ulong TimestampUnixMilliseconds;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativePublicTelemetryReport
{
    internal uint StructSize;
    internal uint GpuIndex;
    internal uint FieldCount;
    internal uint Reserved;
    internal ulong TimestampUnixMilliseconds;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = NativeMethods.MaxPublicFields)]
    internal NativePublicTelemetryValue[] Fields;

    internal static NativePublicTelemetryReport Create() => new()
    {
        StructSize = checked((uint)Marshal.SizeOf<NativePublicTelemetryReport>()),
        Fields = new NativePublicTelemetryValue[NativeMethods.MaxPublicFields],
    };
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeComputedMetricOptions
{
    internal uint StructSize;
    internal uint WindowMilliseconds;
    internal int TemperatureThresholdC;
    internal uint MaximumSamples;

    internal static NativeComputedMetricOptions Create(ComputedMetricOptions options) => new()
    {
        StructSize = checked((uint)Marshal.SizeOf<NativeComputedMetricOptions>()),
        WindowMilliseconds = options.WindowMilliseconds,
        TemperatureThresholdC = options.TemperatureThresholdC,
        MaximumSamples = options.MaximumSamples,
    };
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeComputedMetric
{
    internal uint Metric;
    internal uint State;
    internal uint Origin;
    internal uint Unit;
    internal double Value;
    internal ulong TimestampUnixMilliseconds;
    internal ulong WindowMilliseconds;
    internal uint SampleCount;
    internal uint InputCount;
    internal int TemperatureThresholdC;
    internal uint Reserved;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = NativeMethods.MaxMetricInputs)]
    internal uint[] InputFields;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeComputedMetricsReport
{
    internal uint StructSize;
    internal uint GpuIndex;
    internal uint MetricCount;
    internal uint Reserved;
    internal ulong TimestampUnixMilliseconds;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = NativeMethods.MaxComputedMetrics)]
    internal NativeComputedMetric[] Metrics;

    internal static NativeComputedMetricsReport Create() => new()
    {
        StructSize = checked((uint)Marshal.SizeOf<NativeComputedMetricsReport>()),
        Metrics = Enumerable.Range(0, NativeMethods.MaxComputedMetrics)
            .Select(_ => new NativeComputedMetric
            {
                InputFields = new uint[NativeMethods.MaxMetricInputs],
            })
            .ToArray(),
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

internal sealed class SafeMetricsContext : SafeHandleZeroOrMinusOneIsInvalid
{
    internal SafeMetricsContext(IntPtr handle)
        : base(ownsHandle: true)
    {
        SetHandle(handle);
    }

    protected override bool ReleaseHandle()
    {
        NativeMethods.MetricsContextDestroy(handle);
        return true;
    }
}

internal static class NativeMethods
{
    internal const int TextCapacity = 96;
    internal const int MaxThermalProviders = 3;
    internal const int MaxThermalCapabilities = 8;
    internal const int MaxPublicFields = 48;
    internal const int MaxComputedMetrics = 4;
    internal const int MaxMetricInputs = 2;
    internal const uint AbiVersion = 4;
    internal const uint PrivateThermalDieValid = 1U << 0;
    internal const uint PrivateThermalHotspotValid = 1U << 1;
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
    private static extern IntPtr rtxmon_data_origin_string(uint origin);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr rtxmon_public_field_string(uint field);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr rtxmon_public_provider_string(uint provider);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr rtxmon_value_type_string(uint valueType);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr rtxmon_unit_string(uint unit);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr rtxmon_computed_metric_string(uint metric);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr rtxmon_computed_metric_formula(uint metric);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr rtxmon_metric_state_string(uint state);

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

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern NativeStatus rtxmon_read_private_thermal_channels(
        SafeRtxmonContext context,
        uint gpuIndex,
        ref NativePrivateThermalSample sample);

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

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern NativeStatus rtxmon_read_public_telemetry(
        SafeRtxmonContext context,
        uint gpuIndex,
        ref NativePublicTelemetryReport report);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern NativeStatus rtxmon_metrics_context_create(
        in NativeComputedMetricOptions options,
        out IntPtr context);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void rtxmon_metrics_context_destroy(IntPtr context);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void rtxmon_metrics_context_reset(SafeMetricsContext context);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern NativeStatus rtxmon_metrics_observe(
        SafeMetricsContext context,
        in NativePublicTelemetryReport telemetry,
        ref NativeComputedMetricsReport report);

    internal static void ContextDestroy(IntPtr context) => rtxmon_context_destroy(context);

    internal static void MetricsContextDestroy(IntPtr context) =>
        rtxmon_metrics_context_destroy(context);

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

    internal static string DataOriginString(uint origin) =>
        Marshal.PtrToStringUTF8(rtxmon_data_origin_string(origin)) ?? "unknown";

    internal static string PublicFieldString(uint field) =>
        Marshal.PtrToStringUTF8(rtxmon_public_field_string(field)) ?? "unknown_public_field";

    internal static string PublicProviderString(uint provider) =>
        Marshal.PtrToStringUTF8(rtxmon_public_provider_string(provider)) ?? "unknown_public_provider";

    internal static string ValueTypeString(uint valueType) =>
        Marshal.PtrToStringUTF8(rtxmon_value_type_string(valueType)) ?? "unknown";

    internal static string UnitString(uint unit) =>
        Marshal.PtrToStringUTF8(rtxmon_unit_string(unit)) ?? "unknown";

    internal static string ComputedMetricString(uint metric) =>
        Marshal.PtrToStringUTF8(rtxmon_computed_metric_string(metric)) ?? "unknown_computed_metric";

    internal static string ComputedMetricFormula(uint metric) =>
        Marshal.PtrToStringUTF8(rtxmon_computed_metric_formula(metric)) ?? "unknown";

    internal static string MetricStateString(uint state) =>
        Marshal.PtrToStringUTF8(rtxmon_metric_state_string(state)) ?? "unknown";

    internal static string LastError() =>
        Marshal.PtrToStringUTF8(rtxmon_last_error()) ?? string.Empty;
}
