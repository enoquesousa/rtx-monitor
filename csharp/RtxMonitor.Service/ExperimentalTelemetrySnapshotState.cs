using RtxMonitor.Managed;

namespace RtxMonitor.Service;

public interface IExperimentalTelemetrySnapshotSource
{
    ExperimentalTelemetryReport? GetSnapshot(string gpuUuid);
}

public interface IExperimentalTelemetryPublicationSource
{
    ExperimentalTelemetryReport? GetPublishedSnapshot(string gpuUuid);
    void RecordCommitted(ExperimentalTelemetryReport report);
}

/// <summary>A snapshot expires once and cannot become available again within this service session.</summary>
public sealed class ExperimentalTelemetrySnapshotState : IExperimentalTelemetrySnapshotSource, IExperimentalTelemetryPublicationSource
{
    private readonly object gate = new();
    private readonly Dictionary<string, Entry> snapshots = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Publication> publications = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ExperimentalTelemetryState> invalidations = new(StringComparer.OrdinalIgnoreCase);
    private readonly bool enabled;
    private readonly IMonitoringSnapshotSource monitoring;
    private readonly TimeProvider clock;
    private readonly TimeSpan maximumAge;

    public ExperimentalTelemetrySnapshotState(RtxMonitorServiceOptions options, IMonitoringSnapshotSource monitoring)
        : this(options.ExperimentalTelemetryEnabled, options.ExperimentalIntervalMilliseconds, monitoring, TimeProvider.System) { }

    internal ExperimentalTelemetrySnapshotState(
        bool enabled, int intervalMilliseconds, IMonitoringSnapshotSource monitoring, TimeProvider clock)
    {
        this.enabled = enabled;
        this.monitoring = monitoring;
        this.clock = clock;
        maximumAge = TimeSpan.FromMilliseconds(Math.Max(10_000L, intervalMilliseconds * 3L));
    }

