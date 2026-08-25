using RtxMonitor.Managed;

namespace RtxMonitor.Service;

public sealed class MonitoringState : IMonitoringSnapshotSource
{
    private readonly object gate = new();
    private readonly DateTimeOffset startedAt = DateTimeOffset.UtcNow;
    private readonly string databasePath;
    private readonly Dictionary<string, GpuRuntimeSnapshot> gpus =
        new(StringComparer.OrdinalIgnoreCase);

    private StorageRuntimeSnapshot storage;
    private DiscoveryRuntimeSnapshot discovery = new("starting", null, null, null);
    private bool stopped;

    public MonitoringState(RtxMonitorServiceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        databasePath = options.DatabasePath;
        storage = new StorageRuntimeSnapshot(
            "starting",
            databasePath,
            null,
            startedAt,
            null);
    }

    public void MarkStorageStarting()
    {
        lock (gate)
        {
            storage = storage with
            {
                State = "starting",
                ChangedAt = DateTimeOffset.UtcNow,
                LastError = null,
            };
        }
    }

    public void MarkStorageAvailable(int schemaVersion)
    {
        lock (gate)
        {
            storage = new StorageRuntimeSnapshot(
                "available",
                databasePath,
                schemaVersion,
                DateTimeOffset.UtcNow,
                null);
        }
    }

    public void MarkStorageUnavailable(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        lock (gate)
        {
            storage = storage with
            {
                State = "unavailable",
                ChangedAt = DateTimeOffset.UtcNow,
                LastError = error.Message,
            };
        }
    }

    public void RecordDiscoverySuccess(IReadOnlyList<DiscoveredGpu> discovered)
    {
        ArgumentNullException.ThrowIfNull(discovered);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        lock (gate)
        {
            string[] knownUuids = gpus.Keys.ToArray();
            foreach (string uuid in knownUuids)
            {
                gpus[uuid] = gpus[uuid] with { Present = false };
            }

            foreach (DiscoveredGpu item in discovered)
            {
                if (gpus.TryGetValue(item.Gpu.Uuid, out GpuRuntimeSnapshot? current))
                {
                    gpus[item.Gpu.Uuid] = current with
                    {
                        Gpu = item.Gpu,
                        Present = true,
                        ProfileKey = item.Evidence.ProfileKey,
                        BoardCaptureState = item.Evidence.BoardStateName,
                        BoardCaptureError = item.Evidence.BoardError,
                        Capabilities = item,
                    };
                }
                else
                {
                    gpus.Add(
                        item.Gpu.Uuid,
                        new GpuRuntimeSnapshot(
                            item.Gpu,
                            true,
                            "stopped",
                            null,
                            item.Evidence.ProfileKey,
                            item.Evidence.BoardStateName,
                            item.Evidence.BoardError,
                            null,
                            null,
                            null,
                            null,
                            0,
                            null,
                            item));
                }
            }

            discovery = new DiscoveryRuntimeSnapshot(
                "available",
                now,
                now,
                null);
        }
    }

    public void RecordDiscoveryFailure(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        lock (gate)
        {
            discovery = discovery with
            {
                State = "unavailable",
                LastAttemptAt = DateTimeOffset.UtcNow,
                LastError = error.Message,
            };
        }
    }

    public void RecordCollectorStarting(GpuInfo gpu)
    {
        ArgumentNullException.ThrowIfNull(gpu);
        UpdateGpu(
            gpu.Uuid,
            current => current with
            {
                Gpu = gpu,
                CollectorState = "starting",
                RunId = null,
                LastError = null,
            });
    }

    public void RecordCollectorStarted(string gpuUuid, string runId)
    {
        UpdateGpu(
            gpuUuid,
            current => current with
            {
                CollectorState = "running",
                RunId = runId,
                LastError = null,
            });
    }

    public void RecordTelemetry(string gpuUuid, TelemetryEvent telemetryEvent)
    {
        ArgumentNullException.ThrowIfNull(telemetryEvent);
        UpdateGpu(
            gpuUuid,
            current => current with
            {
                Gpu = telemetryEvent.Gpu ?? current.Gpu,
                CollectorState = telemetryEvent.Kind == TelemetryEventKind.Gap
                    ? "degraded"
                    : "running",
                LastEventType = telemetryEvent.KindName,
                LastEventAt = telemetryEvent.ObservedAt,
                TemperatureC = telemetryEvent.Sample?.TemperatureC ?? current.TemperatureC,
                TemperatureCapturedAt = telemetryEvent.Sample?.CapturedAt ??
                    current.TemperatureCapturedAt,
                ConsecutiveFailures = telemetryEvent.ConsecutiveFailures,
                LastError = telemetryEvent.Kind == TelemetryEventKind.Gap
                    ? telemetryEvent.Message
                    : null,
                PublicTelemetry = telemetryEvent.PublicTelemetry ?? current.PublicTelemetry,
                ComputedMetrics = telemetryEvent.ComputedMetrics ?? current.ComputedMetrics,
            });
    }

    public void RecordCollectorFailure(string gpuUuid, Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        UpdateGpu(
            gpuUuid,
            current => current with
            {
                CollectorState = "failed",
                RunId = null,
                LastError = error.Message,
            });
    }

    public void RecordCollectorStopped(string gpuUuid)
    {
        UpdateGpu(
            gpuUuid,
            current => current with
            {
                CollectorState = "stopped",
                RunId = null,
            });
    }

    public void MarkStopped()
    {
        lock (gate)
        {
            stopped = true;
            storage = storage with
            {
                State = "stopped",
                ChangedAt = DateTimeOffset.UtcNow,
            };
            foreach (string uuid in gpus.Keys.ToArray())
            {
                gpus[uuid] = gpus[uuid] with
                {
                    CollectorState = "stopped",
                    RunId = null,
                };
            }
        }
    }

    public MonitoringRuntimeSnapshot GetSnapshot()
    {
        lock (gate)
        {
            GpuRuntimeSnapshot[] gpuSnapshot = gpus.Values
                .OrderBy(gpu => gpu.Gpu.Index)
                .ThenBy(gpu => gpu.Gpu.Uuid, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            bool storageReady = storage.State == "available";
            bool discoveryAttempted = discovery.State != "starting";
            bool ready = !stopped && storageReady && discoveryAttempted;
            string status;
            if (stopped)
            {
                status = "stopped";
            }
            else if (!storageReady)
            {
                status = storage.State == "starting" ? "starting" : "unavailable";
            }
            else if (!discoveryAttempted)
            {
                status = "starting";
            }
            else if (discovery.State != "available" ||
                     gpuSnapshot.Length == 0 ||
                     gpuSnapshot.All(gpu => !gpu.Present) ||
                     gpuSnapshot.Any(
                         gpu => gpu.CollectorState is not "running"))
            {
                status = "degraded";
            }
            else
            {
                status = "healthy";
            }

            return new MonitoringRuntimeSnapshot(
                startedAt,
                status,
                ready,
                storage,
                discovery,
                gpuSnapshot);
        }
    }

    private void UpdateGpu(
        string gpuUuid,
        Func<GpuRuntimeSnapshot, GpuRuntimeSnapshot> update)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gpuUuid);
        ArgumentNullException.ThrowIfNull(update);
        lock (gate)
        {
            if (gpus.TryGetValue(gpuUuid, out GpuRuntimeSnapshot? current))
            {
                gpus[gpuUuid] = update(current);
            }
        }
    }
}
