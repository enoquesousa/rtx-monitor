using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using RtxMonitor.Managed;
using RtxMonitor.Storage;

namespace RtxMonitor.Service;

public static class ServiceEndpoints
{
    public const int ApiSchemaVersion = 1;

    private static readonly JsonSerializerOptions SseJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet(
            "/health",
            (IMonitoringSnapshotSource snapshots, TelemetryEventHub hub) =>
            {
                MonitoringRuntimeSnapshot snapshot = snapshots.GetSnapshot();
                ServiceHealthResponse response = BuildHealth(snapshot, hub);
                return Results.Json(response, statusCode: snapshot.Ready ? 200 : 503);
            })
            .WithName("health");

        RouteGroupBuilder api = endpoints.MapGroup("/api/v1");
        api.MapGet(
            "/gpus",
            (IMonitoringSnapshotSource snapshots) =>
            {
                MonitoringRuntimeSnapshot snapshot = snapshots.GetSnapshot();
                GpuRuntimeResponse[] gpus = snapshot.Gpus.Select(BuildGpu).ToArray();
                return Results.Json(
                    new GpuListResponse(
                        ApiSchemaVersion,
                        snapshot.Discovery.State,
                        gpus.Length,
                        gpus));
            })
            .WithName("gpus");

        api.MapGet(
            "/gpus/{gpuUuid}/capabilities",
            (string gpuUuid, IMonitoringSnapshotSource snapshots) =>
            {
                if (string.IsNullOrWhiteSpace(gpuUuid) || gpuUuid.Length > 256)
                {
                    return Results.Problem(
                        statusCode: 400,
                        title: "UUID inválido",
                        detail: "gpu_uuid deve possuir entre 1 e 256 caracteres.");
                }

                GpuRuntimeSnapshot? gpu = snapshots.GetSnapshot().Gpus.FirstOrDefault(
                    item => string.Equals(
                        item.Gpu.Uuid,
                        gpuUuid,
                        StringComparison.OrdinalIgnoreCase));
                if (gpu is null)
                {
                    return Results.Problem(
                        statusCode: 404,
                        title: "GPU desconhecida",
                        detail: $"Nenhuma GPU com UUID {gpuUuid} foi observada pelo serviço.");
                }
                if (gpu.Capabilities is null)
                {
                    return Results.Problem(
                        statusCode: 503,
                        title: "Capabilities indisponíveis",
                        detail: "O inventário térmico ainda não foi capturado para esta GPU.");
                }

                return Results.Json(BuildCapabilities(gpu.Capabilities));
            })
            .WithName("capabilities");

        api.MapGet(
            "/gpus/{gpuUuid}/telemetry",
            (string gpuUuid, IMonitoringSnapshotSource snapshots) =>
            {
                if (string.IsNullOrWhiteSpace(gpuUuid) || gpuUuid.Length > 256)
                {
                    return Results.Problem(
                        statusCode: 400,
                        title: "UUID inválido",
                        detail: "gpu_uuid deve possuir entre 1 e 256 caracteres.");
                }

                GpuRuntimeSnapshot? gpu = snapshots.GetSnapshot().Gpus.FirstOrDefault(
                    item => string.Equals(
                        item.Gpu.Uuid,
                        gpuUuid,
                        StringComparison.OrdinalIgnoreCase));
                if (gpu is null)
                {
                    return Results.Problem(
                        statusCode: 404,
                        title: "GPU desconhecida",
                        detail: $"Nenhuma GPU com UUID {gpuUuid} foi observada pelo serviço.");
                }
                if (gpu.PublicTelemetry is null)
                {
                    return Results.Problem(
                        statusCode: 503,
                        title: "Telemetria indisponível",
                        detail: "O primeiro relatório de telemetria pública ainda não foi capturado para esta GPU.");
                }

                return Results.Json(BuildTelemetry(gpu));
            })
            .WithName("telemetry");

