using RtxMonitor.Managed;
using RtxMonitor.Storage;

namespace RtxMonitor.Service;

public interface ITelemetrySampler : IDisposable
{
    IReadOnlyList<TelemetryEvent> Poll();

    uint NextDelayMilliseconds(uint successfulSampleIntervalMilliseconds);
}

public interface IMonitoringBackend
{
    IReadOnlyList<DiscoveredGpu> Discover();

    GpuEvidenceSnapshot CaptureEvidence(GpuInfo gpu);

    ITelemetrySampler CreateSampler(string gpuUuid, SamplingOptions options);
}

public sealed class NvidiaMonitoringBackend : IMonitoringBackend
{
    public IReadOnlyList<DiscoveredGpu> Discover()
    {
        using NvidiaMonitor monitor = NvidiaMonitor.Open();
        IReadOnlyList<GpuInfo> gpus = monitor.GetGpus();
        var discovered = new List<DiscoveredGpu>(gpus.Count);
        foreach (GpuInfo gpu in gpus)
        {
            DateTimeOffset capturedAt = DateTimeOffset.UtcNow;
            GpuEvidenceSnapshot evidence = CaptureEvidence(monitor, gpu, capturedAt);
            ThermalReport? report = null;
            string? thermalError = null;
            try
            {
                report = monitor.ScanThermalCapabilities(gpu.Index);
            }
            catch (Exception error) when (IsProviderFailure(error))
            {
                thermalError = error.Message;
            }

            discovered.Add(new DiscoveredGpu(
                gpu,
                evidence,
                report,
                thermalError,
                capturedAt));
        }

        return discovered;
    }

    public GpuEvidenceSnapshot CaptureEvidence(GpuInfo gpu)
    {
        ArgumentNullException.ThrowIfNull(gpu);
        DateTimeOffset capturedAt = DateTimeOffset.UtcNow;
        try
        {
            using NvidiaMonitor monitor = NvidiaMonitor.Open();
            GpuInfo current = monitor.GetGpuByUuid(gpu.Uuid);
            return CaptureEvidence(monitor, current, capturedAt);
        }
        catch (Exception error) when (IsProviderFailure(error))
        {
            return new GpuEvidenceSnapshot(
                gpu,
                null,
                BoardEvidenceState.QueryFailed,
                error.Message,
                capturedAt);
        }
    }

    public ITelemetrySampler CreateSampler(string gpuUuid, SamplingOptions options) =>
        new ResilientTelemetrySampler(gpuUuid, options);

    private static GpuEvidenceSnapshot CaptureEvidence(
        NvidiaMonitor monitor,
        GpuInfo gpu,
        DateTimeOffset capturedAt)
    {
        try
        {
            BoardIdentity board = monitor.GetBoardIdentity(gpu.Index);
            return new GpuEvidenceSnapshot(
                gpu,
                board,
                BoardEvidenceState.Available,
                null,
                capturedAt);
        }
        catch (Exception error) when (IsProviderFailure(error))
        {
            return new GpuEvidenceSnapshot(
                gpu,
                null,
                BoardEvidenceState.QueryFailed,
                error.Message,
                capturedAt);
        }
    }

    private static bool IsProviderFailure(Exception error) => error is
        RtxMonitorException or
        DllNotFoundException or
        EntryPointNotFoundException or
        BadImageFormatException;

    private sealed class ResilientTelemetrySampler : ITelemetrySampler
    {
        private readonly ResilientSampler sampler;

        internal ResilientTelemetrySampler(string gpuUuid, SamplingOptions options)
        {
            sampler = new ResilientSampler(gpuUuid, options);
        }

        public IReadOnlyList<TelemetryEvent> Poll() => sampler.Poll();

        public uint NextDelayMilliseconds(uint successfulSampleIntervalMilliseconds) =>
            sampler.NextDelayMilliseconds(successfulSampleIntervalMilliseconds);

        public void Dispose() => sampler.Dispose();
    }
}
