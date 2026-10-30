using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging.Abstractions;
using RtxMonitor.Managed;
using RtxMonitor.Service;
using RtxMonitor.Storage;

namespace RtxMonitor.Service.Tests;

internal static partial class Program
{
    private static async Task TestExperimentalFirstIdentityDriftAsync()
    {
        using var temporary = new TemporaryWorkspace();
        var options = TestOptions(temporary.DatabasePath) with { ExperimentalTelemetryEnabled = true };
        var gpu = new GpuInfo(0, "Fixture RTX 3060", ExperimentalTelemetryReport.SupportedGpuUuid, "610.88", "13.610.88");
        BoardIdentity board = FakeDiscoveredGpu().Evidence.Board! with { VbiosVersion = "94.06.25.00.fc" };
        var evidence = new GpuEvidenceSnapshot(gpu, board, BoardEvidenceState.Available, null, DateTimeOffset.UtcNow);
        DiscoveredGpu discovered = FakeDiscoveredGpu() with { Gpu = gpu, Evidence = evidence };
        var source = new EndpointExperimentalSource(ExperimentalEndpointFixture(gpu, board, DateTimeOffset.UtcNow));
        var state = new MonitoringState(options);
        var storeProvider = new TelemetryStoreProvider();
        var hub = new TelemetryEventHub(options);
        using TelemetryEventHub.TelemetrySubscription subscription = hub.Subscribe();
        var worker = new GpuMonitoringWorker(options, state, storeProvider, hub,
            new FirstDriftBackend(discovered), new FakeWindowsTelemetrySource(),
            NullLogger<GpuMonitoringWorker>.Instance, source);
        await worker.StartAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await WaitUntilAsync(() => state.GetSnapshot().Gpus.FirstOrDefault()?.LastEventAt is not null,
                TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        finally { await worker.StopAsync(CancellationToken.None).ConfigureAwait(false); }
        var store = SqliteTelemetryStore.Open(new TelemetryStoreOptions(temporary.DatabasePath,
            openMode: TelemetryStoreOpenMode.OpenExisting));
        StoredTelemetryEvidence first = store.QueryEvents(new TelemetryEventQuery(Limit: 1, Ascending: true))[0];
        using JsonDocument persisted = JsonDocument.Parse(first.EventJson);
        JsonElement report = persisted.RootElement.GetProperty("profile_telemetry");
        Check(report.GetProperty("identity").GetProperty("driver_version").GetString() == "changed-driver",
            "o primeiro evento após mudança deve registrar a identidade recém-observada");
        Check(report.GetProperty("fields").EnumerateArray().All(field => field.GetProperty("state").GetString() == "identity_mismatch" &&
                field.GetProperty("value").ValueKind == JsonValueKind.Null && field.GetProperty("raw_value").ValueKind == JsonValueKind.Null),
            "o primeiro evento após mudança não pode transportar leitura válida da identidade anterior");
        using JsonDocument live = JsonDocument.Parse(subscription.TakeBatch().Records[0].Json);
        Check(CanonicalJson(live.RootElement.GetProperty("event").GetProperty("profile_telemetry")) == CanonicalJson(report),
            "SSE deve recusar imediatamente a mesma leitura obsoleta do histórico");
    }

    private static async Task TestExperimentalEndpointsAsync()
    {
        using var temporary = new TemporaryWorkspace();
        RtxMonitorServiceOptions options = TestOptions(temporary.DatabasePath) with { ExperimentalTelemetryEnabled = true };
        DateTimeOffset now = DateTimeOffset.UtcNow;
        GpuInfo gpu = new(0, "Fixture RTX 3060", ExperimentalTelemetryReport.SupportedGpuUuid, "610.88", "13.610.88");
        BoardIdentity board = FakeDiscoveredGpu().Evidence.Board! with { VbiosVersion = "94.06.25.00.fc" };
        var evidence = new GpuEvidenceSnapshot(gpu, board, BoardEvidenceState.Available, null, now);
        DiscoveredGpu discovered = FakeDiscoveredGpu() with { Gpu = gpu, Evidence = evidence };
        var monitoring = new MonitoringState(options);
        monitoring.MarkStorageAvailable(1);
        monitoring.RecordDiscoverySuccess([discovered]);
        var reports = new EndpointExperimentalSource(ExperimentalEndpointFixture(gpu, board, now));
        Check(reports.Report.Fields.All(field => field.State == ExperimentalTelemetryState.Available),
            "fixture experimental deve conter duas leituras disponíveis");
        var store = SqliteTelemetryStore.Open(new TelemetryStoreOptions(temporary.DatabasePath));
        string runId = store.StartRun(new MonitoringRunOptions(gpu.Uuid, 1000, 32, null, 0, "0.11.0", now));
        long snapshotId = store.RegisterGpuSnapshot(runId, evidence);
        var hub = new TelemetryEventHub(options);

        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0,
            listen => listen.Protocols = HttpProtocols.Http1));
        builder.Services.ConfigureHttpJsonOptions(json => json.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<IMonitoringSnapshotSource>(monitoring);
        builder.Services.AddSingleton<IHistorySource>(new EndpointHistorySource(store));
        builder.Services.AddSingleton<IWindowsTelemetrySnapshotSource>(new FakeWindowsTelemetrySource());
        builder.Services.AddSingleton<IExperimentalTelemetrySnapshotSource>(reports);
        builder.Services.AddSingleton<IExperimentalTelemetryPublicationSource>(reports);
        builder.Services.AddSingleton(hub);
        await using WebApplication app = builder.Build();
        ServiceEndpoints.Map(app);
        await app.StartAsync().ConfigureAwait(false);
        using var client = new HttpClient { BaseAddress = GetServerAddress(app), Timeout = TimeSpan.FromSeconds(10) };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using HttpResponseMessage streamResponse = await client.GetAsync("/api/v1/events",
            HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        await using Stream stream = await streamResponse.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        _ = await reader.ReadLineAsync(deadline.Token).ConfigureAwait(false);
        _ = await reader.ReadLineAsync(deadline.Token).ConfigureAwait(false);

        for (int index = 0; index < 2; index++)
        {
            if (index == 1) reports.Report = reports.Report.WithFailure(ExperimentalTelemetryState.Timeout, now.AddSeconds(1));
            JsonElement expected = ExperimentalTelemetryJson.ToElement(reports.Report);
            string route = $"/api/v1/gpus/{Uri.EscapeDataString(gpu.Uuid)}/profile-telemetry";
            using HttpResponseMessage response = await client.GetAsync(route, deadline.Token).ConfigureAwait(false);
            Check(response.StatusCode == HttpStatusCode.OK && response.Headers.CacheControl?.NoStore == true,
                "endpoint experimental deve retornar relatório sem cache inclusive em falha");
            using JsonDocument api = JsonDocument.Parse(await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false));
            Check(CanonicalJson(api.RootElement) == CanonicalJson(expected),
                "API deve preservar o relatório experimental canônico");
            TelemetryEvent sample = FakeTelemetryEvent((ulong)index + 1) with
            {
                TargetGpuUuid = gpu.Uuid,
                Gpu = gpu,
                ExperimentalTelemetry = reports.Report,
            };
            long eventId = store.AppendEvent(runId, sample, snapshotId);
            hub.Publish(eventId, runId, gpu.Uuid, sample);
            string? line;
            do { line = await reader.ReadLineAsync(deadline.Token).ConfigureAwait(false); }
            while (line is not null && !line.StartsWith("data: ", StringComparison.Ordinal));
            Check(line is not null, "SSE deve emitir o evento experimental persistido");
            using JsonDocument live = JsonDocument.Parse(line![6..]);
            JsonElement liveEvent = live.RootElement.GetProperty("event");
            Check(liveEvent.GetProperty("schema_version").GetInt32() == 8,
                "evento com canal experimental deve declarar v6");
            Check(CanonicalJson(liveEvent.GetProperty("profile_telemetry")) == CanonicalJson(api.RootElement),
                "SSE deve preservar valores, estado, horário e proveniência da API");

            using JsonDocument history = JsonDocument.Parse(await client.GetStringAsync(
                $"/api/v1/history?run_id={runId}&limit=1", deadline.Token).ConfigureAwait(false));
            JsonElement stored = history.RootElement.GetProperty("items")[0].GetProperty("event").GetProperty("profile_telemetry");
            Check(CanonicalJson(stored) == CanonicalJson(api.RootElement),
                "histórico deve preservar o mesmo relatório experimental");
            StoredTelemetryEvidence persisted = store.QueryEvents(new TelemetryEventQuery(RunId: runId, Limit: 1))[0];
            using JsonDocument exported = JsonDocument.Parse(EvidenceJson.Serialize(persisted));
            Check(CanonicalJson(exported.RootElement.GetProperty("event").GetProperty("profile_telemetry")) == CanonicalJson(api.RootElement),
                "exportação deve preservar o mesmo relatório experimental");
            if (index == 1)
                Check(stored.GetProperty("fields").EnumerateArray().All(field => field.GetProperty("value").ValueKind == JsonValueKind.Null &&
                    field.GetProperty("raw_value").ValueKind == JsonValueKind.Null && field.GetProperty("state").GetString() == "timeout"),
                    "falha deve propagar timeout sem reapresentar valor ou bytes anteriores");
        }
        using HttpResponseMessage unknown = await client.GetAsync("/api/v1/gpus/GPU-unknown/profile-telemetry").ConfigureAwait(false);
        Check(unknown.StatusCode == HttpStatusCode.NotFound, "endpoint experimental deve recusar GPU desconhecida");
        streamResponse.Dispose();
        await app.StopAsync().ConfigureAwait(false);
    }

