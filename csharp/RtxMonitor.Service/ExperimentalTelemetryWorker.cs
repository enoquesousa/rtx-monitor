using RtxMonitor.Managed;

namespace RtxMonitor.Service;

internal sealed class ExperimentalTelemetryWorker(
    RtxMonitorServiceOptions options,
    IMonitoringSnapshotSource monitoring,
    ExperimentalTelemetrySnapshotState state,
    IExperimentalProfileSource profiles,
    IExperimentalWorkerFactory workers,
    ILogger<ExperimentalTelemetryWorker> logger,
    TimeProvider? timeProvider = null) : BackgroundService
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Disabled mode must not even open the native profile diagnostic or create a process.
        if (!options.ExperimentalTelemetryEnabled) return;
        var collectors = new Dictionary<string, ExperimentalTelemetryCollector>(StringComparer.OrdinalIgnoreCase);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                long iterationStartedAt = clock.GetTimestamp();
                MonitoringRuntimeSnapshot runtime = monitoring.GetSnapshot();
                foreach (GpuRuntimeSnapshot gpu in runtime.Gpus)
                {
                    if (!gpu.Present || runtime.Discovery.State != "available")
                    {
                        if (collectors.TryGetValue(gpu.Gpu.Uuid, out ExperimentalTelemetryCollector? existing))
                        {
                            ExperimentalTelemetryState failure = gpu.Present
                                ? ExperimentalTelemetryState.IdentityUnavailable : ExperimentalTelemetryState.GpuNotFound;
                            await existing.InvalidateAsync(failure).ConfigureAwait(false);
                            state.Invalidate(gpu, failure);
                        }
                        continue;
                    }
                    if (!collectors.TryGetValue(gpu.Gpu.Uuid, out ExperimentalTelemetryCollector? collector))
                    {
                        collector = new ExperimentalTelemetryCollector(true,
                            options.ExperimentalIntervalMilliseconds, profiles, workers, clock);
                        collectors.Add(gpu.Gpu.Uuid, collector);
                    }
                    // Reading once also applies identity/age invalidation without requiring an API client.
                    _ = state.GetSnapshot(gpu.Gpu.Uuid);
                    if (state.GetInvalidation(gpu.Gpu.Uuid) is ExperimentalTelemetryState invalidation)
                        await collector.InvalidateAsync(invalidation).ConfigureAwait(false);
                    ExperimentalTelemetryReport report = await collector.CollectAsync(gpu.Gpu, stoppingToken).ConfigureAwait(false);
                    state.Record(report);
                    // Optional diagnostic trace observes every collection, independently of HTTP
                    // polling and public sampling. It does not provide values to the product.
                    if (logger.IsEnabled(LogLevel.Debug))
                    {
                        logger.LogDebug(new EventId(1101, "ProfileAcquisition"),
                            "Profile acquisition {GpuUuid}: hotspot {HotspotState} at {HotspotCapturedUnixMs}, " +
                            "voltage {VoltageState} at {VoltageCapturedUnixMs}; thermal clock {ThermalFinishedMonotonicMs}",
                            report.GpuUuid, report.Fields[0].StateName, report.Fields[0].CapturedAt?.ToUnixTimeMilliseconds(),
                            report.Fields[1].StateName, report.Fields[1].CapturedAt?.ToUnixTimeMilliseconds(),
                            report.Fields[0].Acquisition?.FinishedMonotonicMilliseconds);
                    }
                }
                TimeSpan interval = TimeSpan.FromMilliseconds(options.ExperimentalIntervalMilliseconds);
                TimeSpan elapsed = clock.GetElapsedTime(iterationStartedAt);
                // Account for profile discovery and collection within the configured start-to-start period.
                // An overrun skips catch-up and starts a fresh wait; it cannot trigger a burst or hot loop.
                TimeSpan delay = elapsed >= TimeSpan.Zero && elapsed < interval ? interval - elapsed : interval;
                await Task.Delay(delay, clock, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            foreach ((string uuid, ExperimentalTelemetryCollector collector) in collectors)
            {
                await collector.DisposeAsync().ConfigureAwait(false);
                GpuRuntimeSnapshot? gpu = monitoring.GetSnapshot().Gpus.FirstOrDefault(item => item.Gpu.Uuid == uuid);
                if (gpu is not null) state.Invalidate(gpu, ExperimentalTelemetryState.Cancelled);
            }
            logger.LogInformation("Sessões experimentais encerradas; operações com falha não foram reiniciadas.");
        }
    }
}
