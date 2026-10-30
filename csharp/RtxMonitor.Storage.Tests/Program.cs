using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using RtxMonitor.Managed;

namespace RtxMonitor.Storage.Tests;

internal static class Program
{
    private static int failures;

    private static async Task<int> Main()
    {
        Run("migration, restart, and evidence export", TestMigrationRestartAndEvidence);
        Run("legacy event versions remain unchanged after reopening", TestLegacyEventVersions);
        Run("experimental provenance survives restart, history, and export", TestExperimentalEvidence);
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
        sample = sample with
        {
            PublicTelemetry = new PublicTelemetryReport(gpu.Index, observedAt,
            sample.ObservedAtUnixMilliseconds,
            [FanRpmValue(0, 0, sample.ObservedAtUnixMilliseconds), FanRpmValue(1, null, sample.ObservedAtUnixMilliseconds)])
        };
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
            root.GetProperty("event").GetProperty("schema_version").GetInt32() == 5,
            "exportação deve incorporar o evento v5 sem alterar seu contrato");
        Check(
            root.GetProperty("run").GetProperty("event_schema_version").GetInt32() == 5,
            "exportação deve declarar o schema v5 também na proveniência do run");
        JsonElement fans = root.GetProperty("event").GetProperty("public_telemetry").GetProperty("fields");
        Check(fans[0].GetProperty("value_u64").GetUInt64() == 0 &&
            fans[0].GetProperty("provider_native_id").GetUInt32() == 0,
            "zero RPM disponível deve sobreviver ao SQLite sem virar ausência");
        Check(fans[1].GetProperty("value_u64").ValueKind == JsonValueKind.Null &&
            fans[1].GetProperty("state").GetString() == "not_supported" &&
            fans[1].GetProperty("provider_native_id").GetUInt32() == 1,
            "RPM ausente e índice do segundo fan devem sobreviver ao SQLite");
        Check(
            root.GetProperty("device_snapshot")
                .GetProperty("board")
                .GetProperty("profile_key")
                .GetString() == "10de:2504/1b4c:1530@94.06.14.40.72",
            "exportação deve incluir proveniência da placa");
    }

    private static void TestLegacyEventVersions()
    {
        using var temporary = new TemporaryDatabase();
        SqliteTelemetryStore store = CreateStore(temporary.DatabasePath);
        var expected = new Dictionary<string, (int Version, string Json)>();
        GpuInfo gpu = FakeGpu();
        for (int version = 1; version <= 7; version++)
        {
            DateTimeOffset observedAt = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000 + version);
            string runId = store.StartRun(RunOptions(gpu.Uuid, observedAt));
            TelemetryEvent sample = SampleEvent(1, gpu, 40 + version, observedAt);
            long eventId = store.AppendEvent(runId, sample);
            JsonObject legacy = JsonNode.Parse(TelemetryJson.Serialize(sample))!.AsObject();
            legacy["schema_version"] = version;
            if (version < 4) { legacy.Remove("windows_telemetry"); }
            if (version < 3) { legacy.Remove("public_telemetry"); legacy.Remove("computed_metrics"); }
            if (version < 2) { legacy.Remove("alert_threshold_c"); legacy.Remove("alert_hysteresis_c"); }
            string legacyJson = legacy.ToJsonString();
            if (version == 6)
                legacyJson = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "v011-candidate-event-v6-recorded.json")).TrimEnd();
            if (version == 7)
                legacyJson = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "v011-candidate-event-v7-recorded.json")).TrimEnd();
            using var connection = new SqliteConnection($"Data Source={temporary.DatabasePath}");
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "UPDATE telemetry_events SET event_schema_version=$version,event_json=$json WHERE event_id=$id; UPDATE monitor_runs SET event_schema_version=$version WHERE run_id=$run;";
            command.Parameters.AddWithValue("$version", version);
            command.Parameters.AddWithValue("$json", legacyJson);
            command.Parameters.AddWithValue("$id", eventId);
            command.Parameters.AddWithValue("$run", runId);
            command.ExecuteNonQuery();
            expected.Add(runId, (version, legacyJson));
        }
        SqliteConnection.ClearAllPools();
        SqliteTelemetryStore reopened = SqliteTelemetryStore.Open(new TelemetryStoreOptions(
            temporary.DatabasePath, openMode: TelemetryStoreOpenMode.OpenExisting));
        foreach (StoredTelemetryEvidence evidence in reopened.QueryEvents(new TelemetryEventQuery(Limit: 10)))
        {
            (int version, string json) = expected[evidence.Run.RunId];
            Check(evidence.EventSchemaVersion == version && evidence.Run.EventSchemaVersion == version &&
                evidence.EventJson == json, "reabertura não pode migrar ou reserializar eventos históricos");
            using JsonDocument exported = JsonDocument.Parse(EvidenceJson.Serialize(evidence));
            Check(exported.RootElement.GetProperty("event").GetRawText() == json,
                "exportação deve preservar integralmente cada contrato histórico");
        }
        Check(reopened.QueryEvents(new TelemetryEventQuery(Limit: 10)).Count == 7,
            "as sete versões históricas, incluindo os dois contratos candidatos, devem continuar consultáveis");
    }

    private static void TestExperimentalEvidence()
    {
        using var temporary = new TemporaryDatabase();
        using JsonDocument thermalDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "rtx3060-thermal-synthetic-v2.json")));
        using JsonDocument voltageDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "rtx3060-voltage-recorded-v1.json")));
        JsonElement thermal = thermalDocument.RootElement;
        JsonElement voltage = voltageDocument.RootElement;
        DateTimeOffset thermalAt = DateTimeOffset.FromUnixTimeMilliseconds(thermal.GetProperty("captured_at_unix_ms").GetInt64());
        DateTimeOffset voltageAt = DateTimeOffset.FromUnixTimeMilliseconds(voltage.GetProperty("captured_at_unix_ms").GetInt64());
        var gpu = new GpuInfo(0, "NVIDIA GeForce RTX 3060", ExperimentalTelemetryReport.SupportedGpuUuid, "610.88", "13.610.88");
        var board = new BoardIdentity(0, 0x10de, 0x2504, 0x10de, 0x1536, 0, 1, 0, 0,
            BoardIdentityFlags.PciValid | BoardIdentityFlags.VbiosValid, "00000000:01:00.0", "94.06.25.00.fc");
        string[] identityFields = ["pci_vendor_id", "pci_device_id", "pci_subsystem_vendor_id",
            "pci_subsystem_device_id", "gpu_uuid", "vbios_version", "driver_version"];
        var profile = new PrivateProfileStatus(gpu.Index, ExperimentalTelemetryReport.SupportedProfileId,
            ExperimentalTelemetryReport.SupportedProfileRevision, PrivateProfileState.Active, null,
            identityFields.Select(field => new PrivateProfileIdentityCheck(field, "matched")).ToArray(),
            PrivateOperationState.Compatible, PrivateOperationState.Compatible, 100, 2000, 100, 2000);

        // The thermal vector/timing is synthetic; voltage retains its distinct recorded capture time.
        ExperimentalTelemetryReport voltageReport = ExperimentalTelemetryReport.Create(
            gpu, board, profile, null, voltage, evaluatedAt: voltageAt);
        ExperimentalTelemetryReport thermalReport = ExperimentalTelemetryReport.Create(
            gpu, board, profile, thermal, null, evaluatedAt: thermalAt);
        Check(thermalReport.Fields[0].State == ExperimentalTelemetryState.Available &&
            thermalReport.Fields[0].RawValue == 12168 && thermalReport.Fields[0].Value == 47.53125 &&
            thermalReport.Fields[0].Acquisition?.StartedMonotonicMilliseconds == 123456,
            "fixture térmica deve manter o valor bruto ABI e a leitura registrada");
        Check(voltageReport.Fields[1].State == ExperimentalTelemetryState.Available &&
            voltageReport.Fields[1].RawValue == 956250 && voltageReport.Fields[1].Value == 0.95625,
            "fixture de tensão deve manter microvolts e volts registrados");

        JsonObject malformed = JsonNode.Parse(thermal.GetRawText())!.AsObject();
        malformed["nvapi_module_sha256"] = new string('0', 64);
        ExperimentalTelemetryReport invalidReport = ExperimentalTelemetryReport.Create(gpu, board, profile,
            JsonSerializer.SerializeToElement(malformed), null, evaluatedAt: thermalAt);
        Check(invalidReport.Fields[0].State == ExperimentalTelemetryState.PayloadInvalid,
            "proveniência adulterada deve falhar com payload_invalid antes de persistir");

        ExperimentalTelemetryReport[] reports =
        [
            voltageReport,
            thermalReport,
            invalidReport,
            ExperimentalTelemetryReport.Create(gpu, board, profile with { ProfileId = "unknown-profile" },
                thermal, voltage, evaluatedAt: thermalAt),
            ExperimentalTelemetryReport.Create(gpu with { DriverVersion = "999.1" }, board, profile,
                thermal, voltage, evaluatedAt: thermalAt),
            ExperimentalTelemetryReport.Create(gpu, board, profile with
                { ProfileState = PrivateProfileState.Revoked, RevocationReason = "fixture_revoked" },
                thermal, voltage, evaluatedAt: thermalAt),
            thermalReport.WithFailure(ExperimentalTelemetryState.Timeout, thermalAt.AddSeconds(1)),
            thermalReport.WithFailure(ExperimentalTelemetryState.QueryFailed, thermalAt.AddSeconds(2)),
            thermalReport.WithFailure(ExperimentalTelemetryState.WorkerUnavailable, thermalAt.AddSeconds(3)),
            thermalReport.WithFailure(ExperimentalTelemetryState.Disabled, thermalAt.AddSeconds(4)),
        ];
        Check(reports[3].Fields.All(field => field.State == ExperimentalTelemetryState.ProfileUnknown) &&
            reports[4].Fields.All(field => field.State == ExperimentalTelemetryState.IdentityMismatch) &&
            reports[5].Fields.All(field => field.State == ExperimentalTelemetryState.Revoked),
            "perfil desconhecido, identidade alterada e revogação devem ter estados distintos");

        SqliteTelemetryStore store = CreateStore(temporary.DatabasePath);
        string runId = store.StartRun(RunOptions(gpu.Uuid, voltageAt.AddSeconds(-1)));
        var expected = new Dictionary<ulong, (string EventJson, string? ReportJson)>();
        TelemetryEvent initial = SampleEvent(1, gpu, 35, voltageAt.AddSeconds(-1));
        expected.Add(1, (TelemetryJson.Serialize(initial), null));
        store.AppendEvent(runId, initial);
        StoredTelemetryEvidence first = store.QueryEvents(new TelemetryEventQuery(RunId: runId, Limit: 1))[0];
        Check(first.EventSchemaVersion == 5 && first.Run.EventSchemaVersion == 5 &&
            !JsonNode.Parse(first.EventJson)!.AsObject().ContainsKey("profile_telemetry"),
            "evento padrão e run sem relatório experimental devem continuar v5 sem novo campo");

        (GpuInfo Gpu, string Name)[] mismatches =
        [
            (gpu with { Uuid = "GPU-00000000-0000-0000-0000-000000000001" }, "UUID alvo"),
            (gpu with { Index = 1 }, "índice"),
            (gpu with { DriverVersion = "611.00" }, "driver"),
            (gpu with { NvmlVersion = "changed-nvml" }, "NVML"),
        ];
        foreach ((GpuInfo mismatchedGpu, string name) in mismatches)
        {
            string rejectedRun = store.StartRun(RunOptions(mismatchedGpu.Uuid, thermalAt));
            TelemetryEvent mismatched = SampleEvent(1, mismatchedGpu, 35, thermalAt) with
            { ExperimentalTelemetry = thermalReport };
            Check(Throws<ArgumentException>(() => TelemetryJson.Serialize(mismatched)) &&
                Throws<ArgumentException>(() => store.AppendEvent(rejectedRun, mismatched)) &&
                store.QueryEvents(new TelemetryEventQuery(RunId: rejectedRun, Limit: 1)).Count == 0,
                $"relatório disponível com {name} divergente não pode ser serializado ou persistido");
        }
        TelemetryEvent wrongTargetFailure = SampleEvent(1, mismatches[0].Gpu, 35, thermalAt) with
        { ExperimentalTelemetry = thermalReport.WithFailure(ExperimentalTelemetryState.Timeout, thermalAt) };
        string wrongFailureRun = store.StartRun(RunOptions(mismatches[0].Gpu.Uuid, thermalAt));
        Check(Throws<ArgumentException>(() => TelemetryJson.Serialize(wrongTargetFailure)) &&
            Throws<ArgumentException>(() => store.AppendEvent(wrongFailureRun, wrongTargetFailure)),
            "relatório de falha também deve corresponder ao UUID alvo");

        GpuInfo uppercaseGpu = gpu with { Uuid = gpu.Uuid.ToUpperInvariant() };
        string uppercaseRun = store.StartRun(RunOptions(uppercaseGpu.Uuid, thermalAt));
        TelemetryEvent uppercase = SampleEvent(1, uppercaseGpu, 35, thermalAt) with
        { ExperimentalTelemetry = thermalReport };
        store.AppendEvent(uppercaseRun, uppercase);
        Check(store.QueryEvents(new TelemetryEventQuery(RunId: uppercaseRun, Limit: 1)).Count == 1,
            "a mesma identidade com UUID em caixa diferente deve continuar compatível");

        ulong sequence = 2;
        foreach (ExperimentalTelemetryReport report in reports)
        {
            bool gap = sequence % 2 == 0 && sequence > 3;
            TelemetryEvent item = (gap ? GapEvent(sequence, gpu.Uuid, report.CapturedAt)
                : SampleEvent(sequence, gpu, 35, report.CapturedAt)) with
            { ExperimentalTelemetry = report };
            string eventJson = TelemetryJson.Serialize(item);
            string reportJson = ExperimentalTelemetryJson.Serialize(report);
            expected.Add(sequence, (eventJson, reportJson));
            long eventId = store.AppendEvent(runId, item);
            Check(store.AppendEvent(runId, item) == eventId, "retry experimental deve ser idempotente");
            Check(Throws<TelemetrySequenceConflictException>(() => store.AppendEvent(runId, item with
            { ExperimentalTelemetry = report.WithFailure(ExperimentalTelemetryState.Cancelled, report.CapturedAt) })),
                "a mesma sequência não pode trocar proveniência ou estado experimental");
            sequence++;
        }
        TelemetryEvent finalStable = SampleEvent(sequence, gpu, 36, thermalAt.AddSeconds(5));
        expected.Add(sequence, (TelemetryJson.Serialize(finalStable), null));
        store.AppendEvent(runId, finalStable);
        Check(Throws<ArgumentException>(() => TelemetryJson.Serialize(initial with
        { Kind = TelemetryEventKind.AlertRaised, ExperimentalTelemetry = thermalReport })),
            "alertas não podem herdar um bloco experimental fora do contrato v7");

        SqliteConnection.ClearAllPools();
        SqliteTelemetryStore reopened = SqliteTelemetryStore.Open(new TelemetryStoreOptions(
            temporary.DatabasePath, openMode: TelemetryStoreOpenMode.OpenExisting));
        reopened.VerifyIntegrity();
        IReadOnlyList<StoredTelemetryEvidence> history = reopened.QueryEvents(
            new TelemetryEventQuery(RunId: runId, Limit: 100, Ascending: true));
        Check(history.Count == expected.Count && reopened.GetSchemaVersion() == 1,
            "o JSON canônico existente deve preservar todos os eventos sem migration redundante");
        foreach (StoredTelemetryEvidence evidence in history)
        {
            (string eventJson, string? reportJson) = expected[evidence.StreamSequence];
            Check(evidence.EventJson == eventJson && evidence.EventSchemaVersion == (reportJson is null ? 5 : 8),
                "histórico deve preservar bytes do evento e sua versão efetiva");
            Check(evidence.Run.EventSchemaVersion == 8,
                "proveniência do run deve manter a maior versão após novos eventos v5");
            using JsonDocument exported = JsonDocument.Parse(EvidenceJson.Serialize(evidence));
            JsonElement exportedEvent = exported.RootElement.GetProperty("event");
            Check(exportedEvent.GetRawText() == eventJson, "exportação deve manter integralmente o evento gravado");
            if (reportJson is null) continue;
            JsonElement report = exportedEvent.GetProperty("profile_telemetry");
            Check(report.GetRawText() == reportJson && report.GetProperty("origin").GetString() == "private_profile",
                "API de serialização, evento, SQLite, histórico e exportação devem preservar a mesma proveniência");
            Check(report.GetProperty("fields").GetArrayLength() == 2,
                "canal experimental só deve transportar hotspot e tensão de núcleo");
            foreach (JsonElement field in report.GetProperty("fields").EnumerateArray())
            {
                if (field.GetProperty("state").GetString() == "available") continue;
                Check(field.GetProperty("value").ValueKind == JsonValueKind.Null &&
                    field.GetProperty("raw_value").ValueKind == JsonValueKind.Null &&
                    field.GetProperty("captured_at_unix_ms").ValueKind == JsonValueKind.Null,
                    "falha deve continuar explícita e nula sem reutilizar leitura anterior");
            }
        }
    }

    private static PublicTelemetryValue FanRpmValue(uint fanIndex, ulong? rpm, ulong timestamp) => new(
        PublicTelemetryField.FanSpeedIntendedRpm, "fan_speed_intended_rpm",
        PublicTelemetryProvider.NvmlFanSpeedRpm, "NVML nvmlDeviceGetFanSpeedRPM",
        rpm.HasValue ? CapabilityState.Available : CapabilityState.NotSupported,
        rpm.HasValue ? "available" : "not_supported", DataOrigin.DriverReported, "driver_reported",
        rpm.HasValue ? TelemetryValueType.UnsignedInteger : TelemetryValueType.Unknown,
        rpm.HasValue ? "unsigned_integer" : "unknown", TelemetryUnit.Rpm, "rpm",
        rpm.HasValue ? 0 : 3, fanIndex, rpm, null, null, timestamp);

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