    private static string CanonicalJson(JsonElement element) => JsonSerializer.Serialize(element);

    private static ExperimentalTelemetryReport ExperimentalEndpointFixture(GpuInfo gpu, BoardIdentity board, DateTimeOffset now)
    {
        var profile = new PrivateProfileStatus(0, ExperimentalTelemetryReport.SupportedProfileId, 2,
            PrivateProfileState.Active, null,
            new[] { "pci_vendor_id", "pci_device_id", "pci_subsystem_vendor_id", "pci_subsystem_device_id", "gpu_uuid", "vbios_version", "driver_version" }
                .Select(field => new PrivateProfileIdentityCheck(field, "matched")).ToArray(),
            PrivateOperationState.Compatible, PrivateOperationState.Compatible, 100, 2000, 100, 2000);
        JsonElement Payload(bool thermal) => JsonSerializer.SerializeToElement(new
        {
            schema_version = thermal ? 2 : 1,
            source_kind = thermal ? PrivateThermalSample.SourceKind : PrivateVoltageSample.SourceKind,
            gpu_index = gpu.Index,
            gpu_uuid = gpu.Uuid,
            captured_at_utc = now.ToString("O"),
            captured_at_unix_ms = now.ToUnixTimeMilliseconds(),
            monotonic_ns = 1000,
            monotonic_frequency_hz = 10000000,
            gpu_die_temperature_c = 37.0625,
            gpu_hotspot_temperature_c = 47.53125,
            delta_c = 10.46875,
            gpu_die_temperature_fixed8 = 9488,
            gpu_hotspot_temperature_fixed8 = 12168,
            gpu_die_temperature_millic = 37063,
            gpu_hotspot_temperature_millic = 47531,
            acquisition_started_at_utc = now.ToString("O"),
            acquisition_started_unix_ms = now.ToUnixTimeMilliseconds(),
            acquisition_started_monotonic_ms = 1000,
            acquisition_finished_monotonic_ms = 1001,
            gpu_core_voltage_microvolts = 956250,
            gpu_core_voltage_v = 0.95625,
            native_status = 0,
            profile_evidence_stage = "matched_external_reference",
            profile_name = PrivateThermalSample.Profile,
            interface_id = thermal ? PrivateThermalSample.InterfaceId : PrivateVoltageSample.InterfaceId,
            structure_version = thermal ? PrivateThermalSample.StructureVersion : PrivateVoltageSample.StructureVersion,
            nvapi_module_sha256 = PrivateThermalSample.NvapiModuleSha256,
            function_rva = thermal ? PrivateThermalSample.FunctionRva : PrivateVoltageSample.FunctionRva,
        });
        return ExperimentalTelemetryReport.Create(gpu, board, profile, Payload(true), Payload(false), evaluatedAt: now);
    }

