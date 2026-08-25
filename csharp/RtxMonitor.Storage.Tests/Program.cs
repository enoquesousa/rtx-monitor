using System.Text.Json;
using Microsoft.Data.Sqlite;
using RtxMonitor.Managed;

namespace RtxMonitor.Storage.Tests;

internal static class Program
{
    private static int failures;

    private static async Task<int> Main()
    {
        Run("migration, restart, and evidence export", TestMigrationRestartAndEvidence);
        Run("sequence conflict", TestSequenceConflict);
        Run("query filters and retention", TestQueryFiltersAndRetention);
        await RunAsync("concurrent writers", TestConcurrentWritersAsync).ConfigureAwait(false);
        Run("invalid and future databases", TestInvalidAndFutureDatabases);
        Run("open-existing does not create a database", TestOpenExistingDoesNotCreate);

        if (failures == 0)
        {
            Console.WriteLine("RtxMonitor.Storage tests passed");
        }

        return failures == 0 ? 0 : 1;
    }

    private static void TestMigrationRestartAndEvidence()
    {
        using var temporary = new TemporaryDatabase();
        DateTimeOffset observedAt = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000);
        GpuInfo gpu = FakeGpu();
        GpuEvidenceSnapshot snapshot = FakeSnapshot(gpu, observedAt);

        SqliteTelemetryStore first = CreateStore(temporary.DatabasePath);
        Check(first.GetSchemaVersion() == 1, "migration inicial deve criar schema 1");
        first.VerifyIntegrity();

        string runId = first.StartRun(RunOptions(gpu.Uuid, observedAt));
        long snapshotId = first.RegisterGpuSnapshot(runId, snapshot);
        TelemetryEvent sample = SampleEvent(1, gpu, 47, observedAt);
        long firstEventId = first.AppendEvent(runId, sample, snapshotId);
        long retriedEventId = first.AppendEvent(runId, sample, snapshotId);
        Check(firstEventId == retriedEventId, "retry idempotente deve retornar o mesmo event_id");
        first.CompleteRun(runId, "completed", observedAt.AddSeconds(1));

        SqliteConnection.ClearAllPools();
        SqliteTelemetryStore reopened = SqliteTelemetryStore.Open(
            new TelemetryStoreOptions(
                temporary.DatabasePath,
                openMode: TelemetryStoreOpenMode.OpenExisting));
        IReadOnlyList<StoredTelemetryEvidence> history = reopened.QueryEvents(
            new TelemetryEventQuery(RunId: runId, Limit: 10, Ascending: true));

        Check(history.Count == 1, "reabertura deve preservar o evento confirmado");
        StoredTelemetryEvidence evidence = history[0];
        Check(evidence.EventId == firstEventId, "event_id deve sobreviver ao reinício");
        Check(evidence.StreamSequence == 1, "sequência deve sobreviver ao reinício");
        Check(evidence.DeviceSnapshot?.Gpu.Uuid == gpu.Uuid, "UUID deve permanecer no snapshot");
        Check(
            evidence.DeviceSnapshot?.ProfileKey == "10de:2504/1b4c:1530@94.06.14.40.72",
            "profile key deve preservar placa e VBIOS");
        Check(evidence.Run.CompletionReason == "completed", "run deve registrar encerramento");

