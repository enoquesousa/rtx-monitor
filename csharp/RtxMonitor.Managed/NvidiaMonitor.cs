using System.Runtime.InteropServices;

namespace RtxMonitor.Managed;

public sealed class NvidiaMonitor : IDisposable
{
    private readonly SafeRtxmonContext context;
    private bool disposed;

    private NvidiaMonitor(SafeRtxmonContext context)
    {
        this.context = context;
    }

    public static NvidiaMonitor Open()
    {
        VerifyManagedAbi();

        NativeStatus status = NativeMethods.rtxmon_context_create(out IntPtr nativeContext);
        if (status != NativeStatus.Ok)
        {
            Throw(status, "Não foi possível inicializar o monitor NVIDIA");
        }

        return new NvidiaMonitor(new SafeRtxmonContext(nativeContext));
    }

    public IReadOnlyList<GpuInfo> GetGpus()
    {
        ThrowIfDisposed();
        NativeStatus status = NativeMethods.rtxmon_get_gpu_count(context, out uint count);
        if (status != NativeStatus.Ok)
        {
            Throw(status, "Não foi possível enumerar as GPUs NVIDIA");
        }

        var gpus = new List<GpuInfo>(checked((int)count));
        for (uint index = 0; index < count; index++)
        {
            gpus.Add(GetGpu(index));
        }

        return gpus;
    }

    public GpuInfo GetGpu(uint index)
    {
        ThrowIfDisposed();
        NativeGpuInfo native = NativeGpuInfo.Create();
        NativeStatus status = NativeMethods.rtxmon_get_gpu_info(context, index, ref native);
        if (status != NativeStatus.Ok)
        {
            Throw(status, $"Não foi possível obter os dados da GPU {index}");
        }

        return new GpuInfo(
            native.Index,
            native.Name,
            native.Uuid,
            native.DriverVersion,
            native.NvmlVersion);
    }

    public TemperatureSample ReadGpuDieTemperature(uint index)
    {
        ThrowIfDisposed();
        NativeTemperatureSample native = NativeTemperatureSample.Create();
        NativeStatus status = NativeMethods.rtxmon_read_gpu_die_temperature(
            context,
            index,
            ref native);

        if (status != NativeStatus.Ok)
        {
            Throw(status, $"Não foi possível ler o sensor do die da GPU {index}");
        }

        return new TemperatureSample(
            native.GpuIndex,
            native.TemperatureC,
            (TemperatureBackend)native.Backend,
            NativeMethods.BackendString(native.Backend),
            DateTimeOffset.FromUnixTimeMilliseconds(checked((long)native.TimestampUnixMilliseconds)),
            native.TimestampUnixMilliseconds);
    }

    public BoardIdentity GetBoardIdentity(uint index)
    {
        ThrowIfDisposed();
        NativeBoardIdentity native = NativeBoardIdentity.Create();
        NativeStatus status = NativeMethods.rtxmon_get_board_identity(context, index, ref native);
        if (status != NativeStatus.Ok)
        {
            Throw(status, $"Não foi possível obter a identidade da placa da GPU {index}");
        }

        return new BoardIdentity(
            native.GpuIndex,
            native.PciVendorId,
            native.PciDeviceId,
            native.PciSubsystemVendorId,
            native.PciSubsystemDeviceId,
            native.PciDomain,
            native.PciBus,
            native.PciDevice,
            native.PciFunction,
            (BoardIdentityFlags)native.Flags,
            native.PciBusId,
            native.VbiosVersion);
    }