    private sealed class EndpointExperimentalSource(ExperimentalTelemetryReport report) : IExperimentalTelemetrySnapshotSource, IExperimentalTelemetryPublicationSource
    {
        internal ExperimentalTelemetryReport Report { get; set; } = report;
        public ExperimentalTelemetryReport? GetSnapshot(string gpuUuid) => gpuUuid == Report.GpuUuid ? Report : null;
        public ExperimentalTelemetryReport? GetPublishedSnapshot(string gpuUuid) => GetSnapshot(gpuUuid);
        public void RecordCommitted(ExperimentalTelemetryReport report) => Report = report;
    }

    private sealed class EndpointHistorySource(SqliteTelemetryStore store) : IHistorySource
    {
        public IReadOnlyList<StoredTelemetryEvidence> Query(TelemetryEventQuery query) => store.QueryEvents(query);
    }

    private sealed class FirstDriftBackend(DiscoveredGpu discovered) : IMonitoringBackend
    {
        public IReadOnlyList<DiscoveredGpu> Discover() => [discovered];
        public GpuEvidenceSnapshot CaptureEvidence(GpuInfo gpu) => discovered.Evidence with { Gpu = gpu };
        public ITelemetrySampler CreateSampler(string gpuUuid, SamplingOptions options) => new FirstDriftSampler(discovered.Gpu);
    }

    private sealed class FirstDriftSampler(GpuInfo gpu) : ITelemetrySampler
    {
        private ulong sequence;
        public IReadOnlyList<TelemetryEvent> Poll() => [FakeTelemetryEvent(++sequence) with
        {
            TargetGpuUuid = gpu.Uuid,
            Gpu = gpu with { DriverVersion = "changed-driver" },
        }];
        public uint NextDelayMilliseconds(uint successfulSampleIntervalMilliseconds) => successfulSampleIntervalMilliseconds;
        public void Dispose() { }
    }
}
