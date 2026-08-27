using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace RtxMonitor.Service;

internal sealed class WindowsTelemetryWorker : BackgroundService
{
    private static readonly TimeSpan DefaultCollectionInterval = TimeSpan.FromSeconds(2);
    private readonly IMonitoringSnapshotSource monitoring;
    private readonly WindowsTelemetryState state;
    private readonly IWindowsGpuReader reader;
    private readonly ILogger<WindowsTelemetryWorker> logger;
    private readonly TimeSpan collectionInterval;

    public WindowsTelemetryWorker(
        IMonitoringSnapshotSource monitoring,
        WindowsTelemetryState state,
        IWindowsGpuReader reader,
        ILogger<WindowsTelemetryWorker> logger)
        : this(monitoring, state, reader, logger, DefaultCollectionInterval)
    {
    }

    internal WindowsTelemetryWorker(
        IMonitoringSnapshotSource monitoring,
        WindowsTelemetryState state,
        IWindowsGpuReader reader,
        ILogger<WindowsTelemetryWorker> logger,
        TimeSpan collectionInterval)
    {
        this.monitoring = monitoring;
        this.state = state;
        this.reader = reader;
        this.logger = logger;
        this.collectionInterval = collectionInterval > TimeSpan.Zero
            ? collectionInterval
            : throw new ArgumentOutOfRangeException(nameof(collectionInterval));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (GpuRuntimeSnapshot gpu in monitoring.GetSnapshot().Gpus.Where(item => item.Present))
            {
                if (gpu.Capabilities is not DiscoveredGpu discovered)
                {
                    continue;
                }

                try
                {
                    state.Record(reader.Read(discovered, stoppingToken));
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception error)
                {
                    logger.LogWarning(error, "Falha ao coletar telemetria WDDM para {GpuUuid}.", gpu.Gpu.Uuid);
                }
            }

            await Task.Delay(collectionInterval, stoppingToken).ConfigureAwait(false);
        }
    }
}