        api.MapGet(
            "/gpus/{gpuUuid}/windows-telemetry",
            (string gpuUuid, IMonitoringSnapshotSource monitoring,
                IWindowsTelemetrySnapshotSource windowsTelemetry) =>
            {
                if (string.IsNullOrWhiteSpace(gpuUuid) || gpuUuid.Length > 256)
                {
                    return Results.Problem(
                        statusCode: 400,
                        title: "UUID inválido",
                        detail: "gpu_uuid deve possuir entre 1 e 256 caracteres.");
                }
                bool known = monitoring.GetSnapshot().Gpus.Any(item => string.Equals(
                    item.Gpu.Uuid, gpuUuid, StringComparison.OrdinalIgnoreCase));
                if (!known)
                {
                    return Results.Problem(
                        statusCode: 404,
                        title: "GPU desconhecida",
                        detail: $"Nenhuma GPU com UUID {gpuUuid} foi observada pelo serviço.");
                }

                WindowsTelemetrySnapshot? snapshot = windowsTelemetry.GetSnapshot(gpuUuid);
                if (snapshot is null)
                {
                    return Results.Problem(
                        statusCode: 503,
                        title: "Telemetria Windows indisponível",
                        detail: "A primeira tentativa PDH/DXGI ainda não foi concluída para esta GPU.");
                }
                return Results.Json(BuildWindowsTelemetry(snapshot));
            })
            .WithName("windows-telemetry");

