using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using RtxMonitor.Managed;

namespace RtxMonitor.Service;

public sealed record LiveTelemetryRecord(
    long EventId,
    string RunId,
    string GpuUuid,
    DateTimeOffset PublishedAt,
    string Json);

public sealed record StreamDropSnapshot(long Count, long LatestEventId);

public sealed record TelemetryDeliveryBatch(
    IReadOnlyList<LiveTelemetryRecord> Records,
    StreamDropSnapshot Dropped);

public sealed class TelemetrySubscriberLimitException : Exception
{
    public TelemetrySubscriberLimitException(int maximumClients)
        : base($"O limite de {maximumClients} clientes SSE simultâneos foi atingido.")
    {
    }
}

public sealed class TelemetryEventHub
{
    public const int LiveSchemaVersion = 1;

    private readonly object gate = new();
    private readonly Dictionary<long, SubscriberState> subscribers = [];
    private readonly int queueCapacity;
    private readonly int maximumClients;
    private long nextSubscriberId;

    public TelemetryEventHub(RtxMonitorServiceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        queueCapacity = options.SseClientQueueCapacity;
        maximumClients = options.MaximumSseClients;
    }

    public int QueueCapacity => queueCapacity;

    public int MaximumClients => maximumClients;

    public int ConnectedClients
    {
        get
        {
            lock (gate)
            {
                return subscribers.Count;
            }
        }
    }

    public void Publish(
        long eventId,
        string runId,
        string gpuUuid,
        TelemetryEvent telemetryEvent)
    {
        if (eventId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(eventId));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(gpuUuid);
        ArgumentNullException.ThrowIfNull(telemetryEvent);
        if (!string.Equals(
            telemetryEvent.TargetGpuUuid,
            gpuUuid,
            StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "O UUID do envelope ao vivo diverge do evento de telemetria.",
                nameof(gpuUuid));
        }

        SubscriberState[] targets;
        lock (gate)
        {
            targets = subscribers.Values
                .Where(
                    subscriber => subscriber.GpuUuid is null ||
                        string.Equals(
                            subscriber.GpuUuid,
                            gpuUuid,
                            StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }
        if (targets.Length == 0)
        {
            return;
        }

        DateTimeOffset publishedAt = DateTimeOffset.UtcNow;
        string eventJson = TelemetryJson.Serialize(telemetryEvent);
        string json = Serialize(eventId, runId, gpuUuid, publishedAt, eventJson);
        var record = new LiveTelemetryRecord(
            eventId,
            runId,
            gpuUuid,
            publishedAt,
            json);

        foreach (SubscriberState subscriber in targets)
        {
            lock (subscriber.Gate)
            {
                if (subscriber.DroppedCount > 0 ||
                    !subscriber.Channel.Writer.TryWrite(record))
                {
                    subscriber.DroppedCount++;
                    subscriber.LatestDroppedEventId = eventId;
                }
            }
        }

    }

    public TelemetrySubscription Subscribe(string? gpuUuid = null)
    {
        if (gpuUuid is not null && string.IsNullOrWhiteSpace(gpuUuid))
        {
            throw new ArgumentException("O filtro de UUID não pode estar vazio.", nameof(gpuUuid));
        }

        lock (gate)
        {
            if (subscribers.Count >= maximumClients)
            {
                throw new TelemetrySubscriberLimitException(maximumClients);
            }

            long subscriberId = ++nextSubscriberId;
            var channel = Channel.CreateBounded<LiveTelemetryRecord>(
                new BoundedChannelOptions(queueCapacity)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = true,
                    SingleWriter = false,
                    AllowSynchronousContinuations = false,
                });
            var state = new SubscriberState(subscriberId, gpuUuid, channel);
            subscribers.Add(subscriberId, state);
            return new TelemetrySubscription(this, state);
        }
    }

    private void Unsubscribe(SubscriberState state)
    {
        lock (gate)
        {
            if (subscribers.Remove(state.Id))
            {
                state.Channel.Writer.TryComplete();
            }
        }
    }

    private static string Serialize(
        long eventId,
        string runId,
        string gpuUuid,
        DateTimeOffset publishedAt,
        string eventJson)
    {
        using JsonDocument eventDocument = JsonDocument.Parse(eventJson);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema_version", LiveSchemaVersion);
            writer.WriteNumber("event_id", eventId);
            writer.WriteString("run_id", runId);
            writer.WriteString("gpu_uuid", gpuUuid);
            writer.WriteNumber("published_at_unix_ms", publishedAt.ToUnixTimeMilliseconds());
            writer.WritePropertyName("event");
            eventDocument.RootElement.WriteTo(writer);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    internal sealed class SubscriberState
    {
        internal SubscriberState(
            long id,
            string? gpuUuid,
            Channel<LiveTelemetryRecord> channel)
        {
            Id = id;
            GpuUuid = gpuUuid;
            Channel = channel;
        }

        internal long Id { get; }

        internal object Gate { get; } = new();

        internal string? GpuUuid { get; }

        internal Channel<LiveTelemetryRecord> Channel { get; }

        internal long DroppedCount;

        internal long LatestDroppedEventId;
    }

    public sealed class TelemetrySubscription : IDisposable
    {
        private readonly TelemetryEventHub owner;
        private SubscriberState? state;

        internal TelemetrySubscription(TelemetryEventHub owner, SubscriberState state)
        {
            this.owner = owner;
            this.state = state;
        }

        public ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken)
        {
            SubscriberState current = state ??
                throw new ObjectDisposedException(nameof(TelemetrySubscription));
            return current.Channel.Reader.WaitToReadAsync(cancellationToken);
        }

        public TelemetryDeliveryBatch TakeBatch()
        {
            SubscriberState current = state ??
                throw new ObjectDisposedException(nameof(TelemetrySubscription));
            lock (current.Gate)
            {
                List<LiveTelemetryRecord>? records = null;
                while (current.Channel.Reader.TryRead(out LiveTelemetryRecord? record))
                {
                    records ??= [];
                    records.Add(record);
                }

                var dropped = new StreamDropSnapshot(
                    current.DroppedCount,
                    current.LatestDroppedEventId);
                current.DroppedCount = 0;
                current.LatestDroppedEventId = 0;
                return new TelemetryDeliveryBatch(records ?? [], dropped);
            }
        }

        public void Dispose()
        {
            SubscriberState? current = Interlocked.Exchange(ref state, null);
            if (current is null)
            {
                return;
            }

            owner.Unsubscribe(current);
            GC.SuppressFinalize(this);
        }
    }
}