    public ExperimentalTelemetryReport? GetSnapshot(string gpuUuid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gpuUuid);
        MonitoringRuntimeSnapshot runtime = monitoring.GetSnapshot();
        GpuRuntimeSnapshot? gpu = runtime.Gpus.SingleOrDefault(item =>
            string.Equals(item.Gpu.Uuid, gpuUuid, StringComparison.OrdinalIgnoreCase));
        if (gpu is null) return null;
        lock (gate)
        {
            if (!snapshots.TryGetValue(gpuUuid, out Entry? entry))
            {
                ExperimentalTelemetryState initial = enabled
                    ? ExperimentalTelemetryState.NotRequested : ExperimentalTelemetryState.Disabled;
                ExperimentalTelemetryReport report = ExperimentalTelemetryReport.Create(
                    gpu.Gpu, gpu.Capabilities?.Evidence.Board, null, null, null, initial, initial, clock.GetUtcNow())
                    .WithFailure(initial, clock.GetUtcNow());
                snapshots[gpuUuid] = entry = new Entry(report, clock.GetTimestamp());
            }
            if (!enabled || invalidations.ContainsKey(gpuUuid)) return entry.Report;

            ExperimentalTelemetryState? failure = !gpu.Present ? ExperimentalTelemetryState.GpuNotFound
                : runtime.Discovery.State != "available" ? ExperimentalTelemetryState.IdentityUnavailable
                : null;
            if (failure is null && entry.Report.Fields.Any(field => field.State == ExperimentalTelemetryState.Available))
            {
                BoardIdentity? board = gpu.Capabilities?.Evidence.Board;
                if (board?.HasPciIdentity != true || board.HasVbiosVersion != true)
                    failure = ExperimentalTelemetryState.IdentityUnavailable;
                else if (ExperimentalTelemetryReport.Create(gpu.Gpu, board, null, null, null).Identity != entry.Report.Identity ||
                    gpu.Gpu.Index != entry.Report.GpuIndex)
                    failure = ExperimentalTelemetryState.IdentityMismatch;
                else if (clock.GetElapsedTime(entry.RecordedAt) > maximumAge)
                    failure = ExperimentalTelemetryState.Timeout;
            }
            return failure is ExperimentalTelemetryState reason
                ? InvalidateCore(gpu, reason, entry).Report : entry.Report;
        }
    }

    internal void Record(ExperimentalTelemetryReport report)
    {
        lock (gate)
        {
            // An in-flight sample cannot undo an API-observed disappearance, drift, or expiry.
            if (invalidations.ContainsKey(report.GpuUuid)) return;
            snapshots[report.GpuUuid] = new Entry(report, clock.GetTimestamp());
        }
    }

    public ExperimentalTelemetryReport? GetPublishedSnapshot(string gpuUuid)
    {
        lock (gate)
        {
            // Acquisition remains separate: the sampler must continue to see newer, uncommitted snapshots.
            // Apply live disappearance/identity/expiry gates before considering any committed numbers.
            ExperimentalTelemetryReport? current = GetSnapshot(gpuUuid);
            if (current is null || !current.Fields.Any(field => field.State == ExperimentalTelemetryState.Available))
                return current;
            if (!publications.TryGetValue(gpuUuid, out Publication? publication))
                return current.WithFailure(ExperimentalTelemetryState.PublicationPending, clock.GetUtcNow());
            ExperimentalTelemetryReport committed = publication.Report;
            if (committed.Identity != current.Identity || committed.GpuIndex != current.GpuIndex ||
                committed.ProfileId != current.ProfileId || committed.ProfileRevision != current.ProfileRevision)
                return current.WithFailure(ExperimentalTelemetryState.IdentityMismatch, clock.GetUtcNow());
            // A newly failed operation must not retain its previous committed number. An all-null
            // diagnostic may precede persistence; never assemble a report with new, uncommitted values.
            if (committed.Fields.Zip(current.Fields).Any(pair =>
                pair.First.State == ExperimentalTelemetryState.Available && pair.Second.State != ExperimentalTelemetryState.Available))
                return current.WithFailure(ExperimentalTelemetryState.PublicationPending, clock.GetUtcNow());
            TimeSpan elapsed = clock.GetElapsedTime(publication.RecordedAt);
            if (elapsed < TimeSpan.Zero || publication.AgeAtCommit + elapsed > maximumAge ||
                committed.Fields.Any(field => field.State == ExperimentalTelemetryState.Available &&
                    field.CapturedAt > clock.GetUtcNow().AddSeconds(1)))
                return committed.WithFailure(ExperimentalTelemetryState.Timeout, clock.GetUtcNow());
            return committed;
        }
    }

    public void RecordCommitted(ExperimentalTelemetryReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        lock (gate)
        {
            if (publications.TryGetValue(report.GpuUuid, out Publication? previous) && SameAcquisitions(previous.Report, report))
            {
                // Repeating one acquisition in multiple public events never renews its validity.
                publications[report.GpuUuid] = previous with { Report = report };
                return;
            }
            DateTimeOffset now = clock.GetUtcNow();
            TimeSpan age = report.Fields.Where(field => field.State == ExperimentalTelemetryState.Available)
                .Select(field => field.CapturedAt.HasValue ? now - field.CapturedAt.Value : maximumAge + TimeSpan.FromTicks(1))
                .DefaultIfEmpty(TimeSpan.Zero).Max();
            publications[report.GpuUuid] = new Publication(report, clock.GetTimestamp(), age > TimeSpan.Zero ? age : TimeSpan.Zero);
        }
    }

    private static bool SameAcquisitions(ExperimentalTelemetryReport first, ExperimentalTelemetryReport second) =>
        first.Identity == second.Identity && first.GpuIndex == second.GpuIndex &&
        first.ProfileId == second.ProfileId && first.ProfileRevision == second.ProfileRevision &&
        first.Fields.Zip(second.Fields).All(pair => pair.First.State == pair.Second.State &&
            pair.First.CapturedAt == pair.Second.CapturedAt && pair.First.RawValue == pair.Second.RawValue &&
            pair.First.Acquisition == pair.Second.Acquisition);

    internal ExperimentalTelemetryState? GetInvalidation(string gpuUuid)
    {
        lock (gate)
        {
            return invalidations.TryGetValue(gpuUuid, out ExperimentalTelemetryState state) ? state : null;
        }
    }

    internal void Invalidate(GpuRuntimeSnapshot gpu, ExperimentalTelemetryState failure)
    {
        lock (gate)
        {
            if (invalidations.ContainsKey(gpu.Gpu.Uuid)) return;
            if (!snapshots.TryGetValue(gpu.Gpu.Uuid, out Entry? entry))
                entry = new Entry(ExperimentalTelemetryReport.Create(
                    gpu.Gpu, gpu.Capabilities?.Evidence.Board, null, null, null), clock.GetTimestamp());
            InvalidateCore(gpu, failure, entry);
        }
    }

    private Entry InvalidateCore(GpuRuntimeSnapshot gpu, ExperimentalTelemetryState failure, Entry entry)
    {
        invalidations[gpu.Gpu.Uuid] = failure;
        var invalid = new Entry(entry.Report.WithFailure(
            failure, clock.GetUtcNow(), gpu.Gpu, gpu.Capabilities?.Evidence.Board), clock.GetTimestamp());
        snapshots[gpu.Gpu.Uuid] = invalid;
        return invalid;
    }

    private sealed record Entry(ExperimentalTelemetryReport Report, long RecordedAt);
    private sealed record Publication(ExperimentalTelemetryReport Report, long RecordedAt, TimeSpan AgeAtCommit);
}
