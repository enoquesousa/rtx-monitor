using RtxMonitor.Managed;

namespace RtxMonitor.Storage;

public enum TelemetryStoreOpenMode
{
    CreateOrOpen,
    OpenExisting,
}

public enum BoardEvidenceState
{
    NotAttempted,
    Available,
    QueryFailed,
}

public sealed class TelemetryStoreOptions
{
    public TelemetryStoreOptions(
        string databasePath,
        TimeSpan? retentionPeriod = null,
        TelemetryStoreOpenMode openMode = TelemetryStoreOpenMode.CreateOrOpen,
        int busyTimeoutSeconds = 5)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        TimeSpan selectedRetention = retentionPeriod ?? TimeSpan.FromDays(30);
        if (selectedRetention < TimeSpan.FromDays(1) ||
            selectedRetention > TimeSpan.FromDays(3650))
        {
            throw new ArgumentOutOfRangeException(
                nameof(retentionPeriod),
                "A retenção deve estar entre 1 e 3650 dias.");
        }
        if (busyTimeoutSeconds is < 1 or > 60)
        {
            throw new ArgumentOutOfRangeException(
                nameof(busyTimeoutSeconds),
                "O timeout de bloqueio deve estar entre 1 e 60 segundos.");
        }

        DatabasePath = Path.GetFullPath(databasePath);
        RetentionPeriod = selectedRetention;
        OpenMode = openMode;
        BusyTimeoutSeconds = busyTimeoutSeconds;
    }

    public string DatabasePath { get; }

    public TimeSpan RetentionPeriod { get; }

    public TelemetryStoreOpenMode OpenMode { get; }

    public int BusyTimeoutSeconds { get; }
}

public sealed record MonitoringRunOptions(
    string TargetGpuUuid,
    int IntervalMilliseconds,
    int BufferCapacity,
    int? AlertThresholdC,
    int AlertHysteresisC,
    string ApplicationVersion,
    DateTimeOffset StartedAt);

public sealed record GpuEvidenceSnapshot(
    GpuInfo Gpu,
    BoardIdentity? Board,
    BoardEvidenceState BoardState,
    string? BoardError,
    DateTimeOffset ObservedAt)
{
    public string BoardStateName => BoardState switch
    {
        BoardEvidenceState.NotAttempted => "not_attempted",
        BoardEvidenceState.Available => "available",
        BoardEvidenceState.QueryFailed => "query_failed",
        _ => "unknown",
    };

    public string? ProfileKey => Board is { HasPciIdentity: true } identity
        ? $"{identity.PciVendorId & 0xffffU:x4}:{identity.PciDeviceId & 0xffffU:x4}/" +
          $"{identity.PciSubsystemVendorId & 0xffffU:x4}:{identity.PciSubsystemDeviceId & 0xffffU:x4}@" +
          (identity.HasVbiosVersion ? identity.VbiosVersion : "unknown")
        : null;
}

public sealed record TelemetryEventQuery(
    string? RunId = null,
    string? TargetGpuUuid = null,
    TelemetryEventKind? EventKind = null,
    long? FromUnixMilliseconds = null,
    long? ToUnixMilliseconds = null,
    ulong? AfterSequence = null,
    long? AfterEventId = null,
    long? ThroughEventId = null,
    int Limit = 100,
    bool Ascending = false);

public sealed record MonitoringRunEvidence(
    string RunId,
    int EventSchemaVersion,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    string? CompletionReason,
    string TargetGpuUuid,
    int IntervalMilliseconds,
    int BufferCapacity,
    int? AlertThresholdC,
    int AlertHysteresisC,
    double RetentionDays,
    string ApplicationVersion,
    string OsDescription,
    string OsArchitecture,
    string ProcessArchitecture);

public sealed record StoredGpuEvidenceSnapshot(
    long SnapshotId,
    GpuInfo Gpu,
    BoardIdentity? Board,
    BoardEvidenceState BoardState,
    string? BoardError,
    string? ProfileKey,
    DateTimeOffset ObservedAt);

public sealed record StoredTelemetryEvidence(
    long EventId,
    int StoreSchemaVersion,
    int EventSchemaVersion,
    ulong StreamSequence,
    TelemetryEventKind EventKind,
    string EventKindName,
    string TargetGpuUuid,
    DateTimeOffset ObservedAt,
    DateTimeOffset StoredAt,
    MonitoringRunEvidence Run,
    StoredGpuEvidenceSnapshot? DeviceSnapshot,
    string EventJson);

public sealed record RetentionResult(
    long EventsDeleted,
    long SnapshotsDeleted,
    long RunsDeleted);

public class TelemetryStoreException : Exception
{
    public TelemetryStoreException(string message)
        : base(message)
    {
    }

    public TelemetryStoreException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class TelemetrySequenceConflictException : TelemetryStoreException
{
    public TelemetrySequenceConflictException(string runId, ulong sequence)
        : base($"A sequência {sequence} do run {runId} já existe com outro conteúdo ou contexto.")
    {
    }
}
