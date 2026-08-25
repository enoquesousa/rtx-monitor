namespace RtxMonitor.Managed;

public enum MonitoringStatus
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

public enum TelemetryEventKind
{
    Sample,
    Gap,
    Recovered,
    AlertRaised,
    AlertCleared,
}

public sealed record SamplingOptions(
    int BufferCapacity = 256,
    uint InitialBackoffMilliseconds = 250,
    uint MaximumBackoffMilliseconds = 5000);

public sealed record TelemetryEvent(
    ulong Sequence,
    TelemetryEventKind Kind,
    string TargetGpuUuid,
    GpuInfo? Gpu,
    TemperatureSample? Sample,
    DateTimeOffset ObservedAt,
    ulong ObservedAtUnixMilliseconds,
    MonitoringStatus Status,
    string StatusName,
    string Message,
    uint ConsecutiveFailures,
    uint RetryAfterMilliseconds,
    int? AlertThresholdC = null,
    int? AlertHysteresisC = null)
{
    public string KindName => Kind switch
    {
        TelemetryEventKind.Sample => "sample",
        TelemetryEventKind.Gap => "gap",
        TelemetryEventKind.Recovered => "recovered",
        TelemetryEventKind.AlertRaised => "alert_raised",
        TelemetryEventKind.AlertCleared => "alert_cleared",
        _ => "unknown",
    };
}

public interface ITemperatureSession : IDisposable
{
    IReadOnlyList<GpuInfo> GetGpus();

    TemperatureSample ReadGpuDieTemperature(uint index);
}

public sealed class ResilientSampler : IDisposable
{
    private readonly string targetGpuUuid;
    private readonly SamplingOptions options;
    private readonly Func<ITemperatureSession> sessionFactory;
    private readonly CircularEventBuffer eventBuffer;
    private ITemperatureSession? session;
    private GpuInfo? currentGpu;
    private ulong nextSequence = 1;
    private uint consecutiveFailures;
    private uint nextBackoffMilliseconds;
    private uint pendingRetryMilliseconds;
    private bool disposed;

    public ResilientSampler(
        string targetGpuUuid,
        SamplingOptions? options = null,
        Func<ITemperatureSession>? sessionFactory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetGpuUuid);

