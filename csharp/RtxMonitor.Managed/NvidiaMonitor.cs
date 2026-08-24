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
        if (gpuInfoSize != 392 || sampleSize != 32)
        {
            throw new InvalidOperationException(
                $"Layout P/Invoke incompatível: gpu_info={gpuInfoSize}, sample={sampleSize}.");
        }
    }

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
