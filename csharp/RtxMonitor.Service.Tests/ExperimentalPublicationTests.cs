using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using RtxMonitor.ConsoleApp;
using RtxMonitor.Managed;
using RtxMonitor.Service;
using RtxMonitor.Storage;

namespace RtxMonitor.Service.Tests;

internal static partial class Program
{
    private static async Task TestExperimentalCommittedPublicationAsync()
    {
        using var temporary = new TemporaryWorkspace();
        var options = TestOptions(temporary.DatabasePath) with { ExperimentalTelemetryEnabled = true, IntervalMilliseconds = 1000 };
        var monitoring = new MonitoringState(options);
        var clock = new PublicationClock();
        DiscoveredGpu discovered = PublicationGpu(clock);
        monitoring.RecordDiscoverySuccess([discovered]);
        var acquired = new ExperimentalTelemetrySnapshotState(true, 1000, monitoring, clock);
        ExperimentalTelemetryReport first = ExperimentalEndpointFixture(discovered.Gpu, discovered.Evidence.Board!, clock.GetUtcNow());
        acquired.Record(first);
        Check(acquired.GetPublishedSnapshot(first.GpuUuid)!.Fields.All(field => field.State == ExperimentalTelemetryState.PublicationPending && field.Value is null),
            "an acquired report is not numeric HTTP data before its first commit");
        var provider = new TelemetryStoreProvider();
        var publication = new PublicationSpy(acquired, provider);
        var hub = new TelemetryEventHub(options);
        using TelemetryEventHub.TelemetrySubscription subscription = hub.Subscribe();
        var backend = new PublicationBackend(discovered);
        var worker = new GpuMonitoringWorker(options, monitoring, provider, hub, backend,
            new FakeWindowsTelemetrySource(), NullLogger<GpuMonitoringWorker>.Instance, publication);

        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0,
            listen => listen.Protocols = HttpProtocols.Http1));
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<IMonitoringSnapshotSource>(monitoring);
        builder.Services.AddSingleton<IHistorySource>(provider);
        builder.Services.AddSingleton<IWindowsTelemetrySnapshotSource>(new FakeWindowsTelemetrySource());
        builder.Services.AddSingleton<IExperimentalTelemetryPublicationSource>(publication);
        builder.Services.AddSingleton(hub);
        await using WebApplication app = builder.Build();
        ServiceEndpoints.Map(app);
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = GetServerAddress(app), Timeout = TimeSpan.FromSeconds(5) };
        string route = $"/api/v1/gpus/{first.GpuUuid}/profile-telemetry";
        async Task<string> HttpReportAsync()
        {
            using JsonDocument json = JsonDocument.Parse(await client.GetStringAsync(route));
            return CanonicalJson(json.RootElement);
        }
        await worker.StartAsync(default);
        try
        {
            await WaitUntilAsync(() => publication.Commits == 1, TimeSpan.FromSeconds(5));
            string firstJson = ExperimentalTelemetryJson.Serialize(first);
            Check(await HttpReportAsync() == firstJson, "HTTP publishes the exact first SQLite-confirmed report");
            var skipped = new List<string>();
            for (int i = 0; i < 3; i++)
            {
                clock.Advance(100);
                ExperimentalTelemetryReport fast = ExperimentalEndpointFixture(discovered.Gpu, discovered.Evidence.Board!, clock.GetUtcNow());
                acquired.Record(fast);
                if (i != 2) skipped.Add(ExperimentalTelemetryJson.Serialize(fast));
                Check(ReferenceEquals(acquired.GetSnapshot(first.GpuUuid), fast), "sampler sees the newer acquisition without publication feedback");
                Check(await HttpReportAsync() == firstJson, "faster private acquisitions cannot leak into HTTP before public sampling commits them");
            }
            ExperimentalTelemetryReport next = acquired.GetSnapshot(first.GpuUuid)!;
            backend.RequestSample();
            await WaitUntilAsync(() => publication.Commits == 2, TimeSpan.FromSeconds(5));
            Check(await HttpReportAsync() == ExperimentalTelemetryJson.Serialize(next), "HTTP advances to the next committed acquisition");
            IReadOnlyList<StoredTelemetryEvidence> history = provider.Query(new TelemetryEventQuery(Limit: 10));
            var recorded = history.Select(item =>
            {
                using JsonDocument document = JsonDocument.Parse(item.EventJson);
                return document.RootElement.GetProperty("profile_telemetry").GetRawText();
            }).ToArray();
            Check(recorded.Length == 2 && skipped.All(report => !recorded.Contains(report)),
                "unused acquisitions need no queue or new event kind while every numeric HTTP result is stored");
            foreach (var live in subscription.TakeBatch().Records)
            {
                using JsonDocument document = JsonDocument.Parse(live.Json);
                JsonElement liveEvent = document.RootElement.GetProperty("event");
                Check(history.Any(item => item.EventJson == liveEvent.GetRawText()), "SSE remains identical to persisted events");
            }

            // The real append path fails before the publication callback; no simulated commit shortcut.
            using (var connection = new SqliteConnection($"Data Source={temporary.DatabasePath}"))
            {
                connection.Open();
                using SqliteCommand command = connection.CreateCommand();
                command.CommandText = "CREATE TRIGGER publication_fixture_abort BEFORE INSERT ON telemetry_events BEGIN SELECT RAISE(ABORT, 'publication fixture append failure'); END;";
                command.ExecuteNonQuery();
            }
            clock.Advance(100);
            ExperimentalTelemetryReport uncommitted = ExperimentalEndpointFixture(discovered.Gpu, discovered.Evidence.Board!, clock.GetUtcNow());
            acquired.Record(uncommitted);
            backend.RequestSample();
            await WaitUntilAsync(() => backend.Emitted >= 3, TimeSpan.FromSeconds(5));
            await WaitUntilAsync(() => monitoring.GetSnapshot().Storage.State == "unavailable", TimeSpan.FromSeconds(5));
            Check(publication.Commits == 2 && publication.InvalidCommits == 0,
                "failed SQLite insertion cannot advance HTTP publication");
            using JsonDocument afterFailure = JsonDocument.Parse(await HttpReportAsync());
            Check(afterFailure.RootElement.GetProperty("fields").EnumerateArray().All(field => field.GetProperty("value").ValueKind == JsonValueKind.Null) ||
                afterFailure.RootElement.GetRawText() == ExperimentalTelemetryJson.Serialize(next),
                "storage failure may suppress numbers but cannot serve the uncommitted acquisition");
        }
        finally
        {
            await worker.StopAsync(default);
            worker.Dispose();
            await app.StopAsync();
        }
    }

    private static void TestExperimentalPublicationInvalidation()
    {
        using var temporary = new TemporaryWorkspace();
        var options = TestOptions(temporary.DatabasePath) with { ExperimentalTelemetryEnabled = true };
        var clock = new PublicationClock();
        var monitoring = new MonitoringState(options);
        DiscoveredGpu discovered = PublicationGpu(clock);
        monitoring.RecordDiscoverySuccess([discovered]);
        ExperimentalTelemetrySnapshotState NewState() => new(true, 1000, monitoring, clock);
        ExperimentalTelemetryReport Report() => ExperimentalEndpointFixture(discovered.Gpu, discovered.Evidence.Board!, clock.GetUtcNow());
        var acquired = NewState();
        ExperimentalTelemetryReport old = Report();
        acquired.Record(old);
        clock.Advance(9000);
        acquired.RecordCommitted(old);
        Check(ReferenceEquals(acquired.GetPublishedSnapshot(old.GpuUuid), old), "a nine-second acquisition retains its original capture time at commit");
        clock.Advance(2000);
        acquired.Record(Report());
        acquired.RecordCommitted(old);
        Check(acquired.GetPublishedSnapshot(old.GpuUuid)!.Fields.All(field => field.State == ExperimentalTelemetryState.Timeout && field.Value is null),
            "commit delay and repeated commits cannot refresh the original sensor age");
        Check(acquired.GetSnapshot(old.GpuUuid)!.Fields.All(field => field.State == ExperimentalTelemetryState.Available),
            "publication expiry does not invalidate a fresh private acquisition session");
        ExperimentalTelemetryReport fresh = acquired.GetSnapshot(old.GpuUuid)!;
        acquired.RecordCommitted(fresh);
        Check(ReferenceEquals(acquired.GetPublishedSnapshot(old.GpuUuid), fresh), "a newer committed acquisition recovers publication");

        var restarted = NewState();
        restarted.Record(fresh);
        Check(restarted.GetPublishedSnapshot(fresh.GpuUuid)!.Fields.All(field => field.State == ExperimentalTelemetryState.PublicationPending),
            "restart begins without a confirmed publication cache");

        acquired.Record(fresh.WithFailure(ExperimentalTelemetryState.Revoked, clock.GetUtcNow()));
        Check(acquired.GetPublishedSnapshot(fresh.GpuUuid)!.Fields.All(field => field.State == ExperimentalTelemetryState.Revoked && field.Value is null),
            "revocation suppresses committed numbers before the failure is persisted");

        acquired = NewState();
        acquired.Record(fresh);
        acquired.RecordCommitted(fresh);
        DateTimeOffset now = clock.GetUtcNow();
        var profile = new PrivateProfileStatus(0, ExperimentalTelemetryReport.SupportedProfileId, 2, PrivateProfileState.Active, null,
            new[] { "pci_vendor_id", "pci_device_id", "pci_subsystem_vendor_id", "pci_subsystem_device_id", "gpu_uuid", "vbios_version", "driver_version" }
                .Select(field => new PrivateProfileIdentityCheck(field, "matched")).ToArray(),
            PrivateOperationState.Compatible, PrivateOperationState.Compatible, 100, 2000, 100, 2000);
        JsonElement voltage = PrivateSampleJson.Voltage(discovered.Gpu, new PrivateVoltageSample(0, 800000, 0, now, checked((ulong)now.ToUnixTimeMilliseconds())));
        ExperimentalTelemetryReport partial = ExperimentalTelemetryReport.Create(discovered.Gpu, discovered.Evidence.Board, profile, null, voltage,
            thermalFailure: ExperimentalTelemetryState.Timeout, evaluatedAt: now);
        acquired.Record(partial);
        Check(acquired.GetPublishedSnapshot(fresh.GpuUuid)!.Fields.All(field => field.State == ExperimentalTelemetryState.PublicationPending && field.Value is null),
            "a newly failed field cannot retain old numbers or expose the sibling's uncommitted value");
        acquired.RecordCommitted(partial);
        Check(ReferenceEquals(acquired.GetPublishedSnapshot(fresh.GpuUuid), partial),
            "persisted partial failure preserves its exact states and remaining available field");

        discovered = discovered with { Gpu = discovered.Gpu with { DriverVersion = "changed-driver" } };
        monitoring.RecordDiscoverySuccess([discovered]);
        Check(acquired.GetPublishedSnapshot(fresh.GpuUuid)!.Fields.All(field => field.State == ExperimentalTelemetryState.IdentityMismatch && field.Value is null),
            "current identity drift immediately hides the previously committed report");
    }

    private static DiscoveredGpu PublicationGpu(TimeProvider clock)
    {
        GpuInfo gpu = new(0, "Fixture RTX 3060", ExperimentalTelemetryReport.SupportedGpuUuid, "610.88", "13.610.88");
        BoardIdentity board = FakeDiscoveredGpu().Evidence.Board! with { VbiosVersion = "94.06.25.00.fc" };
        var evidence = new GpuEvidenceSnapshot(gpu, board, BoardEvidenceState.Available, null, clock.GetUtcNow());
        return new(gpu, evidence, null, null, clock.GetUtcNow());
    }

    private sealed class PublicationClock : TimeProvider
    {
        private readonly DateTimeOffset initial = DateTimeOffset.UtcNow;
        private long milliseconds;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Interlocked.Read(ref milliseconds);
        public override DateTimeOffset GetUtcNow() => initial.AddMilliseconds(GetTimestamp());
        internal void Advance(long amount) => Interlocked.Add(ref milliseconds, amount);
    }

    private sealed class PublicationSpy(ExperimentalTelemetrySnapshotState state, TelemetryStoreProvider store)
        : IExperimentalTelemetrySnapshotSource, IExperimentalTelemetryPublicationSource
    {
        private int commits;
        private int invalidCommits;
        internal int Commits => Volatile.Read(ref commits);
        internal int InvalidCommits => Volatile.Read(ref invalidCommits);
        public ExperimentalTelemetryReport? GetSnapshot(string gpuUuid) => state.GetSnapshot(gpuUuid);
        public ExperimentalTelemetryReport? GetPublishedSnapshot(string gpuUuid) => state.GetPublishedSnapshot(gpuUuid);
        public void RecordCommitted(ExperimentalTelemetryReport report)
        {
            string expected = ExperimentalTelemetryJson.Serialize(report);
            bool persisted = store.Query(new TelemetryEventQuery(Limit: 10)).Any(item =>
            {
                using JsonDocument document = JsonDocument.Parse(item.EventJson);
                return document.RootElement.GetProperty("profile_telemetry").GetRawText() == expected;
            });
            if (!persisted) { Interlocked.Increment(ref invalidCommits); throw new InvalidOperationException("Publication preceded SQLite commit."); }
            state.RecordCommitted(report);
            Interlocked.Increment(ref commits);
        }
    }

    private sealed class PublicationBackend(DiscoveredGpu discovered) : IMonitoringBackend, ITelemetrySampler
    {
        private int pending = 1;
        private int emitted;
        internal int Emitted => Volatile.Read(ref emitted);
        internal void RequestSample() => Volatile.Write(ref pending, 1);
        public IReadOnlyList<DiscoveredGpu> Discover() => [discovered];
        public GpuEvidenceSnapshot CaptureEvidence(GpuInfo gpu) => discovered.Evidence;
        public ITelemetrySampler CreateSampler(string gpuUuid, SamplingOptions options) => this;
        public IReadOnlyList<TelemetryEvent> Poll() => Interlocked.Exchange(ref pending, 0) == 0 ? [] :
            [FakeTelemetryEvent(checked((ulong)Interlocked.Increment(ref emitted))) with { TargetGpuUuid = discovered.Gpu.Uuid, Gpu = discovered.Gpu }];
        public uint NextDelayMilliseconds(uint successfulSampleIntervalMilliseconds) => successfulSampleIntervalMilliseconds;
        public void Dispose() { }
    }
}
