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
        Run("DXGI PCI identity gate", TestWindowsIdentityGate);
        Run("Windows telemetry fixtures", TestWindowsTelemetryFixtures);
        Run("WDDM engine aggregation", TestWindowsEngineAggregation);
        await RunAsync("HTTP and SSE contracts", TestHttpAndSseContractsAsync)
            .ConfigureAwait(false);
        await RunAsync("single collector and graceful stop", TestSingleCollectorAndStopAsync)
            .ConfigureAwait(false);
        await RunAsync("Windows telemetry recovery", TestWindowsTelemetryRecoveryAsync)
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
        Check(options.MetricWindowMilliseconds == 5000, "janela métrica padrão deve ser estável");
        Check(options.MetricTemperatureThresholdC == 80, "limiar métrico padrão deve ser estável");

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

    private static void TestWindowsIdentityGate()
    {
        BoardIdentity board = FakeDiscoveredGpu().Evidence.Board!;
        var expected = new WindowsAdapterIdentity(
            0x1669b, "Fake RTX 3060", 0x10de, 0x2504, 0x10de, 0x1536);
        var wrongLuidPeer = new WindowsAdapterIdentity(
            0x17974, "Other adapter", 0x8086, 0x4680, 0x0000, 0x0000);
        Check(
            ReferenceEquals(WindowsGpuReader.MatchAdapter(board, [wrongLuidPeer, expected]), expected),
            "correlação deve selecionar o LUID somente após casar todos os IDs PCI");
        Check(
            WindowsGpuReader.MatchAdapter(board, [expected with { SubsystemDeviceId = 0x9999 }]) is null,
            "subsystem incompatível deve fechar o gate de identidade");
        Check(
            WindowsGpuReader.MatchAdapter(board, []) is null,
            "GPU DXGI ausente deve fechar o gate de identidade");
        Check(
            WindowsGpuReader.MatchAdapter(board, [expected, expected with { Luid = 0x9999 }]) is null,
            "identidade PCI ambígua não pode escolher adaptador por ordem");
    }

    private static void TestWindowsTelemetryFixtures()
    {
        DiscoveredGpu gpu = FakeDiscoveredGpu();
        var adapter = new WindowsAdapterIdentity(
            0x1669b, "Fake RTX 3060", 0x10de, 0x2504, 0x10de, 0x1536);
        var complete = WindowsGpuReader.EngineTypes.ToDictionary(
            type => type,
            type => (double?)(type == "3D" ? 12.5 : 0),
            StringComparer.OrdinalIgnoreCase);
        var reader = new WindowsGpuReader(
            new FakeAdapterSource([adapter]),
            new FakePdhSource(new PdhGpuSample(658640896, 120369152, complete)));
        WindowsTelemetrySnapshot available = reader.Read(gpu, CancellationToken.None);
        Check(available.State == "available", "fixture completa deve produzir snapshot disponível");
        Check(available.Engines.Count == 6, "catálogo WDDM deve conter seis tipos estáveis");
        Check(available.Engines.Single(item => item.EngineType == "Copy").Utilization.State == "inactive",
            "zero observado deve ser inativo, não indisponível");

        var partialReader = new WindowsGpuReader(
            new FakeAdapterSource([adapter]),
            new FakePdhSource(new PdhGpuSample(1, null,
                new Dictionary<string, double?> { ["3D"] = 1 })));
        WindowsTelemetrySnapshot partial = partialReader.Read(gpu, CancellationToken.None);
        Check(partial.State == "partial" && partial.NonLocalMemory.Value is null,
            "counter individual ausente deve produzir estado parcial");
        Check(partial.Engines.Single(item => item.EngineType == "Copy").Utilization.State ==
            "counter_unavailable", "tipo sem leitura não pode virar zero");

        var unavailableReader = new WindowsGpuReader(
            new FakeAdapterSource([adapter]),
            new ThrowingPdhSource("GPU removida durante a coleta"));
        WindowsTelemetrySnapshot unavailable = unavailableReader.Read(gpu, CancellationToken.None);
        Check(unavailable.State == "counters_unavailable" && unavailable.Engines.Count == 6,
            "falha PDH deve preservar catálogo com estados explícitos");

        var dxgiFailureReader = new WindowsGpuReader(
            new ThrowingAdapterSource(),
            new FakePdhSource(new PdhGpuSample(1, 1, complete)));
        WindowsTelemetrySnapshot dxgiFailure = dxgiFailureReader.Read(gpu, CancellationToken.None);
        Check(dxgiFailure.State == "identity_unavailable" && dxgiFailure.Adapter is null,
            "falha DXGI deve fechar o gate antes do PDH");
    }

    private static void TestWindowsEngineAggregation()
    {
        IReadOnlyDictionary<string, double?> values = PdhGpuCounterSource.AggregateEngineReadings(
        [
            new PdhEngineReading("3D", "0", 60),
            new PdhEngineReading("3D", "0", 55),
            new PdhEngineReading("3D", "1", 40),
            new PdhEngineReading("Copy", "0", null),
        ]);
        Check(values["3D"] == 100,
            "processos do mesmo engine físico devem somar com limite de 100%");
        Check(values["Copy"] is null,
            "engine sem amostra válida deve continuar indisponível");
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
        state.RecordTelemetry(discovered.Gpu.Uuid, FakeTelemetryEvent(1));
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
        builder.Services.AddSingleton<IWindowsTelemetrySnapshotSource>(new FakeWindowsTelemetrySource());
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

        using (HttpResponseMessage telemetryResponse = await client.GetAsync(
            $"/api/v1/gpus/{Uri.EscapeDataString(discovered.Gpu.Uuid)}/telemetry")
            .ConfigureAwait(false))
        {
            Check(
                telemetryResponse.StatusCode == HttpStatusCode.OK,
                "telemetria conhecida deve retornar 200");
            using JsonDocument telemetry = JsonDocument.Parse(
                await telemetryResponse.Content.ReadAsStringAsync().ConfigureAwait(false));
            JsonElement root = telemetry.RootElement;
            Check(root.GetProperty("coverage").GetProperty("available").GetInt32() == 1,
                "cobertura deve preservar campos disponíveis");
            Check(root.GetProperty("fields")[0].GetProperty("provider").GetString() == "NVML fake",
                "endpoint deve preservar a proveniência do campo");
            Check(root.GetProperty("computed_metrics").GetProperty("metrics")[0]
                    .GetProperty("formula").GetString() == "mean(gpu_die_temperature_c within window)",
                "endpoint deve expor fórmula reproduzível");
        }

        using (HttpResponseMessage windowsResponse = await client.GetAsync(
            $"/api/v1/gpus/{Uri.EscapeDataString(discovered.Gpu.Uuid)}/windows-telemetry")
            .ConfigureAwait(false))
        {
            Check(windowsResponse.StatusCode == HttpStatusCode.OK,
                "telemetria Windows com identidade confirmada deve retornar 200");
            using JsonDocument windows = JsonDocument.Parse(
                await windowsResponse.Content.ReadAsStringAsync().ConfigureAwait(false));
            JsonElement root = windows.RootElement;
            Check(root.GetProperty("adapter").GetProperty("luid").GetString() == "0x000000000001669b",
                "endpoint deve expor o LUID correlacionado");
            Check(root.GetProperty("local_memory").GetProperty("value").GetDouble() == 658640896,
                "endpoint deve preservar memória local sem somá-la à não local");
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

        TelemetryEvent liveEvent = FakeTelemetryEvent(1) with
        {
            WindowsTelemetry = FakeWindowsTelemetrySnapshot(),
        };
        hub.Publish(42, "run-http", liveEvent.TargetGpuUuid, liveEvent);
        string? idLine = await reader.ReadLineAsync(streamCancellation.Token).ConfigureAwait(false);
        string? eventLine = await reader.ReadLineAsync(streamCancellation.Token).ConfigureAwait(false);
        string? dataLine = await reader.ReadLineAsync(streamCancellation.Token).ConfigureAwait(false);
        Check(idLine == "id: 42", "SSE deve usar event_id persistido como cursor");
        Check(eventLine == "event: telemetry", "SSE deve nomear o evento de telemetria");
        Check(dataLine?.StartsWith("data: {", StringComparison.Ordinal) == true, "SSE deve emitir JSON");
        using (JsonDocument liveJson = JsonDocument.Parse(dataLine!["data: ".Length..]))
        {
            Check(liveJson.RootElement.GetProperty("event").GetProperty("windows_telemetry")
                    .GetProperty("adapter").GetProperty("luid").GetString() ==
                "0x000000000001669b",
                "SSE deve preservar o snapshot Windows do evento persistido");
        }

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
            new FakeWindowsTelemetrySource(),
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
        StoredTelemetryEvidence sampleRecord = records.First(
            record => record.EventKind == TelemetryEventKind.Sample);
        using JsonDocument persisted = JsonDocument.Parse(sampleRecord.EventJson);
        Check(persisted.RootElement.GetProperty("windows_telemetry")
                .GetProperty("adapter").GetProperty("luid").GetString() ==
            "0x000000000001669b",
            "SQLite deve persistir o mesmo snapshot Windows no evento sample");
        using JsonDocument historical = JsonDocument.Parse(EvidenceJson.Serialize(sampleRecord));
        Check(historical.RootElement.GetProperty("event").GetProperty("windows_telemetry")
                .GetProperty("local_memory").GetProperty("value").GetDouble() == 658640896,
            "evidência usada por /history deve preservar a telemetria Windows");
    }

    private static async Task TestWindowsTelemetryRecoveryAsync()
    {
        using var temporary = new TemporaryWorkspace();
        var monitoring = new MonitoringState(TestOptions(temporary.DatabasePath));
        monitoring.RecordDiscoverySuccess([FakeDiscoveredGpu()]);
        var windowsState = new WindowsTelemetryState();
        var reader = new RecoveringWindowsReader();
        var worker = new WindowsTelemetryWorker(
            monitoring,
            windowsState,
            reader,
            NullLogger<WindowsTelemetryWorker>.Instance,
            TimeSpan.FromMilliseconds(10));
        await worker.StartAsync(CancellationToken.None).ConfigureAwait(false);
        await WaitUntilAsync(
            () => reader.Reads >= 2 &&
                windowsState.GetSnapshot(FakeGpu().Uuid)?.State == "available",
            TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        await worker.StopAsync(CancellationToken.None).ConfigureAwait(false);
        Check(reader.Reads >= 2,
            "worker deve tentar novamente depois de snapshot indisponível");
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
            new FakeWindowsTelemetrySource(),
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
        var field = new PublicTelemetryValue(
            PublicTelemetryField.GpuDieTemperatureC,
            "gpu_die_temperature_c",
            PublicTelemetryProvider.NvmlTemperatureV1,
            "NVML fake",
            CapabilityState.Available,
            "available",
            DataOrigin.DriverReported,
            "driver_reported",
            TelemetryValueType.SignedInteger,
            "signed_integer",
            TelemetryUnit.Celsius,
            "celsius",
            0,
            0,
            null,
            47,
            null,
            timestamp);
        var publicTelemetry = new PublicTelemetryReport(gpu.Index, now, timestamp, [field]);
        var metric = new ComputedMetric(
            ComputedMetricKind.GpuTemperatureWindowAverage,
            "gpu_temperature_window_average",
            ComputedMetricState.Available,
            "available",
            DataOrigin.Computed,
            "computed",
            TelemetryUnit.Celsius,
            "celsius",
            "mean(gpu_die_temperature_c within window)",
            47,
            timestamp,
            5000,
            1,
            null,
            [PublicTelemetryField.GpuDieTemperatureC],
            ["gpu_die_temperature_c"]);
        var computed = new ComputedMetricsReport(gpu.Index, timestamp, [metric]);
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
            0,
            PublicTelemetry: publicTelemetry,
            ComputedMetrics: computed);
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

    private sealed class FakeWindowsTelemetrySource : IWindowsTelemetrySnapshotSource
    {
        public WindowsTelemetrySnapshot? GetSnapshot(string gpuUuid)
        {
            if (!string.Equals(gpuUuid, FakeGpu().Uuid, StringComparison.OrdinalIgnoreCase)) return null;
            return FakeWindowsTelemetrySnapshot();
        }
    }

    private static WindowsTelemetrySnapshot FakeWindowsTelemetrySnapshot()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new WindowsTelemetrySnapshot(
            1, now, "available", null, FakeGpu(),
            new WindowsAdapterIdentity(0x1669b, "Fake RTX 3060", 0x10de, 0x2504, 0x10de, 0x1536),
            new WindowsTelemetryMetric("available", 658640896, "bytes"),
            new WindowsTelemetryMetric("available", 120369152, "bytes"),
            WindowsGpuReader.EngineTypes.Select(type => new WindowsEngineTelemetry(
                type,
                new WindowsTelemetryMetric(
                    type == "3D" ? "available" : "inactive",
                    type == "3D" ? 12.5 : 0,
                    "percent"))).ToArray());
    }

    private sealed class FakeAdapterSource(IReadOnlyList<WindowsAdapterIdentity> adapters)
        : IWindowsAdapterSource
    {
        public IReadOnlyList<WindowsAdapterIdentity> Enumerate() => adapters;
    }

    private sealed class ThrowingAdapterSource : IWindowsAdapterSource
    {
        public IReadOnlyList<WindowsAdapterIdentity> Enumerate() =>
            throw new InvalidOperationException("DXGI indisponível");
    }

    private sealed class FakePdhSource(PdhGpuSample sample) : IPdhGpuCounterSource
    {
        public PdhGpuSample Read(long luid, CancellationToken cancellationToken) => sample;
    }

    private sealed class ThrowingPdhSource(string message) : IPdhGpuCounterSource
    {
        public PdhGpuSample Read(long luid, CancellationToken cancellationToken) =>
            throw new PdhException(message);
    }

    private sealed class RecoveringWindowsReader : IWindowsGpuReader
    {
        private int reads;
        internal int Reads => Volatile.Read(ref reads);

        public WindowsTelemetrySnapshot Read(DiscoveredGpu gpu, CancellationToken cancellationToken)
        {
            int attempt = Interlocked.Increment(ref reads);
            string state = attempt == 1 ? "counters_unavailable" : "available";
            string? error = attempt == 1 ? "falha transitória" : null;
            double? value = attempt == 1 ? null : 1;
            var bytes = new WindowsTelemetryMetric(state, value, "bytes", error);
            return new WindowsTelemetrySnapshot(
                1, DateTimeOffset.UtcNow, state, error, gpu.Gpu, null, bytes, bytes,
                WindowsGpuReader.EngineTypes.Select(type => new WindowsEngineTelemetry(
                    type, new WindowsTelemetryMetric(state, value, "percent", error))).ToArray());
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
