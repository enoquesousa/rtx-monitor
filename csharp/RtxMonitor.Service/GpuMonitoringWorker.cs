using System.Threading.Channels;
using RtxMonitor.Managed;
using RtxMonitor.Storage;

namespace RtxMonitor.Service;

public sealed class GpuMonitoringWorker : BackgroundService
{
    private static readonly TimeSpan RetentionInterval = TimeSpan.FromHours(24);

    private readonly object persistenceGate = new();
    private readonly RtxMonitorServiceOptions options;
    private readonly MonitoringState state;
    private readonly TelemetryStoreProvider storeProvider;
    private readonly TelemetryEventHub eventHub;
    private readonly IMonitoringBackend backend;
    private readonly ILogger<GpuMonitoringWorker> logger;

    public GpuMonitoringWorker(
        RtxMonitorServiceOptions options,
        MonitoringState state,
        TelemetryStoreProvider storeProvider,
        TelemetryEventHub eventHub,
        IMonitoringBackend backend,
        ILogger<GpuMonitoringWorker> logger)
    {
        this.options = options;
        this.state = state;
        this.storeProvider = storeProvider;
        this.eventHub = eventHub;
        this.backend = backend;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                SqliteTelemetryStore? store = null;
                try
                {
                    state.MarkStorageStarting();
                    store = SqliteTelemetryStore.Open(
                        new TelemetryStoreOptions(
                            options.DatabasePath,
                            TimeSpan.FromDays(options.RetentionDays)));
                    store.VerifyIntegrity();
                    RetentionResult retention = store.ApplyRetention(DateTimeOffset.UtcNow);
                    storeProvider.SetAvailable(store);
                    state.MarkStorageAvailable(store.GetSchemaVersion());
                    logger.LogInformation(
                        "SQLite disponível em {DatabasePath}; retenção removeu {Events} eventos, " +
                        "{Snapshots} snapshots e {Runs} runs.",
                        store.DatabasePath,
                        retention.EventsDeleted,
                        retention.SnapshotsDeleted,
                        retention.RunsDeleted);

                    await RunWithStoreAsync(store, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception error) when (IsStorageFailure(error))
                {
                    state.MarkStorageUnavailable(error);
                    logger.LogError(
                        error,
                        "Armazenamento indisponível; nova tentativa em {RetrySeconds} segundos.",
                        options.DependencyRetryInterval.TotalSeconds);
                }
                finally
                {
                    if (store is not null)
                    {
                        storeProvider.Clear(store);
                    }
                }

                try
                {
                    await Task.Delay(options.DependencyRetryInterval, stoppingToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
        finally
        {
            storeProvider.Clear();
            state.MarkStopped();
        }
    }

    private async Task RunWithStoreAsync(
        SqliteTelemetryStore store,
        CancellationToken stoppingToken)
    {
        var collectors = new Dictionary<string, CollectorHandle>(StringComparer.OrdinalIgnoreCase);
        var retryAfter = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        Channel<CollectorCompletion> completions = Channel.CreateUnbounded<CollectorCompletion>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            });
        DateTimeOffset nextDiscoveryAt = DateTimeOffset.MinValue;
        DateTimeOffset nextRetentionAt = DateTimeOffset.UtcNow.Add(RetentionInterval);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                DateTimeOffset now = DateTimeOffset.UtcNow;
                if (now >= nextRetentionAt)
                {
                    RetentionResult retention = store.ApplyRetention(now);
                    logger.LogInformation(
                        "Retenção periódica removeu {Events} eventos, {Snapshots} snapshots e {Runs} runs.",
                        retention.EventsDeleted,
                        retention.SnapshotsDeleted,
                        retention.RunsDeleted);
                    nextRetentionAt = now.Add(RetentionInterval);
                }

                if (now >= nextDiscoveryAt)
                {
                    DiscoverAndStartCollectors(
                        store,
                        collectors,
                        retryAfter,
                        completions.Writer,
                        stoppingToken);
                    nextDiscoveryAt = now.Add(options.DiscoveryInterval);
                }

                while (completions.Reader.TryRead(out CollectorCompletion? completion))
                {
                    if (collectors.Remove(completion.GpuUuid, out CollectorHandle? handle))
                    {
                        handle.Cancellation.Dispose();
                    }

                    if (completion.Error is null)
                    {
                        state.RecordCollectorStopped(completion.GpuUuid);
                        continue;
                    }

                    state.RecordCollectorFailure(completion.GpuUuid, completion.Error);
                    if (IsStorageFailure(completion.Error))
                    {
                        throw completion.Error;
                    }

                    retryAfter[completion.GpuUuid] =
                        DateTimeOffset.UtcNow.Add(options.DependencyRetryInterval);
                    logger.LogWarning(
                        completion.Error,
                        "Coletor da GPU {GpuUuid} encerrou; uma nova instância será tentada.",
                        completion.GpuUuid);
                }

                TimeSpan untilDiscovery = nextDiscoveryAt - DateTimeOffset.UtcNow;
                TimeSpan delay = untilDiscovery <= TimeSpan.Zero
                    ? TimeSpan.FromMilliseconds(50)
                    : TimeSpan.FromMilliseconds(
                        Math.Min(untilDiscovery.TotalMilliseconds, 1000));
                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
            }
        }
        finally
        {
            foreach (CollectorHandle handle in collectors.Values)
            {
                handle.Cancellation.Cancel();
            }

            try
            {
                await Task.WhenAll(collectors.Values.Select(handle => handle.Task))
                    .ConfigureAwait(false);
            }
            catch (Exception error)
            {
                logger.LogDebug(error, "Um ou mais coletores falharam durante o encerramento.");
            }

            foreach (CollectorHandle handle in collectors.Values)
            {
                handle.Cancellation.Dispose();
            }
        }
    }

    private void DiscoverAndStartCollectors(
        SqliteTelemetryStore store,
        Dictionary<string, CollectorHandle> collectors,
        Dictionary<string, DateTimeOffset> retryAfter,
        ChannelWriter<CollectorCompletion> completionWriter,
        CancellationToken stoppingToken)
    {
        IReadOnlyList<DiscoveredGpu> discovered;
        try
        {
            discovered = backend.Discover();
            state.RecordDiscoverySuccess(discovered);
        }
        catch (Exception error) when (!IsStorageFailure(error))
        {
            state.RecordDiscoveryFailure(error);
            logger.LogWarning(error, "Não foi possível descobrir GPUs NVIDIA.");
            return;
        }

        var startedThisPass = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (DiscoveredGpu gpu in discovered)
        {
            if (!startedThisPass.Add(gpu.Gpu.Uuid) || collectors.ContainsKey(gpu.Gpu.Uuid))
            {
                continue;
            }
            if (retryAfter.TryGetValue(gpu.Gpu.Uuid, out DateTimeOffset retryAt) &&
                retryAt > DateTimeOffset.UtcNow)
            {
                continue;
            }

            retryAfter.Remove(gpu.Gpu.Uuid);
            state.RecordCollectorStarting(gpu.Gpu);
            CancellationTokenSource cancellation =
                CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            Task task = CollectGpuAsync(store, gpu, cancellation.Token);
            var handle = new CollectorHandle(cancellation, task);
            collectors.Add(gpu.Gpu.Uuid, handle);

            _ = task.ContinueWith(
                completed =>
                {
                    Exception? error = completed.IsFaulted
                        ? completed.Exception?.GetBaseException()
                        : null;
                    completionWriter.TryWrite(new CollectorCompletion(gpu.Gpu.Uuid, error));
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private async Task CollectGpuAsync(
        SqliteTelemetryStore store,
        DiscoveredGpu discovered,
        CancellationToken cancellationToken)
    {
        string? runId = null;
        Exception? primaryError = null;
        string completionReason = "error";

        try
        {
            runId = store.StartRun(
                new MonitoringRunOptions(
                    discovered.Gpu.Uuid,
                    options.IntervalMilliseconds,
                    options.BufferCapacity,
                    options.AlertThresholdC,
                    options.AlertHysteresisC,
                    ApplicationVersion(),
                    DateTimeOffset.UtcNow));
            state.RecordCollectorStarted(discovered.Gpu.Uuid, runId);

            long currentSnapshotId = store.RegisterGpuSnapshot(runId, discovered.Evidence);
            string currentFingerprint = GpuFingerprint(discovered.Gpu);
            using ITelemetrySampler sampler = backend.CreateSampler(
                discovered.Gpu.Uuid,
                new SamplingOptions(options.BufferCapacity, 250, 5000));
            AlertEvaluator? alertEvaluator = options.AlertThresholdC is int threshold
                ? new AlertEvaluator(new AlertOptions(threshold, options.AlertHysteresisC))
                : null;
            ulong streamSequence = 0;

            while (!cancellationToken.IsCancellationRequested)
            {
                IReadOnlyList<TelemetryEvent> events = sampler.Poll();
                foreach (TelemetryEvent sampledEvent in events)
                {
                    TelemetryEvent telemetryEvent = sampledEvent with
                    {
                        Sequence = ++streamSequence,
                    };

                    if (telemetryEvent.Gpu is GpuInfo currentGpu)
                    {
                        string fingerprint = GpuFingerprint(currentGpu);
                        if (!string.Equals(
                            fingerprint,
                            currentFingerprint,
                            StringComparison.Ordinal))
                        {
                            GpuEvidenceSnapshot evidence = backend.CaptureEvidence(currentGpu);
                            currentSnapshotId = store.RegisterGpuSnapshot(runId, evidence);
                            currentFingerprint = fingerprint;
                        }
                    }

                    PersistPublishAndObserve(
                        store,
                        runId,
                        discovered.Gpu.Uuid,
                        telemetryEvent,
                        currentSnapshotId);

                    if (alertEvaluator is not null &&
                        telemetryEvent.Kind == TelemetryEventKind.Sample &&
                        telemetryEvent.Sample is TemperatureSample sample)
                    {
                        TelemetryEventKind? transition =
                            alertEvaluator.Observe(sample.TemperatureC);
                        if (transition is TelemetryEventKind kind)
                        {
                            TelemetryEvent alertEvent = telemetryEvent with
                            {
                                Sequence = ++streamSequence,
                                Kind = kind,
                                AlertThresholdC = alertEvaluator.Options.ThresholdC,
                                AlertHysteresisC = alertEvaluator.Options.HysteresisC,
                                Message = AlertMessage(
                                    kind,
                                    sample.TemperatureC,
                                    alertEvaluator.Options),
                            };
                            PersistPublishAndObserve(
                                store,
                                runId,
                                discovered.Gpu.Uuid,
                                alertEvent,
                                currentSnapshotId);
                        }
                    }
                }

                uint delayMilliseconds = sampler.NextDelayMilliseconds(
                    checked((uint)options.IntervalMilliseconds));
                await Task.Delay(
                    TimeSpan.FromMilliseconds(delayMilliseconds),
                    cancellationToken).ConfigureAwait(false);
            }

            completionReason = "service_stopped";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            completionReason = "service_stopped";
        }
        catch (Exception error)
        {
            primaryError = error;
            throw;
        }
        finally
        {
            if (runId is not null)
            {
                try
                {
                    store.CompleteRun(runId, completionReason, DateTimeOffset.UtcNow);
                }
                catch (Exception completionError) when (
                    primaryError is not null || cancellationToken.IsCancellationRequested)
                {
                    logger.LogWarning(
                        completionError,
                        "Não foi possível encerrar o run {RunId} da GPU {GpuUuid}.",
                        runId,
                        discovered.Gpu.Uuid);
                }
            }
        }
    }

    private void PersistPublishAndObserve(
        SqliteTelemetryStore store,
        string runId,
        string gpuUuid,
        TelemetryEvent telemetryEvent,
        long snapshotId)
    {
        lock (persistenceGate)
        {
            long eventId = store.AppendEvent(runId, telemetryEvent, snapshotId);
            eventHub.Publish(eventId, runId, gpuUuid, telemetryEvent);
            state.RecordTelemetry(gpuUuid, telemetryEvent);
        }
    }

    private static string ApplicationVersion() =>
        typeof(GpuMonitoringWorker).Assembly.GetName().Version?.ToString(3) ?? "unknown";

    private static string GpuFingerprint(GpuInfo gpu) =>
        $"{gpu.Uuid}\u001f{gpu.Index}\u001f{gpu.Name}\u001f{gpu.DriverVersion}\u001f{gpu.NvmlVersion}";

    private static string AlertMessage(
        TelemetryEventKind kind,
        int temperatureC,
        AlertOptions options) => kind switch
        {
            TelemetryEventKind.AlertRaised =>
                $"Temperatura {temperatureC} °C atingiu o limiar {options.ThresholdC} °C.",
            TelemetryEventKind.AlertCleared =>
                $"Temperatura {temperatureC} °C encerrou o alerta " +
                $"({options.ThresholdC} °C, histerese {options.HysteresisC} °C).",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Transição desconhecida."),
        };

    private static bool IsStorageFailure(Exception error) => error is
        TelemetryStoreException or
        IOException or
        UnauthorizedAccessException;

    private sealed record CollectorHandle(CancellationTokenSource Cancellation, Task Task);

    private sealed record CollectorCompletion(string GpuUuid, Exception? Error);
}
