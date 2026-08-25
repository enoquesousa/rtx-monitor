using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using RtxMonitor.Managed;
using RtxMonitor.Service;
using RtxMonitor.Storage;

namespace RtxMonitor.Service.Tests;

internal static class Program
{
    private static int failures;

    private static async Task<int> Main()
    {
        Run("options validation", TestOptionsValidation);
        Run("bounded SSE hub", TestBoundedEventHub);
        Run("SSE recovery cursor", TestRecoveryEndpoint);
        Run("runtime health transitions", TestRuntimeHealthTransitions);
        await RunAsync("HTTP and SSE contracts", TestHttpAndSseContractsAsync)
            .ConfigureAwait(false);
        await RunAsync("single collector and graceful stop", TestSingleCollectorAndStopAsync)
            .ConfigureAwait(false);
        await RunAsync("storage recovery", TestStorageRecoveryAsync).ConfigureAwait(false);

        if (failures == 0)
        {
            Console.WriteLine("RtxMonitor.Service tests passed");
        }

        return failures == 0 ? 0 : 1;
    }

    private static void TestOptionsValidation()
    {
        using var temporary = new TemporaryWorkspace();
        IConfiguration validConfiguration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["RtxMonitor:Port"] = "5137",
                    ["RtxMonitor:DatabasePath"] = temporary.DatabasePath,
                    ["RtxMonitor:IntervalMilliseconds"] = "250",
                    ["RtxMonitor:SseClientQueueCapacity"] = "8",
                    ["RtxMonitor:MaximumSseClients"] = "2",
                })
            .Build();
        RtxMonitorServiceOptions options =
            RtxMonitorServiceOptions.FromConfiguration(validConfiguration);
        Check(options.Port == 5137, "porta configurada deve ser preservada");
        Check(
            options.DatabasePath == Path.GetFullPath(temporary.DatabasePath),
            "caminho do banco deve ser absoluto");

        IConfiguration relativeConfiguration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["RtxMonitor:DatabasePath"] = "data\\relative.db",
                })
            .Build();
        RtxMonitorServiceOptions relativeOptions =
            RtxMonitorServiceOptions.FromConfiguration(relativeConfiguration);
        Check(
            relativeOptions.DatabasePath == Path.GetFullPath(
                "data\\relative.db",
                AppContext.BaseDirectory),
            "caminho relativo deve usar a pasta do executável como base");

        IConfiguration invalidConfiguration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["RtxMonitor:Port"] = "0",
                    ["RtxMonitor:DatabasePath"] = temporary.DatabasePath,
                })
            .Build();
        Check(
            Throws<InvalidOperationException>(
                () => RtxMonitorServiceOptions.FromConfiguration(invalidConfiguration)),
            "porta zero deve ser recusada na configuração de produção");
    }

    private static void TestBoundedEventHub()
    {
        using var temporary = new TemporaryWorkspace();
        RtxMonitorServiceOptions options = TestOptions(
            temporary.DatabasePath,
            queueCapacity: 1,
            maximumClients: 1);
        var hub = new TelemetryEventHub(options);
        using TelemetryEventHub.TelemetrySubscription subscription = hub.Subscribe();
        Check(hub.ConnectedClients == 1, "assinante deve ser contabilizado");
        Check(
            Throws<TelemetrySubscriberLimitException>(() => hub.Subscribe()),
            "limite de clientes deve ser aplicado antes de criar outra fila");

        TelemetryEvent telemetryEvent = FakeTelemetryEvent(1);
        hub.Publish(1, "run-test", FakeGpu().Uuid, telemetryEvent);
        hub.Publish(2, "run-test", FakeGpu().Uuid, telemetryEvent);
        hub.Publish(3, "run-test", FakeGpu().Uuid, telemetryEvent);
        hub.Publish(4, "run-test", FakeGpu().Uuid, telemetryEvent);
        TelemetryDeliveryBatch batch = subscription.TakeBatch();
        Check(
            batch.Records.Count == 1 && batch.Records[0].EventId == 1,
            "a fila limitada deve preservar o primeiro evento aceito");
        StreamDropSnapshot dropped = batch.Dropped;
        Check(
            dropped.Count == 3,
            "eventos novos devem permanecer na lacuna até o consumidor receber o aviso");
        Check(dropped.LatestEventId == 4, "lacuna deve informar o último event_id descartado");
        hub.Publish(5, "run-test", FakeGpu().Uuid, telemetryEvent);
        TelemetryDeliveryBatch recovered = subscription.TakeBatch();
        Check(
            recovered.Records.Count == 1 && recovered.Records[0].EventId == 5 &&
            recovered.Dropped.Count == 0,
            "a fila deve reabrir somente depois de consumir a lacuna");

        subscription.Dispose();
        Check(hub.ConnectedClients == 0, "dispose deve remover o assinante");

        using TelemetryEventHub.TelemetrySubscription filtered = hub.Subscribe("GPU-OTHER");
        hub.Publish(6, "run-test", FakeGpu().Uuid, telemetryEvent);
        Check(
            filtered.TakeBatch().Records.Count == 0,
            "assinante filtrado não deve receber evento de outra GPU");
        Check(
            Throws<ArgumentException>(
                () => hub.Publish(7, "run-test", "GPU-DIVERGENT", telemetryEvent)),
            "UUID do envelope deve coincidir com o evento persistido");
    }

    private static void TestRuntimeHealthTransitions()
    {
        using var temporary = new TemporaryWorkspace();
        RtxMonitorServiceOptions options = TestOptions(temporary.DatabasePath);
        var state = new MonitoringState(options);
        Check(!state.GetSnapshot().Ready, "estado inicial não deve estar pronto");

        state.MarkStorageAvailable(SqliteTelemetryStore.CurrentSchemaVersion);
        DiscoveredGpu discovered = FakeDiscoveredGpu();
        state.RecordDiscoverySuccess([discovered]);
        state.RecordCollectorStarted(discovered.Gpu.Uuid, "run-health");
        MonitoringRuntimeSnapshot healthy = state.GetSnapshot();
        Check(healthy.Ready, "storage e discovery disponíveis devem tornar o serviço pronto");
        Check(healthy.Status == "healthy", "coletor ativo deve produzir saúde normal");

        state.RecordTelemetry(discovered.Gpu.Uuid, FakeGapEvent(1));
        Check(state.GetSnapshot().Status == "degraded", "gap deve degradar a saúde");
        state.RecordTelemetry(discovered.Gpu.Uuid, FakeTelemetryEvent(2));
        Check(state.GetSnapshot().Status == "healthy", "nova amostra deve recuperar a saúde");
    }

    private static void TestRecoveryEndpoint()
    {
        Check(
            ServiceEndpoints.BuildRecoveryEndpoint(null, null) ==
            "/api/v1/history?order=asc&after_event_id=0",
            "stream sem cursor deve recuperar desde o início");
        Check(
            ServiceEndpoints.BuildRecoveryEndpoint(17, "GPU FILTER/1") ==
            "/api/v1/history?order=asc&after_event_id=17&gpu_uuid=GPU%20FILTER%2F1",
            "cursor de recuperação deve preservar e escapar o filtro da GPU");
    }

    private static async Task TestHttpAndSseContractsAsync()
    {
        using var temporary = new TemporaryWorkspace();
        RtxMonitorServiceOptions options = TestOptions(temporary.DatabasePath);
        var state = new MonitoringState(options);
        state.MarkStorageAvailable(SqliteTelemetryStore.CurrentSchemaVersion);
        DiscoveredGpu discovered = FakeDiscoveredGpu();
        state.RecordDiscoverySuccess([discovered]);
        state.RecordCollectorStarted(discovered.Gpu.Uuid, "run-http");
        var hub = new TelemetryEventHub(options);
        var history = new RecordingHistorySource();

        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(
            server => server.Listen(
                IPAddress.Loopback,
                0,
                listen => listen.Protocols = HttpProtocols.Http1));
        builder.Services.ConfigureHttpJsonOptions(
            json => json.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<IMonitoringSnapshotSource>(state);
        builder.Services.AddSingleton<IHistorySource>(history);
        builder.Services.AddSingleton(hub);
        await using WebApplication application = builder.Build();
        ServiceEndpoints.Map(application);
        await application.StartAsync().ConfigureAwait(false);

        Uri address = GetServerAddress(application);
        using var client = new HttpClient
        {
            BaseAddress = address,
            Timeout = TimeSpan.FromSeconds(5),
        };

        using (HttpResponseMessage healthResponse = await client.GetAsync("/health")
            .ConfigureAwait(false))
        {
            Check(healthResponse.StatusCode == HttpStatusCode.OK, "health pronto deve retornar 200");
            using JsonDocument health = JsonDocument.Parse(
                await healthResponse.Content.ReadAsStringAsync().ConfigureAwait(false));
            Check(
                health.RootElement.GetProperty("schema_version").GetInt32() == 1,
                "health deve declarar schema 1");
            Check(
                health.RootElement.GetProperty("ready").GetBoolean(),
                "health deve declarar readiness");
        }

        using (HttpResponseMessage gpuResponse = await client.GetAsync("/api/v1/gpus")
            .ConfigureAwait(false))
        {
            using JsonDocument gpuJson = JsonDocument.Parse(
                await gpuResponse.Content.ReadAsStringAsync().ConfigureAwait(false));
            Check(gpuJson.RootElement.GetProperty("count").GetInt32() == 1, "API deve listar GPU");
        }

        using (HttpResponseMessage capabilityResponse = await client.GetAsync(
            $"/api/v1/gpus/{Uri.EscapeDataString(discovered.Gpu.Uuid)}/capabilities")
            .ConfigureAwait(false))
        {
            Check(
                capabilityResponse.StatusCode == HttpStatusCode.OK,
                "capabilities conhecidas devem retornar 200");
        }

        using (HttpResponseMessage historyResponse = await client.GetAsync(
            "/api/v1/history?limit=7&order=asc&event_type=sample")
            .ConfigureAwait(false))
        {
            Check(historyResponse.StatusCode == HttpStatusCode.OK, "histórico válido deve retornar 200");
            Check(history.LastQuery?.Limit == 7, "limite deve alcançar o storage");
            Check(history.LastQuery?.Ascending == true, "ordem ascendente deve alcançar o storage");
            Check(
                history.LastQuery?.EventKind == TelemetryEventKind.Sample,
                "filtro de evento deve alcançar o storage");
        }

        using (HttpResponseMessage invalidHistory = await client.GetAsync(
            "/api/v1/history?limit=1001")
            .ConfigureAwait(false))
        {
            Check(
                invalidHistory.StatusCode == HttpStatusCode.BadRequest,
                "consulta acima do limite deve retornar 400");
        }

        using (HttpResponseMessage invalidEvents = await client.GetAsync(
            "/api/v1/events?gpu_uuid=%20")
            .ConfigureAwait(false))
        {
            Check(
                invalidEvents.StatusCode == HttpStatusCode.BadRequest,
                "filtro SSE vazio deve retornar 400");
        }

        TelemetryEventHub.TelemetrySubscription[] saturatedSubscriptions = Enumerable
            .Range(0, options.MaximumSseClients)
            .Select(_ => hub.Subscribe())
            .ToArray();
        try
        {
            using HttpResponseMessage saturatedEvents = await client.GetAsync(
                "/api/v1/events").ConfigureAwait(false);
            Check(
                saturatedEvents.StatusCode == HttpStatusCode.ServiceUnavailable,
                "limite de clientes SSE deve retornar 503");
            Check(
                saturatedEvents.Content.Headers.ContentType?.MediaType ==
                "application/problem+json",
                "limite SSE deve usar Problem Details");
        }
        finally
        {
            foreach (TelemetryEventHub.TelemetrySubscription saturated in
                saturatedSubscriptions)
            {
                saturated.Dispose();
            }
        }

        using var streamCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/events");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using HttpResponseMessage eventResponse = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            streamCancellation.Token).ConfigureAwait(false);
        Check(eventResponse.StatusCode == HttpStatusCode.OK, "SSE deve retornar 200");
        await using Stream stream = await eventResponse.Content.ReadAsStreamAsync(
            streamCancellation.Token).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        string? banner = await reader.ReadLineAsync(streamCancellation.Token).ConfigureAwait(false);
        Check(banner?.StartsWith(": rtx-monitor", StringComparison.Ordinal) == true, "SSE deve iniciar com comentário");
        _ = await reader.ReadLineAsync(streamCancellation.Token).ConfigureAwait(false);

        TelemetryEvent liveEvent = FakeTelemetryEvent(1);
        hub.Publish(42, "run-http", liveEvent.TargetGpuUuid, liveEvent);
        string? idLine = await reader.ReadLineAsync(streamCancellation.Token).ConfigureAwait(false);
        string? eventLine = await reader.ReadLineAsync(streamCancellation.Token).ConfigureAwait(false);
        string? dataLine = await reader.ReadLineAsync(streamCancellation.Token).ConfigureAwait(false);
        Check(idLine == "id: 42", "SSE deve usar event_id persistido como cursor");
        Check(eventLine == "event: telemetry", "SSE deve nomear o evento de telemetria");
        Check(dataLine?.StartsWith("data: {", StringComparison.Ordinal) == true, "SSE deve emitir JSON");

        eventResponse.Dispose();
        await application.StopAsync().ConfigureAwait(false);
    }

    private static async Task TestSingleCollectorAndStopAsync()
    {
        using var temporary = new TemporaryWorkspace();
        RtxMonitorServiceOptions options = TestOptions(
            temporary.DatabasePath,
            discoveryInterval: TimeSpan.FromMilliseconds(100),
            retryInterval: TimeSpan.FromMilliseconds(100));
        var state = new MonitoringState(options);
        var storeProvider = new TelemetryStoreProvider();
        var hub = new TelemetryEventHub(options);
        var backend = new FakeMonitoringBackend();
        var worker = new GpuMonitoringWorker(
            options,
            state,
            storeProvider,
            hub,
            backend,
            NullLogger<GpuMonitoringWorker>.Instance);

        await worker.StartAsync(CancellationToken.None).ConfigureAwait(false);
        await WaitUntilAsync(
            () => backend.SamplerCreations == 1 &&
                  state.GetSnapshot().Gpus.FirstOrDefault()?.TemperatureC == 47,
            TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await Task.Delay(350).ConfigureAwait(false);
        Check(backend.DiscoveryCalls >= 2, "supervisor deve repetir discovery");
        Check(backend.SamplerCreations == 1, "um UUID não pode receber dois coletores simultâneos");

        await worker.StopAsync(CancellationToken.None).ConfigureAwait(false);
        SqliteConnection.ClearAllPools();
        SqliteTelemetryStore store = SqliteTelemetryStore.Open(
            new TelemetryStoreOptions(
                temporary.DatabasePath,
                openMode: TelemetryStoreOpenMode.OpenExisting));
        IReadOnlyList<StoredTelemetryEvidence> records = store.QueryEvents(
            new TelemetryEventQuery(Limit: 1000, Ascending: true));
        Check(records.Count > 0, "coletor deve persistir eventos");
        Check(
            records.All(record => record.Run.CompletedAt is not null),
            "encerramento gracioso deve confirmar completed_at do run");
        Check(
            records.All(record => record.Run.CompletionReason == "service_stopped"),
            "encerramento gracioso deve registrar service_stopped");
    }

    private static async Task TestStorageRecoveryAsync()
    {
        using var temporary = new TemporaryWorkspace();
        File.WriteAllText(temporary.DatabasePath, "arquivo SQLite inválido");
        RtxMonitorServiceOptions options = TestOptions(
            temporary.DatabasePath,
            discoveryInterval: TimeSpan.FromMilliseconds(100),
            retryInterval: TimeSpan.FromMilliseconds(100));
        var state = new MonitoringState(options);
        var worker = new GpuMonitoringWorker(
            options,
            state,
            new TelemetryStoreProvider(),
            new TelemetryEventHub(options),
            new FakeMonitoringBackend(),
            NullLogger<GpuMonitoringWorker>.Instance);

        await worker.StartAsync(CancellationToken.None).ConfigureAwait(false);
        await WaitUntilAsync(
            () => state.GetSnapshot().Storage.State == "unavailable",
            TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        SqliteConnection.ClearAllPools();
        File.Delete(temporary.DatabasePath);
        await WaitUntilAsync(
            () => state.GetSnapshot().Storage.State == "available" &&
                  state.GetSnapshot().Discovery.State == "available",
            TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await worker.StopAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private static Uri GetServerAddress(WebApplication application)
    {
        IServer server = application.Services.GetRequiredService<IServer>();
        IServerAddressesFeature addresses = server.Features.Get<IServerAddressesFeature>() ??
            throw new InvalidOperationException("Kestrel não publicou endereço de teste.");
        return new Uri(addresses.Addresses.Single(), UriKind.Absolute);
    }

    private static RtxMonitorServiceOptions TestOptions(
        string databasePath,
        int queueCapacity = 8,
        int maximumClients = 4,
        TimeSpan? discoveryInterval = null,
        TimeSpan? retryInterval = null) => new(
            5136,
            Path.GetFullPath(databasePath),
            100,
            32,
            30,
            discoveryInterval ?? TimeSpan.FromSeconds(1),
            retryInterval ?? TimeSpan.FromSeconds(1),
            queueCapacity,
            maximumClients,
            TimeSpan.FromSeconds(1),
            1000,
            null,
            0);

    private static GpuInfo FakeGpu() =>
        new(0, "Fake NVIDIA RTX", "GPU-SERVICE-TEST", "999.1", "99.1");

    private static DiscoveredGpu FakeDiscoveredGpu()
    {
        GpuInfo gpu = FakeGpu();
        var board = new BoardIdentity(
            gpu.Index,
            0x10de,
            0x2504,
            0x10de,
            0x1536,
            0,
            1,
            0,
            0,
            BoardIdentityFlags.PciValid | BoardIdentityFlags.VbiosValid,
            "00000000:01:00.0",
            "TEST-VBIOS");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new DiscoveredGpu(
            gpu,
            new GpuEvidenceSnapshot(
                gpu,
                board,
                BoardEvidenceState.Available,
                null,
                now),
            new ThermalReport(gpu.Index, now, checked((ulong)now.ToUnixTimeMilliseconds()), [], []),
            null,
            now);
    }

    private static TelemetryEvent FakeTelemetryEvent(ulong sequence)
    {
        GpuInfo gpu = FakeGpu();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ulong timestamp = checked((ulong)now.ToUnixTimeMilliseconds());
        var sample = new TemperatureSample(
            gpu.Index,
            47,
            TemperatureBackend.NvmlTemperatureV1,
            "fake",
            now,
            timestamp);
        return new TelemetryEvent(
            sequence,
            TelemetryEventKind.Sample,
            gpu.Uuid,
            gpu,
            sample,
            now,
            timestamp,
            MonitoringStatus.Ok,
            "ok",
            string.Empty,
            0,
            0);
    }

    private static TelemetryEvent FakeGapEvent(ulong sequence)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new TelemetryEvent(
            sequence,
            TelemetryEventKind.Gap,
            FakeGpu().Uuid,
            null,
            null,
            now,
            checked((ulong)now.ToUnixTimeMilliseconds()),
            MonitoringStatus.GpuLost,
            "GPU is inaccessible or lost",
            "falha simulada",
            1,
            250);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("A condição de teste não foi atingida no prazo.");
            }

            await Task.Delay(25).ConfigureAwait(false);
        }
    }

    private static void Run(string name, Action test)
    {
        try
        {
            test();
        }
        catch (Exception error)
        {
            failures++;
            Console.Error.WriteLine($"FAILED: {name}: {error}");
        }
    }

    private static async Task RunAsync(string name, Func<Task> test)
    {
        try
        {
            await test().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            failures++;
            Console.Error.WriteLine($"FAILED: {name}: {error}");
        }
    }

    private static bool Throws<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
            return false;
        }
        catch (TException)
        {
            return true;
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class RecordingHistorySource : IHistorySource
    {
        internal TelemetryEventQuery? LastQuery { get; private set; }

        public IReadOnlyList<StoredTelemetryEvidence> Query(TelemetryEventQuery query)
        {
            LastQuery = query;
            return [];
        }
    }

    private sealed class FakeMonitoringBackend : IMonitoringBackend
    {
        private int discoveryCalls;
        private int samplerCreations;

        internal int DiscoveryCalls => Volatile.Read(ref discoveryCalls);

        internal int SamplerCreations => Volatile.Read(ref samplerCreations);

        public IReadOnlyList<DiscoveredGpu> Discover()
        {
            Interlocked.Increment(ref discoveryCalls);
            return [FakeDiscoveredGpu()];
        }

        public GpuEvidenceSnapshot CaptureEvidence(GpuInfo gpu) =>
            FakeDiscoveredGpu().Evidence with { Gpu = gpu };

        public ITelemetrySampler CreateSampler(string gpuUuid, SamplingOptions options)
        {
            Interlocked.Increment(ref samplerCreations);
            return new FakeSampler();
        }
    }

    private sealed class FakeSampler : ITelemetrySampler
    {
        private ulong sequence;

        public IReadOnlyList<TelemetryEvent> Poll() => [FakeTelemetryEvent(++sequence)];

        public uint NextDelayMilliseconds(uint successfulSampleIntervalMilliseconds) =>
            successfulSampleIntervalMilliseconds;

        public void Dispose()
        {
        }
    }

    private sealed class TemporaryWorkspace : IDisposable
    {
        private readonly string directory;

        internal TemporaryWorkspace()
        {
            directory = Path.Combine(
                Path.GetTempPath(),
                $"rtx-monitor-service-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            DatabasePath = Path.Combine(directory, "telemetry.db");
        }

        internal string DatabasePath { get; }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            string resolvedDirectory = Path.GetFullPath(directory);
            string resolvedTemp = Path.GetFullPath(Path.GetTempPath());
            if (!resolvedDirectory.StartsWith(resolvedTemp, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(resolvedDirectory).StartsWith(
                    "rtx-monitor-service-",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Recusa em remover diretório temporário inesperado: {resolvedDirectory}");
            }

            Directory.Delete(resolvedDirectory, recursive: true);
        }
    }
}