        api.MapGet("/history", HandleHistoryAsync).WithName("history");
        api.MapGet("/events", HandleEventsAsync).WithName("events");
    }

    private static async Task<IResult> HandleHistoryAsync(
        HttpContext context,
        IHistorySource history,
        RtxMonitorServiceOptions options)
    {
        try
        {
            TelemetryEventQuery query = ParseHistoryQuery(context.Request.Query, options);
            IReadOnlyList<StoredTelemetryEvidence> records = await Task.Run(
                () => history.Query(query),
                context.RequestAborted).ConfigureAwait(false);
            JsonElement[] items = records.Select(
                record =>
                {
                    using JsonDocument document = JsonDocument.Parse(EvidenceJson.Serialize(record));
                    return document.RootElement.Clone();
                }).ToArray();
            long? lastEventId = records.Count == 0
                ? null
                : records[^1].EventId;
            context.Response.Headers.CacheControl = "no-store";
            return Results.Json(
                new HistoryResponse(
                    ApiSchemaVersion,
                    items.Length,
                    query.Limit,
                    query.Ascending ? "asc" : "desc",
                    lastEventId,
                    items));
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            return Results.Empty;
        }
        catch (ServiceDependencyUnavailableException error)
        {
            return Results.Problem(
                statusCode: 503,
                title: "Histórico indisponível",
                detail: error.Message);
        }
        catch (ArgumentException error)
        {
            return Results.Problem(
                statusCode: 400,
                title: "Consulta inválida",
                detail: error.Message);
        }
    }

    private static async Task HandleEventsAsync(
        HttpContext context,
        TelemetryEventHub hub,
        RtxMonitorServiceOptions options)
    {
        string? gpuUuid;
        try
        {
            gpuUuid = OptionalQueryValue(context.Request.Query, "gpu_uuid");
        }
        catch (ArgumentException error)
        {
            await Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Consulta inválida",
                detail: error.Message).ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        TelemetryEventHub.TelemetrySubscription subscription;
        try
        {
            subscription = hub.Subscribe(gpuUuid);
        }
        catch (TelemetrySubscriberLimitException error)
        {
            await Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Limite de clientes SSE atingido",
                detail: error.Message).ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        using (subscription)
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "text/event-stream; charset=utf-8";
            context.Response.Headers.CacheControl = "no-cache, no-store";
            context.Response.Headers.Connection = "keep-alive";
            context.Response.Headers["X-Accel-Buffering"] = "no";
            context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

            await context.Response.WriteAsync(
                ": rtx-monitor-service-v1\n\n",
                context.RequestAborted).ConfigureAwait(false);
            await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);

            long? lastDeliveredEventId = null;
            Task<bool>? waitToRead = null;
            Task heartbeat = Task.Delay(
                options.SseHeartbeatInterval,
                context.RequestAborted);
            while (!context.RequestAborted.IsCancellationRequested)
            {
                TelemetryDeliveryBatch batch = subscription.TakeBatch();
                foreach (LiveTelemetryRecord record in batch.Records)
                {
                    await WriteSseEventAsync(
                        context,
                        record.EventId,
                        "telemetry",
                        record.Json).ConfigureAwait(false);
                    lastDeliveredEventId = record.EventId;
                }

                StreamDropSnapshot dropped = batch.Dropped;
                if (dropped.Count > 0)
                {
                    var gap = new StreamGapResponse(
                        ApiSchemaVersion,
                        dropped.Count,
                        lastDeliveredEventId,
                        dropped.LatestEventId,
                        BuildRecoveryEndpoint(lastDeliveredEventId, gpuUuid));
                    await WriteSseEventAsync(
                        context,
                        null,
                        "stream_gap",
                        JsonSerializer.Serialize(gap, SseJsonOptions)).ConfigureAwait(false);
                }

                waitToRead ??= subscription
                    .WaitToReadAsync(context.RequestAborted)
                    .AsTask();
                Task completed = await Task.WhenAny(waitToRead, heartbeat).ConfigureAwait(false);
                if (completed == waitToRead)
                {
                    bool available = await waitToRead.ConfigureAwait(false);
                    waitToRead = null;
                    if (!available)
                    {
                        break;
                    }
                }
                else
                {
                    await context.Response.WriteAsync(
                        $": heartbeat {DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}\n\n",
                        context.RequestAborted).ConfigureAwait(false);
                    await context.Response.Body.FlushAsync(context.RequestAborted)
                        .ConfigureAwait(false);
                    heartbeat = Task.Delay(
                        options.SseHeartbeatInterval,
                        context.RequestAborted);
                }
            }
        }
    }

    internal static string BuildRecoveryEndpoint(long? lastDeliveredEventId, string? gpuUuid)
    {
        string endpoint = "/api/v1/history?order=asc&after_event_id=" +
            (lastDeliveredEventId?.ToString(CultureInfo.InvariantCulture) ?? "0");
        return gpuUuid is null
            ? endpoint
            : endpoint + "&gpu_uuid=" + Uri.EscapeDataString(gpuUuid);
    }

    private static async Task WriteSseEventAsync(
        HttpContext context,
        long? id,
        string eventName,
        string data)
    {
        if (id is long eventId)
        {
            await context.Response.WriteAsync(
                $"id: {eventId.ToString(CultureInfo.InvariantCulture)}\n",
                context.RequestAborted).ConfigureAwait(false);
        }
        await context.Response.WriteAsync(
            $"event: {eventName}\ndata: {data}\n\n",
            context.RequestAborted).ConfigureAwait(false);
        await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
    }

    private static TelemetryEventQuery ParseHistoryQuery(
        IQueryCollection values,
        RtxMonitorServiceOptions options)
    {
        int limit = ParseInt32(values, "limit") ?? Math.Min(100, options.HistoryMaximumLimit);
        if (limit is < 1 || limit > options.HistoryMaximumLimit)
        {
            throw new ArgumentException(
                $"limit deve estar entre 1 e {options.HistoryMaximumLimit}.");
        }

        string order = OptionalQueryValue(values, "order") ?? "desc";
        bool ascending = order switch
        {
            "asc" => true,
            "desc" => false,
            _ => throw new ArgumentException("order deve ser asc ou desc."),
        };
        string? runId = OptionalQueryValue(values, "run_id");
        ulong? afterSequence = ParseUInt64(values, "after_sequence");
        if (afterSequence is not null && runId is null)
        {
            throw new ArgumentException("after_sequence exige run_id.");
        }

        return new TelemetryEventQuery(
            RunId: runId,
            TargetGpuUuid: OptionalQueryValue(values, "gpu_uuid"),
            EventKind: ParseEventKind(OptionalQueryValue(values, "event_type")),
            FromUnixMilliseconds: ParseInt64(values, "from_unix_ms"),
            ToUnixMilliseconds: ParseInt64(values, "to_unix_ms"),
            AfterSequence: afterSequence,
            AfterEventId: ParseInt64(values, "after_event_id"),
            Limit: limit,
            Ascending: ascending);
    }

    private static ServiceHealthResponse BuildHealth(
        MonitoringRuntimeSnapshot snapshot,
        TelemetryEventHub hub)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long started = snapshot.StartedAt.ToUnixTimeMilliseconds();
        return new ServiceHealthResponse(
            ApiSchemaVersion,
            snapshot.Status,
            snapshot.Ready,
            typeof(ServiceEndpoints).Assembly.GetName().Version?.ToString(3) ?? "unknown",
            started,
            Math.Max(0, now - started),
            new StorageHealthResponse(
                snapshot.Storage.State,
                snapshot.Storage.DatabasePath,
                snapshot.Storage.SchemaVersion,
                snapshot.Storage.ChangedAt.ToUnixTimeMilliseconds(),
                snapshot.Storage.LastError),
            new DiscoveryHealthResponse(
                snapshot.Discovery.State,
                snapshot.Discovery.LastAttemptAt?.ToUnixTimeMilliseconds(),
                snapshot.Discovery.LastSuccessAt?.ToUnixTimeMilliseconds(),
                snapshot.Discovery.LastError),
            new CollectorSummaryResponse(snapshot.ActiveCollectors, snapshot.Gpus.Count),
            new SseSummaryResponse(
                hub.ConnectedClients,
                hub.MaximumClients,
                hub.QueueCapacity));
    }

    private static GpuRuntimeResponse BuildGpu(GpuRuntimeSnapshot gpu) => new(
        gpu.Gpu.Index,
        gpu.Gpu.Name,
        gpu.Gpu.Uuid,
        gpu.Gpu.DriverVersion,
        gpu.Gpu.NvmlVersion,
        gpu.Present,
        gpu.CollectorState,
        gpu.RunId,
        gpu.ProfileKey,
        gpu.BoardCaptureState,
        gpu.BoardCaptureError,
        gpu.LastEventType,
        gpu.LastEventAt?.ToUnixTimeMilliseconds(),
        gpu.TemperatureC,
        gpu.TemperatureCapturedAt?.ToUnixTimeMilliseconds(),
        gpu.ConsecutiveFailures,
        gpu.LastError);

    private static CapabilitiesResponse BuildCapabilities(DiscoveredGpu discovered)
    {
        BoardResponse? boardResponse = BuildBoard(discovered);
        ThermalProviderResponse[] providers = discovered.ThermalReport?.Providers.Select(
            provider => new ThermalProviderResponse(
                provider.ProviderName,
                provider.StateName,
                provider.NativeStatus,
                provider.CapabilityCount)).ToArray() ?? [];
        ThermalCapabilityResponse[] capabilities = discovered.ThermalReport?.Capabilities.Select(
            capability => new ThermalCapabilityResponse(
                capability.ProviderName,
                capability.ProviderNativeId,
                capability.TargetName,
                capability.ControllerName,
                capability.StateName,
                capability.ConfidenceName,
                capability.CurrentTemperatureC,
                capability.DefaultMinimumTemperatureC,
                capability.DefaultMaximumTemperatureC,
                capability.NativeStatus)).ToArray() ?? [];

        return new CapabilitiesResponse(
            ApiSchemaVersion,
            discovered.CapturedAt.ToUnixTimeMilliseconds(),
            new GpuIdentityResponse(
                discovered.Gpu.Index,
                discovered.Gpu.Name,
                discovered.Gpu.Uuid,
                discovered.Gpu.DriverVersion,
                discovered.Gpu.NvmlVersion),
            boardResponse,
            discovered.Evidence.BoardStateName,
            discovered.Evidence.BoardError,
            discovered.ThermalError,
            providers,
            capabilities);
    }

    private static PublicTelemetryResponse BuildTelemetry(GpuRuntimeSnapshot gpu)
    {
        PublicTelemetryReport report = gpu.PublicTelemetry!;
        PublicTelemetryCoverage coverage = report.Coverage;
        PublicTelemetryFieldResponse[] fields = report.Fields.Select(field =>
            new PublicTelemetryFieldResponse(
                field.FieldName,
                field.ProviderName,
                field.ProviderNativeId,
                field.StateName,
                field.OriginName,
                field.ValueTypeName,
                field.UnitName,
                field.UnsignedValue,
                field.SignedValue,
                field.DoubleValue,
                field.NativeStatus,
                checked((long)field.TimestampUnixMilliseconds))).ToArray();
        ComputedMetricsResponse? computed = gpu.ComputedMetrics is ComputedMetricsReport metrics
            ? new ComputedMetricsResponse(
                checked((long)metrics.TimestampUnixMilliseconds),
                metrics.Metrics.Select(metric => new ComputedMetricResponse(
                    metric.KindName,
                    metric.StateName,
                    metric.OriginName,
                    metric.UnitName,
                    metric.Formula,
                    metric.Value,
                    checked((long)metric.WindowMilliseconds),
                    metric.SampleCount,
                    metric.TemperatureThresholdC,
                    metric.InputNames)).ToArray())
            : null;
        DiscoveredGpu? discovered = gpu.Capabilities;
        PerformanceLimitReasonReport? reasons = PerformanceLimitReasons.From(report);

        return new PublicTelemetryResponse(
            2,
            checked((long)report.TimestampUnixMilliseconds),
            new GpuIdentityResponse(
                gpu.Gpu.Index,
                gpu.Gpu.Name,
                gpu.Gpu.Uuid,
                gpu.Gpu.DriverVersion,
                gpu.Gpu.NvmlVersion),
            discovered is null ? null : BuildBoard(discovered),
            gpu.BoardCaptureState,
            gpu.BoardCaptureError,
            gpu.CollectorState,
            new PublicTelemetryCoverageResponse(
                coverage.Total,
                coverage.Available,
                coverage.NotSupported,
                coverage.ProviderUnavailable,
                coverage.QueryFailed),
            reasons is null ? null : new PerformanceLimitReasonsResponse(
                reasons.RawBitmask,
                reasons.ActiveReasons,
                reasons.PrimaryReason),
            fields,
            computed);
    }

    private static WindowsTelemetryResponse BuildWindowsTelemetry(WindowsTelemetrySnapshot snapshot) =>
        new(
            snapshot.SchemaVersion,
            snapshot.CapturedAt.ToUnixTimeMilliseconds(),
            snapshot.State,
            snapshot.Error,
            new GpuIdentityResponse(
                snapshot.Gpu.Index, snapshot.Gpu.Name, snapshot.Gpu.Uuid,
                snapshot.Gpu.DriverVersion, snapshot.Gpu.NvmlVersion),
            snapshot.Adapter is null ? null : new WindowsAdapterIdentityResponse(
                $"0x{unchecked((ulong)snapshot.Adapter.Luid):x16}",
                snapshot.Adapter.Description,
                snapshot.Adapter.VendorId,
                snapshot.Adapter.DeviceId,
                snapshot.Adapter.SubsystemVendorId,
                snapshot.Adapter.SubsystemDeviceId),
            BuildWindowsMetric(snapshot.LocalMemory),
            BuildWindowsMetric(snapshot.NonLocalMemory),
            snapshot.Engines.Select(engine => new WindowsEngineResponse(
                engine.EngineType, BuildWindowsMetric(engine.Utilization))).ToArray());

    private static WindowsMetricResponse BuildWindowsMetric(WindowsTelemetryMetric metric) =>
        new(metric.State, metric.Value, metric.Unit, metric.Error);

    private static BoardResponse? BuildBoard(DiscoveredGpu discovered)
    {
        BoardIdentity? board = discovered.Evidence.Board;
        return board is null
            ? null
            : new BoardResponse(
                (uint)board.Flags,
                board.HasPciIdentity,
                board.PciBusId,
                board.PciVendorId,
                board.PciDeviceId,
                board.PciSubsystemVendorId,
                board.PciSubsystemDeviceId,
                board.PciDomain,
                board.PciBus,
                board.PciDevice,
                board.PciFunction,
                board.HasVbiosVersion,
                board.HasVbiosVersion ? board.VbiosVersion : null,
                discovered.Evidence.ProfileKey);
    }

    private static TelemetryEventKind? ParseEventKind(string? value) => value switch
    {
        null => null,
        "sample" => TelemetryEventKind.Sample,
        "gap" => TelemetryEventKind.Gap,
        "recovered" => TelemetryEventKind.Recovered,
        "alert_raised" => TelemetryEventKind.AlertRaised,
        "alert_cleared" => TelemetryEventKind.AlertCleared,
        _ => throw new ArgumentException(
            "event_type deve ser sample, gap, recovered, alert_raised ou alert_cleared."),
    };

    private static string? OptionalQueryValue(IQueryCollection values, string name)
    {
        if (!values.TryGetValue(name, out var value) || value.Count == 0)
        {
            return null;
        }
        if (value.Count != 1 || string.IsNullOrWhiteSpace(value[0]))
        {
            throw new ArgumentException($"{name} deve possuir exatamente um valor não vazio.");
        }
        if (value[0]!.Length > 256)
        {
            throw new ArgumentException($"{name} não pode exceder 256 caracteres.");
        }

        return value[0];
    }

    private static int? ParseInt32(IQueryCollection values, string name)
    {
        string? value = OptionalQueryValue(values, name);
        if (value is null)
        {
            return null;
        }

        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : throw new ArgumentException($"{name} possui valor inválido: {value}.");
    }

    private static long? ParseInt64(IQueryCollection values, string name)
    {
        string? value = OptionalQueryValue(values, name);
        if (value is null)
        {
            return null;
        }

        return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long parsed)
            ? parsed
            : throw new ArgumentException($"{name} possui valor inválido: {value}.");
    }

    private static ulong? ParseUInt64(IQueryCollection values, string name)
    {
        string? value = OptionalQueryValue(values, name);
        if (value is null)
        {
            return null;
        }

        return ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out ulong parsed)
            ? parsed
            : throw new ArgumentException($"{name} possui valor inválido: {value}.");
    }
}