        string json = EvidenceJson.Serialize(evidence);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        Check(
            root.GetProperty("evidence_schema_version").GetInt32() == 1,
            "exportação deve declarar evidence schema 1");
        Check(
            root.GetProperty("event").GetProperty("schema_version").GetInt32() == 3,
            "exportação deve incorporar o evento v3 sem alterar seu contrato");
        Check(
            root.GetProperty("device_snapshot")
                .GetProperty("board")
                .GetProperty("profile_key")
                .GetString() == "10de:2504/1b4c:1530@94.06.14.40.72",
            "exportação deve incluir proveniência da placa");
    }

    private static void TestSequenceConflict()
    {
        using var temporary = new TemporaryDatabase();
        DateTimeOffset observedAt = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_010_000);
        GpuInfo gpu = FakeGpu();
        SqliteTelemetryStore store = CreateStore(temporary.DatabasePath);
        string runId = store.StartRun(RunOptions(gpu.Uuid, observedAt));
        long snapshotId = store.RegisterGpuSnapshot(runId, FakeSnapshot(gpu, observedAt));
        store.AppendEvent(runId, SampleEvent(1, gpu, 45, observedAt), snapshotId);

        Check(
            Throws<TelemetrySequenceConflictException>(
                () => store.AppendEvent(
                    runId,
                    SampleEvent(1, gpu, 99, observedAt),
                    snapshotId)),
            "a mesma sequência não pode representar conteúdo diferente");
        GpuInfo otherGpu = gpu with { Uuid = "GPU-OTHER" };
        Check(
            Throws<TelemetryStoreException>(
                () => store.RegisterGpuSnapshot(
                    runId,
                    new GpuEvidenceSnapshot(
                        otherGpu,
                        null,
                        BoardEvidenceState.NotAttempted,
                        null,
                        observedAt))),
            "snapshot de outra GPU não pode entrar no run");
    }

    private static void TestQueryFiltersAndRetention()
    {
        using var temporary = new TemporaryDatabase();
        DateTimeOffset now = DateTimeOffset.FromUnixTimeMilliseconds(1_750_000_000_000);
        DateTimeOffset old = now.AddDays(-60);
        GpuInfo gpu = FakeGpu();
        SqliteTelemetryStore store = CreateStore(temporary.DatabasePath, retentionDays: 30);

        string oldRun = store.StartRun(RunOptions(gpu.Uuid, old));
        long oldSnapshot = store.RegisterGpuSnapshot(oldRun, FakeSnapshot(gpu, old));
        store.AppendEvent(oldRun, SampleEvent(1, gpu, 40, old), oldSnapshot);
        store.CompleteRun(oldRun, "completed", old.AddMinutes(1));

        string currentRun = store.StartRun(RunOptions(gpu.Uuid, now));
        long currentSnapshot = store.RegisterGpuSnapshot(currentRun, FakeSnapshot(gpu, now));
        store.AppendEvent(currentRun, SampleEvent(1, gpu, 55, now), currentSnapshot);
        store.AppendEvent(currentRun, GapEvent(2, gpu.Uuid, now.AddSeconds(1)), currentSnapshot);

        IReadOnlyList<StoredTelemetryEvidence> gaps = store.QueryEvents(
            new TelemetryEventQuery(
                TargetGpuUuid: gpu.Uuid.ToLowerInvariant(),
                EventKind: TelemetryEventKind.Gap,
                FromUnixMilliseconds: now.ToUnixTimeMilliseconds(),
                Limit: 10));
        Check(gaps.Count == 1, "filtros por UUID, tipo e tempo devem ser combináveis");

        IReadOnlyList<StoredTelemetryEvidence> afterSequence = store.QueryEvents(
            new TelemetryEventQuery(
                RunId: currentRun,
                AfterSequence: 1,
                Limit: 10,
                Ascending: true));
        Check(
            afterSequence.Count == 1 && afterSequence[0].EventKind == TelemetryEventKind.Gap,
            "consulta por sequência deve ser exclusiva e restrita ao run");
        Check(
            Throws<ArgumentException>(
                () => store.QueryEvents(new TelemetryEventQuery(AfterSequence: 1))),
            "sequência sem run_id deve ser recusada");

        RetentionResult result = store.ApplyRetention(now);
        Check(result.EventsDeleted == 1, "retenção deve remover somente o evento antigo");
        Check(result.SnapshotsDeleted == 1, "retenção deve remover snapshot órfão antigo");
        Check(result.RunsDeleted == 1, "retenção deve remover run antigo já encerrado");
        Check(
            store.QueryEvents(new TelemetryEventQuery(Limit: 10)).Count == 2,
            "retenção deve preservar eventos dentro da janela");
    }

    private static async Task TestConcurrentWritersAsync()
    {
        using var temporary = new TemporaryDatabase();
        DateTimeOffset start = DateTimeOffset.FromUnixTimeMilliseconds(1_760_000_000_000);
        GpuInfo gpu = FakeGpu();
        SqliteTelemetryStore store = CreateStore(temporary.DatabasePath);
        string runId = store.StartRun(RunOptions(gpu.Uuid, start));
        long snapshotId = store.RegisterGpuSnapshot(runId, FakeSnapshot(gpu, start));

        Task<long>[] writes = Enumerable.Range(1, 48)
            .Select(sequence => Task.Run(
                () => store.AppendEvent(
                    runId,
                    SampleEvent(
                        checked((ulong)sequence),
                        gpu,
                        30 + sequence,
                        start.AddMilliseconds(sequence)),
                    snapshotId)))
            .ToArray();
        await Task.WhenAll(writes).ConfigureAwait(false);

        IReadOnlyList<StoredTelemetryEvidence> events = store.QueryEvents(
            new TelemetryEventQuery(RunId: runId, Limit: 100, Ascending: true));
        Check(events.Count == writes.Length, "writers concorrentes não podem perder eventos");
        Check(
            events.Select(item => item.StreamSequence)
                .OrderBy(value => value)
                .SequenceEqual(Enumerable.Range(1, 48).Select(value => checked((ulong)value))),
            "histórico concorrente deve preservar todas as sequências");
    }

    private static void TestInvalidAndFutureDatabases()
    {
        using (var invalid = new TemporaryDatabase())
        {
            const string original = "isto não é um banco SQLite";
            File.WriteAllText(invalid.DatabasePath, original);
            Check(
                Throws<TelemetryStoreException>(() => CreateStore(invalid.DatabasePath)),
                "arquivo inválido deve falhar de forma explícita");
            Check(
                File.ReadAllText(invalid.DatabasePath) == original,
                "arquivo inválido não pode ser sobrescrito");
        }

        using var future = new TemporaryDatabase();
        CreateStore(future.DatabasePath);
        SqliteConnection.ClearAllPools();
        using (var connection = new SqliteConnection($"Data Source={future.DatabasePath}"))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version = 2;";
            command.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
        Check(
            Throws<TelemetryStoreException>(
                () => SqliteTelemetryStore.Open(
                    new TelemetryStoreOptions(
                        future.DatabasePath,
                        openMode: TelemetryStoreOpenMode.OpenExisting))),
            "schema futuro deve ser recusado para impedir downgrade destrutivo");
    }

    private static void TestOpenExistingDoesNotCreate()
    {
        using var temporary = new TemporaryDatabase(createFile: false);
        Check(
            Throws<FileNotFoundException>(
                () => SqliteTelemetryStore.Open(
                    new TelemetryStoreOptions(
                        temporary.DatabasePath,
                        openMode: TelemetryStoreOpenMode.OpenExisting))),
            "consulta não deve criar banco quando o caminho está errado");
        Check(!File.Exists(temporary.DatabasePath), "banco ausente deve continuar ausente");
    }

    private static SqliteTelemetryStore CreateStore(string path, int retentionDays = 30) =>
        SqliteTelemetryStore.Open(
            new TelemetryStoreOptions(path, TimeSpan.FromDays(retentionDays)));

    private static MonitoringRunOptions RunOptions(string uuid, DateTimeOffset startedAt) =>
        new(uuid, 1000, 256, 80, 5, "0.5.0-test", startedAt);

    private static GpuInfo FakeGpu() =>
        new(0, "Fake NVIDIA RTX", "GPU-EVIDENCE", "999.1", "99.1");

    private static GpuEvidenceSnapshot FakeSnapshot(GpuInfo gpu, DateTimeOffset observedAt)
    {
        var board = new BoardIdentity(
            gpu.Index,
            0x10de,
            0x2504,
            0x1b4c,
            0x1530,
            0,
            1,
            0,
            0,
            BoardIdentityFlags.PciValid | BoardIdentityFlags.VbiosValid,
            "00000000:01:00.0",
            "94.06.14.40.72");
        return new GpuEvidenceSnapshot(
            gpu,
            board,
            BoardEvidenceState.Available,
            null,
            observedAt);
    }

    private static TelemetryEvent SampleEvent(
        ulong sequence,
        GpuInfo gpu,
        int temperatureC,
        DateTimeOffset observedAt)
    {
        ulong timestamp = checked((ulong)observedAt.ToUnixTimeMilliseconds());
        var sample = new TemperatureSample(
            gpu.Index,
            temperatureC,
            TemperatureBackend.NvmlTemperatureV1,
            "NVML test",
            observedAt,
            timestamp);
        return new TelemetryEvent(
            sequence,
            TelemetryEventKind.Sample,
            gpu.Uuid,
            gpu,
            sample,
            observedAt,
            timestamp,
            MonitoringStatus.Ok,
            "ok",
            string.Empty,
            0,
            0);
    }

    private static TelemetryEvent GapEvent(
        ulong sequence,
        string uuid,
        DateTimeOffset observedAt) =>
        new(
            sequence,
            TelemetryEventKind.Gap,
            uuid,
            null,
            null,
            observedAt,
            checked((ulong)observedAt.ToUnixTimeMilliseconds()),
            MonitoringStatus.GpuLost,
            "GPU is inaccessible or lost",
            "Falha simulada.",
            1,
            250);

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

    private sealed class TemporaryDatabase : IDisposable
    {
        private readonly string directory;

        internal TemporaryDatabase(bool createFile = false)
        {
            directory = Path.Combine(
                Path.GetTempPath(),
                $"rtx-monitor-storage-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            DatabasePath = Path.Combine(directory, "telemetry.db");
            if (createFile)
            {
                using FileStream stream = File.Create(DatabasePath);
            }
        }

        internal string DatabasePath { get; }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            string resolvedDirectory = Path.GetFullPath(directory);
            string resolvedTemp = Path.GetFullPath(Path.GetTempPath());
            if (!resolvedDirectory.StartsWith(resolvedTemp, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(resolvedDirectory).StartsWith(
                    "rtx-monitor-storage-",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Recusa em remover diretório temporário inesperado: {resolvedDirectory}");
            }

            Directory.Delete(resolvedDirectory, recursive: true);
        }
    }
}