    public ThermalReport ScanThermalCapabilities(uint index)
    {
        ThrowIfDisposed();
        NativeThermalReport native = NativeThermalReport.Create();
        NativeStatus status = NativeMethods.rtxmon_scan_thermal_capabilities(context, index, ref native);
        if (status != NativeStatus.Ok)
        {
            Throw(status, $"Não foi possível inventariar as capacidades térmicas da GPU {index}");
        }

        if (native.ProviderCount > NativeMethods.MaxThermalProviders ||
            native.CapabilityCount > NativeMethods.MaxThermalCapabilities)
        {
            throw new InvalidOperationException(
                $"Relatório nativo excedeu os limites da ABI: providers={native.ProviderCount}, " +
                $"capabilities={native.CapabilityCount}.");
        }

        var providers = new List<ThermalProviderResult>(checked((int)native.ProviderCount));
        for (int providerIndex = 0; providerIndex < native.ProviderCount; providerIndex++)
        {
            NativeThermalProviderResult provider = native.Providers[providerIndex];
            providers.Add(new ThermalProviderResult(
                (ThermalProvider)provider.Provider,
                NativeMethods.ProviderString(provider.Provider),
                (CapabilityState)provider.State,
                NativeMethods.CapabilityStateString(provider.State),
                provider.NativeStatus,
                provider.CapabilityCount));
        }

        var capabilities = new List<ThermalCapability>(checked((int)native.CapabilityCount));
        for (int capabilityIndex = 0; capabilityIndex < native.CapabilityCount; capabilityIndex++)
        {
            NativeThermalCapability capability = native.Capabilities[capabilityIndex];
            capabilities.Add(new ThermalCapability(
                (ThermalProvider)capability.Provider,
                NativeMethods.ProviderString(capability.Provider),
                (ThermalTarget)capability.Target,
                NativeMethods.ThermalTargetString(capability.Target),
                (ThermalController)capability.Controller,
                NativeMethods.ThermalControllerString(capability.Controller),
                (CapabilityState)capability.State,
                NativeMethods.CapabilityStateString(capability.State),
                (SensorConfidence)capability.Confidence,
                NativeMethods.SensorConfidenceString(capability.Confidence),
                HasFlag(capability.ValueFlags, NativeMethods.ThermalValueCurrentValid)
                    ? capability.CurrentTemperatureC
                    : null,
                HasFlag(capability.ValueFlags, NativeMethods.ThermalValueDefaultMinimumValid)
                    ? capability.DefaultMinimumTemperatureC
                    : null,
                HasFlag(capability.ValueFlags, NativeMethods.ThermalValueDefaultMaximumValid)
                    ? capability.DefaultMaximumTemperatureC
                    : null,
                capability.NativeStatus,
                capability.ProviderNativeId));
        }

        return new ThermalReport(
            native.GpuIndex,
            DateTimeOffset.FromUnixTimeMilliseconds(checked((long)native.TimestampUnixMilliseconds)),
            native.TimestampUnixMilliseconds,
            providers,
            capabilities);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        context.Dispose();
        disposed = true;
        GC.SuppressFinalize(this);
    }

    private static void VerifyManagedAbi()
    {
        uint nativeAbi = NativeMethods.rtxmon_abi_version();
        if (nativeAbi != NativeMethods.AbiVersion)
        {
            throw new InvalidOperationException(
                $"ABI incompatível: C# espera {NativeMethods.AbiVersion}, biblioteca nativa expõe {nativeAbi}.");
        }

        int gpuInfoSize = Marshal.SizeOf<NativeGpuInfo>();
        int sampleSize = Marshal.SizeOf<NativeTemperatureSample>();
        int boardIdentitySize = Marshal.SizeOf<NativeBoardIdentity>();
        int providerSize = Marshal.SizeOf<NativeThermalProviderResult>();
        int capabilitySize = Marshal.SizeOf<NativeThermalCapability>();
        int reportSize = Marshal.SizeOf<NativeThermalReport>();
        if (gpuInfoSize != 392 || sampleSize != 32 || boardIdentitySize != 240 ||
            providerSize != 16 || capabilitySize != 48 || reportSize != 456)
        {
            throw new InvalidOperationException(
                $"Layout P/Invoke incompatível: gpu_info={gpuInfoSize}, sample={sampleSize}, " +
                $"board={boardIdentitySize}, provider={providerSize}, capability={capabilitySize}, report={reportSize}.");
        }
    }

    private static bool HasFlag(uint value, uint flag) => (value & flag) != 0;

    private static void Throw(NativeStatus status, string operation)
    {
        string diagnostic = NativeMethods.LastError();
        string message = $"{operation}: {NativeMethods.StatusString(status)}";
        if (!string.IsNullOrWhiteSpace(diagnostic))
        {
            message += $" ({diagnostic})";
        }

        throw new RtxMonitorException(status, message);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }
}