        this.options = options ?? new SamplingOptions();
        if (this.options.BufferCapacity is < 1 or > 65536)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "A capacidade do buffer deve estar entre 1 e 65536 eventos.");
        }
        if (this.options.InitialBackoffMilliseconds == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "O backoff inicial deve ser maior que zero.");
        }
        if (this.options.MaximumBackoffMilliseconds < this.options.InitialBackoffMilliseconds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "O backoff máximo deve ser maior ou igual ao inicial.");
        }

        this.targetGpuUuid = targetGpuUuid;
        this.sessionFactory = sessionFactory ?? NvidiaMonitor.Open;
        eventBuffer = new CircularEventBuffer(this.options.BufferCapacity);
        nextBackoffMilliseconds = this.options.InitialBackoffMilliseconds;
    }

    public string TargetGpuUuid => targetGpuUuid;

    public uint ConsecutiveFailures => consecutiveFailures;

    public IReadOnlyList<TelemetryEvent> Poll()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var emitted = new List<TelemetryEvent>(2);

        try
        {
            session ??= Connect();
            TemperatureSample sample = session.ReadGpuDieTemperature(currentGpu!.Index);
            if (sample.GpuIndex != currentGpu.Index)
            {
                throw new RtxMonitorException(
                    MonitoringStatus.BackendError,
                    "A amostra de temperatura pertence a outro índice de GPU.");
            }

            if (consecutiveFailures > 0)
            {
                uint recoveredFailures = consecutiveFailures;
                TelemetryEvent recovered = CreateBaseEvent(TelemetryEventKind.Recovered) with
                {
                    ObservedAt = sample.CapturedAt,
                    ObservedAtUnixMilliseconds = sample.TimestampUnixMilliseconds,
                    ConsecutiveFailures = recoveredFailures,
                    Message = $"Monitoramento recuperado após {recoveredFailures} falha(s).",
                };
                Record(recovered, emitted);
            }

            consecutiveFailures = 0;
            pendingRetryMilliseconds = 0;
            nextBackoffMilliseconds = options.InitialBackoffMilliseconds;

            TelemetryEvent sampleEvent = CreateBaseEvent(TelemetryEventKind.Sample) with
            {
                Sample = sample,
                ObservedAt = sample.CapturedAt,
                ObservedAtUnixMilliseconds = sample.TimestampUnixMilliseconds,
                ConsecutiveFailures = 0,
            };
            Record(sampleEvent, emitted);
        }
        catch (RtxMonitorException error) when (IsRecoverable(error.Status))
        {
            RecordGap(error.Status, error.Message, emitted);
        }
        catch (DllNotFoundException error)
        {
            RecordGap(MonitoringStatus.BackendNotFound, error.Message, emitted);
        }
        catch (EntryPointNotFoundException error)
        {
            RecordGap(MonitoringStatus.BackendSymbolMissing, error.Message, emitted);
        }

        return emitted;
    }

    public IReadOnlyList<TelemetryEvent> GetRecentEvents()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return eventBuffer.Snapshot();
    }

    public uint NextDelayMilliseconds(uint successfulSampleIntervalMilliseconds) =>
        consecutiveFailures == 0 ? successfulSampleIntervalMilliseconds : pendingRetryMilliseconds;

    public static bool IsRecoverable(MonitoringStatus status) => status is
        MonitoringStatus.BackendNotFound or
        MonitoringStatus.BackendSymbolMissing or
        MonitoringStatus.DriverNotLoaded or
        MonitoringStatus.GpuNotFound or
        MonitoringStatus.GpuLost or
        MonitoringStatus.BackendError;

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        session?.Dispose();
        session = null;
        currentGpu = null;
        disposed = true;
        GC.SuppressFinalize(this);
    }

    private ITemperatureSession Connect()
    {
        ITemperatureSession candidate = sessionFactory()
            ?? throw new RtxMonitorException(
                MonitoringStatus.BackendError,
                "A fábrica de sessões não retornou uma sessão.");

        try
        {
            GpuInfo? match = candidate.GetGpus().FirstOrDefault(
                gpu => string.Equals(
                    gpu.Uuid,
                    targetGpuUuid,
                    StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                throw new RtxMonitorException(
                    MonitoringStatus.GpuNotFound,
                    $"A GPU alvo não está disponível: {targetGpuUuid}");
            }

            currentGpu = match;
            return candidate;
        }
        catch
        {
            candidate.Dispose();
            throw;
        }
    }

    private void RecordGap(
        MonitoringStatus status,
        string message,
        List<TelemetryEvent> emitted)
    {
        if (consecutiveFailures < uint.MaxValue)
        {
            consecutiveFailures++;
        }

        uint retryAfter = AdvanceBackoff();
        pendingRetryMilliseconds = retryAfter;
        TelemetryEvent gap = CreateBaseEvent(TelemetryEventKind.Gap) with
        {
            Status = status,
            StatusName = StatusWireName(status),
            Message = message,
            ConsecutiveFailures = consecutiveFailures,
            RetryAfterMilliseconds = retryAfter,
        };
        Record(gap, emitted);

        session?.Dispose();
        session = null;
        currentGpu = null;
    }

    private TelemetryEvent CreateBaseEvent(TelemetryEventKind kind)
    {
        DateTimeOffset observedAt = DateTimeOffset.UtcNow;
        return new TelemetryEvent(
            nextSequence++,
            kind,
            targetGpuUuid,
            currentGpu,
            null,
            observedAt,
            checked((ulong)observedAt.ToUnixTimeMilliseconds()),
            MonitoringStatus.Ok,
            StatusWireName(MonitoringStatus.Ok),
            string.Empty,
            consecutiveFailures,
            0);
    }

    private void Record(TelemetryEvent telemetryEvent, List<TelemetryEvent> emitted)
    {
        eventBuffer.Add(telemetryEvent);
        emitted.Add(telemetryEvent);
    }

    private uint AdvanceBackoff()
    {
        uint current = nextBackoffMilliseconds;
        if (nextBackoffMilliseconds >= options.MaximumBackoffMilliseconds ||
            nextBackoffMilliseconds > options.MaximumBackoffMilliseconds / 2)
        {
            nextBackoffMilliseconds = options.MaximumBackoffMilliseconds;
        }
        else
        {
            nextBackoffMilliseconds *= 2;
        }

        return current;
    }

    private static string StatusWireName(MonitoringStatus status) => status switch
    {
        MonitoringStatus.Ok => "ok",
        MonitoringStatus.InvalidArgument => "invalid argument",
        MonitoringStatus.OutOfMemory => "out of memory",
        MonitoringStatus.BackendNotFound => "NVIDIA monitoring backend not found",
        MonitoringStatus.BackendSymbolMissing => "required monitoring backend symbol missing",
        MonitoringStatus.DriverNotLoaded => "NVIDIA driver not loaded",
        MonitoringStatus.NoPermission => "permission denied by NVIDIA driver",
        MonitoringStatus.GpuNotFound => "GPU not found",
        MonitoringStatus.NotSupported => "operation not supported by GPU or driver",
        MonitoringStatus.GpuLost => "GPU is inaccessible or lost",
        MonitoringStatus.BackendError => "monitoring backend error",
        MonitoringStatus.AbiMismatch => "backend ABI version mismatch",
        _ => "unknown rtxmon status",
    };

    private sealed class CircularEventBuffer
    {
        private readonly TelemetryEvent?[] entries;
        private int start;
        private int count;

        internal CircularEventBuffer(int capacity)
        {
            entries = new TelemetryEvent[capacity];
        }

        internal void Add(TelemetryEvent telemetryEvent)
        {
            if (count < entries.Length)
            {
                entries[count] = telemetryEvent;
                count++;
                return;
            }

            entries[start] = telemetryEvent;
            start = (start + 1) % entries.Length;
        }

        internal IReadOnlyList<TelemetryEvent> Snapshot()
        {
            var snapshot = new List<TelemetryEvent>(count);
            for (int offset = 0; offset < count; offset++)
            {
                int index = count < entries.Length ? offset : (start + offset) % entries.Length;
                snapshot.Add(entries[index]!);
            }

            return snapshot;
        }
    }
}
