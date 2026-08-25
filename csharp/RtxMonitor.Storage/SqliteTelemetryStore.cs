using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using RtxMonitor.Managed;

namespace RtxMonitor.Storage;

public sealed class SqliteTelemetryStore
{
    public const int CurrentSchemaVersion = 1;

    private const string InitialMigrationName = "initial_evidence_store";

    private readonly TelemetryStoreOptions options;
    private readonly string connectionString;

    private SqliteTelemetryStore(TelemetryStoreOptions options)
    {
        this.options = options;
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = options.DatabasePath,
            Mode = options.OpenMode == TelemetryStoreOpenMode.OpenExisting
                ? SqliteOpenMode.ReadWrite
                : SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
        }.ToString();
    }

    public string DatabasePath => options.DatabasePath;

    public static SqliteTelemetryStore Open(TelemetryStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.OpenMode == TelemetryStoreOpenMode.OpenExisting &&
            !File.Exists(options.DatabasePath))
        {
            throw new FileNotFoundException(
                "O banco de telemetria não existe.",
                options.DatabasePath);
        }

        if (options.OpenMode == TelemetryStoreOpenMode.CreateOrOpen)
        {
            string? directory = Path.GetDirectoryName(options.DatabasePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }

        var store = new SqliteTelemetryStore(options);
        try
        {
            store.ExecuteWithStoreErrors("inicializar o banco", store.Initialize);
        }
        catch
        {
            store.ClearConnectionPool();
            throw;
        }
        return store;
    }

    public int GetSchemaVersion() => ExecuteWithStoreErrors(
        "consultar a versão do schema",
        () =>
        {
            using SqliteConnection connection = OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version;";
            return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        });

    public void VerifyIntegrity() => ExecuteWithStoreErrors(
        "verificar a integridade do banco",
        () =>
        {
            using SqliteConnection connection = OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "PRAGMA quick_check;";
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                string result = reader.GetString(0);
                if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                {
                    throw new TelemetryStoreException(
                        $"A verificação de integridade do SQLite falhou: {result}");
                }
            }
        });

    public string StartRun(MonitoringRunOptions runOptions)
    {
        ValidateRunOptions(runOptions);
        string runId = Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture);

        return ExecuteWithStoreErrors(
            "iniciar uma sessão de monitoramento",
            () =>
            {
                using SqliteConnection connection = OpenConnection();
                using SqliteCommand command = connection.CreateCommand();
                command.CommandText =
                    """
                    INSERT INTO monitor_runs (
                        run_id,
                        event_schema_version,
                        started_at_unix_ms,
                        target_gpu_uuid,
                        interval_ms,
                        buffer_capacity,
                        alert_threshold_c,
                        alert_hysteresis_c,
                        retention_days,
                        application_version,
                        os_description,
                        os_architecture,
                        process_architecture
                    ) VALUES (
                        $run_id,
                        $event_schema_version,
                        $started_at_unix_ms,
                        $target_gpu_uuid,
                        $interval_ms,
                        $buffer_capacity,
                        $alert_threshold_c,
                        $alert_hysteresis_c,
                        $retention_days,
                        $application_version,
                        $os_description,
                        $os_architecture,
                        $process_architecture
                    );
                    """;
                command.Parameters.AddWithValue("$run_id", runId);
                command.Parameters.AddWithValue("$event_schema_version", TelemetryJson.SchemaVersion);
                command.Parameters.AddWithValue(
                    "$started_at_unix_ms",
                    runOptions.StartedAt.ToUnixTimeMilliseconds());
                command.Parameters.AddWithValue("$target_gpu_uuid", runOptions.TargetGpuUuid);
                command.Parameters.AddWithValue("$interval_ms", runOptions.IntervalMilliseconds);
                command.Parameters.AddWithValue("$buffer_capacity", runOptions.BufferCapacity);
                AddNullable(command, "$alert_threshold_c", runOptions.AlertThresholdC);
                command.Parameters.AddWithValue(
                    "$alert_hysteresis_c",
                    runOptions.AlertHysteresisC);
                command.Parameters.AddWithValue(
                    "$retention_days",
                    options.RetentionPeriod.TotalDays);
                command.Parameters.AddWithValue(
                    "$application_version",
                    runOptions.ApplicationVersion);
                command.Parameters.AddWithValue("$os_description", RuntimeInformation.OSDescription);
                command.Parameters.AddWithValue(
                    "$os_architecture",
                    RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant());
                command.Parameters.AddWithValue(
                    "$process_architecture",
                    RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant());
                command.ExecuteNonQuery();
                return runId;
            });
    }

    public long RegisterGpuSnapshot(string runId, GpuEvidenceSnapshot snapshot)
    {
        ValidateRunId(runId);
        ValidateSnapshot(snapshot);

        return ExecuteWithStoreErrors(
            "registrar a identidade da GPU",
            () =>
            {
                using SqliteConnection connection = OpenConnection();
                using SqliteTransaction transaction = connection.BeginTransaction();
                ValidateEventContext(
                    connection,
                    transaction,
                    runId,
                    snapshot.Gpu.Uuid,
                    null);
                using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    """
                    INSERT INTO gpu_snapshots (
                        run_id,
                        observed_at_unix_ms,
                        gpu_index,
                        gpu_name,
                        gpu_uuid,
                        driver_version,
                        nvml_version,
                        board_capture_state,
                        board_capture_error,
                        board_flags,
                        pci_vendor_id,
                        pci_device_id,
                        pci_subsystem_vendor_id,
                        pci_subsystem_device_id,
                        pci_domain,
                        pci_bus,
                        pci_device,
                        pci_function,
                        pci_bus_id,
                        vbios_version,
                        profile_key
                    ) VALUES (
                        $run_id,
                        $observed_at_unix_ms,
                        $gpu_index,
                        $gpu_name,
                        $gpu_uuid,
                        $driver_version,
                        $nvml_version,
                        $board_capture_state,
                        $board_capture_error,
                        $board_flags,
                        $pci_vendor_id,
                        $pci_device_id,
                        $pci_subsystem_vendor_id,
                        $pci_subsystem_device_id,
                        $pci_domain,
                        $pci_bus,
                        $pci_device,
                        $pci_function,
                        $pci_bus_id,
                        $vbios_version,
                        $profile_key
                    );
                    """;
                command.Parameters.AddWithValue("$run_id", runId);
                command.Parameters.AddWithValue(
                    "$observed_at_unix_ms",
                    snapshot.ObservedAt.ToUnixTimeMilliseconds());
                command.Parameters.AddWithValue("$gpu_index", snapshot.Gpu.Index);
                command.Parameters.AddWithValue("$gpu_name", snapshot.Gpu.Name);
                command.Parameters.AddWithValue("$gpu_uuid", snapshot.Gpu.Uuid);
                command.Parameters.AddWithValue("$driver_version", snapshot.Gpu.DriverVersion);
                command.Parameters.AddWithValue("$nvml_version", snapshot.Gpu.NvmlVersion);
                command.Parameters.AddWithValue("$board_capture_state", snapshot.BoardStateName);
                AddNullable(command, "$board_capture_error", snapshot.BoardError);
                AddBoardParameters(command, snapshot.Board, snapshot.ProfileKey);
                command.ExecuteNonQuery();

                using SqliteCommand idCommand = connection.CreateCommand();
                idCommand.Transaction = transaction;
                idCommand.CommandText = "SELECT last_insert_rowid();";
                long snapshotId = Convert.ToInt64(
                    idCommand.ExecuteScalar(),
                    CultureInfo.InvariantCulture);
                transaction.Commit();
                return snapshotId;
            });
    }

    public long AppendEvent(
        string runId,
        TelemetryEvent telemetryEvent,
        long? gpuSnapshotId = null)
    {
        ValidateRunId(runId);
        ValidateEvent(telemetryEvent);
        if (gpuSnapshotId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(gpuSnapshotId),
                "O ID do snapshot deve ser positivo.");
        }

        string eventJson = TelemetryJson.Serialize(telemetryEvent);
        long storedAtUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        return ExecuteWithStoreErrors(
            "persistir um evento de telemetria",
            () =>
            {
                using SqliteConnection connection = OpenConnection();
                using SqliteTransaction transaction = connection.BeginTransaction();
                ValidateEventContext(
                    connection,
                    transaction,
                    runId,
                    telemetryEvent.TargetGpuUuid,
                    gpuSnapshotId);
                using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    """
                    INSERT INTO telemetry_events (
                        run_id,
                        gpu_snapshot_id,
                        event_schema_version,
                        stream_sequence,
                        event_type,
                        target_gpu_uuid,
                        observed_at_unix_ms,
                        stored_at_unix_ms,
                        status_code,
                        temperature_c,
                        sample_backend,
                        sample_timestamp_unix_ms,
                        alert_threshold_c,
                        alert_hysteresis_c,
                        event_json
                    ) VALUES (
                        $run_id,
                        $gpu_snapshot_id,
                        $event_schema_version,
                        $stream_sequence,
                        $event_type,
                        $target_gpu_uuid,
                        $observed_at_unix_ms,
                        $stored_at_unix_ms,
                        $status_code,
                        $temperature_c,
                        $sample_backend,
                        $sample_timestamp_unix_ms,
                        $alert_threshold_c,
                        $alert_hysteresis_c,
                        $event_json
                    )
                    ON CONFLICT(run_id, stream_sequence) DO NOTHING;
                    """;
                command.Parameters.AddWithValue("$run_id", runId);
                AddNullable(command, "$gpu_snapshot_id", gpuSnapshotId);
                command.Parameters.AddWithValue("$event_schema_version", TelemetryJson.SchemaVersion);
                command.Parameters.AddWithValue(
                    "$stream_sequence",
                    checked((long)telemetryEvent.Sequence));
                command.Parameters.AddWithValue("$event_type", telemetryEvent.KindName);
                command.Parameters.AddWithValue("$target_gpu_uuid", telemetryEvent.TargetGpuUuid);
                command.Parameters.AddWithValue(
                    "$observed_at_unix_ms",
                    checked((long)telemetryEvent.ObservedAtUnixMilliseconds));
                command.Parameters.AddWithValue("$stored_at_unix_ms", storedAtUnixMilliseconds);
                command.Parameters.AddWithValue("$status_code", (int)telemetryEvent.Status);
                AddNullable(command, "$temperature_c", telemetryEvent.Sample?.TemperatureC);
                AddNullable(command, "$sample_backend", telemetryEvent.Sample?.BackendName);
                AddNullable(
                    command,
                    "$sample_timestamp_unix_ms",
                    telemetryEvent.Sample is TemperatureSample sample
                        ? checked((long)sample.TimestampUnixMilliseconds)
                        : null);
                AddNullable(command, "$alert_threshold_c", telemetryEvent.AlertThresholdC);
                AddNullable(command, "$alert_hysteresis_c", telemetryEvent.AlertHysteresisC);
                command.Parameters.AddWithValue("$event_json", eventJson);

                int inserted = command.ExecuteNonQuery();
                long eventId;
                if (inserted == 1)
                {
                    using SqliteCommand idCommand = connection.CreateCommand();
                    idCommand.Transaction = transaction;
                    idCommand.CommandText = "SELECT last_insert_rowid();";
                    eventId = Convert.ToInt64(
                        idCommand.ExecuteScalar(),
                        CultureInfo.InvariantCulture);
                }
                else
                {
                    eventId = ReadExistingEventId(
                        connection,
                        transaction,
                        runId,
                        telemetryEvent.Sequence,
                        gpuSnapshotId,
                        eventJson);
                }

                transaction.Commit();
                return eventId;
            });
    }

    public void CompleteRun(
        string runId,
        string completionReason,
        DateTimeOffset completedAt)
    {
        ValidateRunId(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(completionReason);
        if (completionReason.Length > 64)
        {
            throw new ArgumentOutOfRangeException(
                nameof(completionReason),
                "O motivo de encerramento deve ter até 64 caracteres.");
        }

        ExecuteWithStoreErrors(
            "encerrar uma sessão de monitoramento",
            () =>
            {
                using SqliteConnection connection = OpenConnection();
                using SqliteCommand command = connection.CreateCommand();
                command.CommandText =
                    """
                    UPDATE monitor_runs
                    SET completed_at_unix_ms = $completed_at_unix_ms,
                        completion_reason = $completion_reason
                    WHERE run_id = $run_id
                      AND completed_at_unix_ms IS NULL;
                    """;
                command.Parameters.AddWithValue(
                    "$completed_at_unix_ms",
                    completedAt.ToUnixTimeMilliseconds());
                command.Parameters.AddWithValue("$completion_reason", completionReason);
                command.Parameters.AddWithValue("$run_id", runId);
                int updated = command.ExecuteNonQuery();
                if (updated == 0 && !RunExists(connection, runId))
                {
                    throw new TelemetryStoreException($"A sessão {runId} não existe.");
                }
            });
    }

    public IReadOnlyList<StoredTelemetryEvidence> QueryEvents(TelemetryEventQuery query)
    {
        ValidateQuery(query);
        return ExecuteWithStoreErrors(
            "consultar o histórico de telemetria",
            () => QueryEventsCore(query));
    }

    public long? GetMaximumEventId() => ExecuteWithStoreErrors<long?>(
        "consultar o último event_id",
        () =>
        {
            using SqliteConnection connection = OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT MAX(event_id) FROM telemetry_events;";
            object? result = command.ExecuteScalar();
            return result is null or DBNull
                ? null
                : Convert.ToInt64(result, CultureInfo.InvariantCulture);
        });

    public RetentionResult ApplyRetention(DateTimeOffset observedAt)
    {
        long cutoff = observedAt.Subtract(options.RetentionPeriod).ToUnixTimeMilliseconds();
        return ExecuteWithStoreErrors(
            "aplicar a política de retenção",
            () =>
            {
                using SqliteConnection connection = OpenConnection();
                using SqliteTransaction transaction = connection.BeginTransaction();

                long eventsDeleted = ExecuteDelete(
                    connection,
                    transaction,
                    "DELETE FROM telemetry_events WHERE observed_at_unix_ms < $cutoff;",
                    cutoff);
                long snapshotsDeleted = ExecuteDelete(
                    connection,
                    transaction,
                    """
                    DELETE FROM gpu_snapshots
                    WHERE observed_at_unix_ms < $cutoff
                      AND NOT EXISTS (
                        SELECT 1
                        FROM telemetry_events
                        WHERE telemetry_events.gpu_snapshot_id = gpu_snapshots.snapshot_id
                    );
                    """,
                    cutoff);
                long runsDeleted = ExecuteDelete(
                    connection,
                    transaction,
                    """
                    DELETE FROM monitor_runs
                    WHERE COALESCE(completed_at_unix_ms, started_at_unix_ms) < $cutoff
                      AND NOT EXISTS (
                          SELECT 1
                          FROM telemetry_events
                          WHERE telemetry_events.run_id = monitor_runs.run_id
                      );
                    """,
                    cutoff);

                transaction.Commit();
                return new RetentionResult(eventsDeleted, snapshotsDeleted, runsDeleted);
            });
    }

    private void Initialize()
    {
        using SqliteConnection connection = OpenConnection();

        using (SqliteCommand journalCommand = connection.CreateCommand())
        {
            journalCommand.CommandText = "PRAGMA journal_mode = WAL;";
            string? journalMode = Convert.ToString(
                journalCommand.ExecuteScalar(),
                CultureInfo.InvariantCulture);
            if (!string.Equals(journalMode, "wal", StringComparison.OrdinalIgnoreCase))
            {
                throw new TelemetryStoreException(
                    $"O SQLite não ativou WAL; modo retornado: {journalMode ?? "null"}.");
            }
        }

        int version;
        using (SqliteCommand versionCommand = connection.CreateCommand())
        {
            versionCommand.CommandText = "PRAGMA user_version;";
            version = Convert.ToInt32(
                versionCommand.ExecuteScalar(),
                CultureInfo.InvariantCulture);
        }

        if (version > CurrentSchemaVersion)
        {
            throw new TelemetryStoreException(
                $"O banco usa schema {version}, mas esta versão suporta até {CurrentSchemaVersion}.");
        }

        if (version == 0)
        {
            ApplyInitialMigration(connection);
            version = CurrentSchemaVersion;
        }

        if (version != CurrentSchemaVersion)
        {
            throw new TelemetryStoreException(
                $"Não existe migration do schema {version} para {CurrentSchemaVersion}.");
        }

        ValidateSchema(connection);
    }

    private static void ApplyInitialMigration(SqliteConnection connection)
    {
        using SqliteTransaction transaction = connection.BeginTransaction();
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            CREATE TABLE schema_migrations (
                version INTEGER PRIMARY KEY,
                name TEXT NOT NULL,
                applied_at_unix_ms INTEGER NOT NULL
            ) STRICT;

            CREATE TABLE monitor_runs (
                run_id TEXT PRIMARY KEY,
                event_schema_version INTEGER NOT NULL,
                started_at_unix_ms INTEGER NOT NULL,
                completed_at_unix_ms INTEGER,
                completion_reason TEXT,
                target_gpu_uuid TEXT NOT NULL COLLATE NOCASE,
                interval_ms INTEGER NOT NULL CHECK (interval_ms BETWEEN 100 AND 60000),
                buffer_capacity INTEGER NOT NULL CHECK (buffer_capacity BETWEEN 1 AND 65536),
                alert_threshold_c INTEGER CHECK (alert_threshold_c BETWEEN 0 AND 500),
                alert_hysteresis_c INTEGER NOT NULL CHECK (alert_hysteresis_c BETWEEN 0 AND 500),
                retention_days REAL NOT NULL CHECK (retention_days BETWEEN 1 AND 3650),
                application_version TEXT NOT NULL,
                os_description TEXT NOT NULL,
                os_architecture TEXT NOT NULL,
                process_architecture TEXT NOT NULL,
                CHECK (alert_threshold_c IS NOT NULL OR alert_hysteresis_c = 0)
            ) STRICT;

            CREATE TABLE gpu_snapshots (
                snapshot_id INTEGER PRIMARY KEY AUTOINCREMENT,
                run_id TEXT NOT NULL,
                observed_at_unix_ms INTEGER NOT NULL,
                gpu_index INTEGER NOT NULL CHECK (gpu_index >= 0),
                gpu_name TEXT NOT NULL,
                gpu_uuid TEXT NOT NULL COLLATE NOCASE,
                driver_version TEXT NOT NULL,
                nvml_version TEXT NOT NULL,
                board_capture_state TEXT NOT NULL CHECK (
                    board_capture_state IN ('not_attempted', 'available', 'query_failed')
                ),
                board_capture_error TEXT,
                board_flags INTEGER,
                pci_vendor_id INTEGER,
                pci_device_id INTEGER,
                pci_subsystem_vendor_id INTEGER,
                pci_subsystem_device_id INTEGER,
                pci_domain INTEGER,
                pci_bus INTEGER,
                pci_device INTEGER,
                pci_function INTEGER,
                pci_bus_id TEXT,
                vbios_version TEXT,
                profile_key TEXT,
                UNIQUE (snapshot_id, run_id),
                FOREIGN KEY (run_id) REFERENCES monitor_runs(run_id) ON DELETE CASCADE,
                CHECK (board_capture_state != 'available' OR board_flags IS NOT NULL)
            ) STRICT;

            CREATE TABLE telemetry_events (
                event_id INTEGER PRIMARY KEY AUTOINCREMENT,
                run_id TEXT NOT NULL,
                gpu_snapshot_id INTEGER,
                event_schema_version INTEGER NOT NULL,
                stream_sequence INTEGER NOT NULL CHECK (stream_sequence >= 1),
                event_type TEXT NOT NULL CHECK (
                    event_type IN ('sample', 'gap', 'recovered', 'alert_raised', 'alert_cleared')
                ),
                target_gpu_uuid TEXT NOT NULL COLLATE NOCASE,
                observed_at_unix_ms INTEGER NOT NULL CHECK (observed_at_unix_ms >= 0),
                stored_at_unix_ms INTEGER NOT NULL CHECK (stored_at_unix_ms >= 0),
                status_code INTEGER NOT NULL CHECK (status_code BETWEEN 0 AND 11),
                temperature_c INTEGER,
                sample_backend TEXT,
                sample_timestamp_unix_ms INTEGER,
                alert_threshold_c INTEGER,
                alert_hysteresis_c INTEGER,
                event_json TEXT NOT NULL,
                UNIQUE (run_id, stream_sequence),
                FOREIGN KEY (run_id) REFERENCES monitor_runs(run_id) ON DELETE CASCADE,
                FOREIGN KEY (gpu_snapshot_id, run_id)
                    REFERENCES gpu_snapshots(snapshot_id, run_id),
                CHECK (
                    (event_type IN ('sample', 'alert_raised', 'alert_cleared') AND temperature_c IS NOT NULL)
                    OR
                    (event_type IN ('gap', 'recovered') AND temperature_c IS NULL)
                )
            ) STRICT;

            CREATE INDEX idx_telemetry_events_target_time
                ON telemetry_events(target_gpu_uuid, observed_at_unix_ms DESC);
            CREATE INDEX idx_telemetry_events_type_time
                ON telemetry_events(event_type, observed_at_unix_ms DESC);
            CREATE INDEX idx_telemetry_events_snapshot
                ON telemetry_events(gpu_snapshot_id);

            INSERT INTO schema_migrations(version, name, applied_at_unix_ms)
            VALUES (1, $migration_name, $applied_at_unix_ms);

            PRAGMA user_version = 1;
            """;
        command.Parameters.AddWithValue("$migration_name", InitialMigrationName);
        command.Parameters.AddWithValue(
            "$applied_at_unix_ms",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    private static void ValidateSchema(SqliteConnection connection)
    {
        string[] requiredTables =
        [
            "schema_migrations",
            "monitor_runs",
            "gpu_snapshots",
            "telemetry_events",
        ];

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT name
            FROM sqlite_master
            WHERE type = 'table';
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        var tables = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            tables.Add(reader.GetString(0));
        }

        string[] missing = requiredTables.Where(table => !tables.Contains(table)).ToArray();
        if (missing.Length > 0)
        {
            throw new TelemetryStoreException(
                $"O banco declara schema {CurrentSchemaVersion}, mas faltam tabelas: {string.Join(", ", missing)}.");
        }
    }

    private IReadOnlyList<StoredTelemetryEvidence> QueryEventsCore(TelemetryEventQuery query)
    {
        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        var conditions = new List<string>();

        if (query.RunId is not null)
        {
            conditions.Add("e.run_id = $run_id");
            command.Parameters.AddWithValue("$run_id", query.RunId);
        }
        if (query.TargetGpuUuid is not null)
        {
            conditions.Add("e.target_gpu_uuid = $target_gpu_uuid COLLATE NOCASE");
            command.Parameters.AddWithValue("$target_gpu_uuid", query.TargetGpuUuid);
        }
        if (query.EventKind is TelemetryEventKind kind)
        {
            conditions.Add("e.event_type = $event_type");
            command.Parameters.AddWithValue("$event_type", EventKindName(kind));
        }
        if (query.FromUnixMilliseconds is long from)
        {
            conditions.Add("e.observed_at_unix_ms >= $from_unix_ms");
            command.Parameters.AddWithValue("$from_unix_ms", from);
        }
        if (query.ToUnixMilliseconds is long to)
        {
            conditions.Add("e.observed_at_unix_ms <= $to_unix_ms");
            command.Parameters.AddWithValue("$to_unix_ms", to);
        }
        if (query.AfterSequence is ulong afterSequence)
        {
            conditions.Add("e.stream_sequence > $after_sequence");
            command.Parameters.AddWithValue("$after_sequence", checked((long)afterSequence));
        }
        if (query.AfterEventId is long afterEventId)
        {
            conditions.Add("e.event_id > $after_event_id");
            command.Parameters.AddWithValue("$after_event_id", afterEventId);
        }
        if (query.ThroughEventId is long throughEventId)
        {
            conditions.Add("e.event_id <= $through_event_id");
            command.Parameters.AddWithValue("$through_event_id", throughEventId);
        }

        string where = conditions.Count == 0
            ? string.Empty
            : $"WHERE {string.Join(" AND ", conditions)}";
        string direction = query.Ascending ? "ASC" : "DESC";
        string order = $"e.event_id {direction}";
        command.CommandText =
            $$"""
            SELECT
                e.event_id,
                e.event_schema_version,
                e.stream_sequence,
                e.event_type,
                e.target_gpu_uuid,
                e.observed_at_unix_ms,
                e.stored_at_unix_ms,
                e.event_json,
                r.run_id,
                r.event_schema_version AS run_event_schema_version,
                r.started_at_unix_ms,
                r.completed_at_unix_ms,
                r.completion_reason,
                r.target_gpu_uuid AS run_target_gpu_uuid,
                r.interval_ms,
                r.buffer_capacity,
                r.alert_threshold_c,
                r.alert_hysteresis_c,
                r.retention_days,
                r.application_version,
                r.os_description,
                r.os_architecture,
                r.process_architecture,
                s.snapshot_id,
                s.observed_at_unix_ms AS snapshot_observed_at_unix_ms,
                s.gpu_index,
                s.gpu_name,
                s.gpu_uuid,
                s.driver_version,
                s.nvml_version,
                s.board_capture_state,
                s.board_capture_error,
                s.board_flags,
                s.pci_vendor_id,
                s.pci_device_id,
                s.pci_subsystem_vendor_id,
                s.pci_subsystem_device_id,
                s.pci_domain,
                s.pci_bus,
                s.pci_device,
                s.pci_function,
                s.pci_bus_id,
                s.vbios_version,
                s.profile_key
            FROM telemetry_events AS e
            INNER JOIN monitor_runs AS r ON r.run_id = e.run_id
            LEFT JOIN gpu_snapshots AS s
                ON s.snapshot_id = e.gpu_snapshot_id AND s.run_id = e.run_id
            {{where}}
            ORDER BY {{order}}
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", query.Limit);

        using SqliteDataReader reader = command.ExecuteReader();
        var evidence = new List<StoredTelemetryEvidence>();
        while (reader.Read())
        {
            evidence.Add(ReadEvidence(reader));
        }

        return evidence;
    }

    private static StoredTelemetryEvidence ReadEvidence(SqliteDataReader reader)
    {
        var run = new MonitoringRunEvidence(
            reader.GetString(reader.GetOrdinal("run_id")),
            reader.GetInt32(reader.GetOrdinal("run_event_schema_version")),
            FromUnixMilliseconds(reader.GetInt64(reader.GetOrdinal("started_at_unix_ms"))),
            GetNullableInt64(reader, "completed_at_unix_ms") is long completedAt
                ? FromUnixMilliseconds(completedAt)
                : null,
            GetNullableString(reader, "completion_reason"),
            reader.GetString(reader.GetOrdinal("run_target_gpu_uuid")),
            reader.GetInt32(reader.GetOrdinal("interval_ms")),
            reader.GetInt32(reader.GetOrdinal("buffer_capacity")),
            GetNullableInt32(reader, "alert_threshold_c"),
            reader.GetInt32(reader.GetOrdinal("alert_hysteresis_c")),
            reader.GetDouble(reader.GetOrdinal("retention_days")),
            reader.GetString(reader.GetOrdinal("application_version")),
            reader.GetString(reader.GetOrdinal("os_description")),
            reader.GetString(reader.GetOrdinal("os_architecture")),
            reader.GetString(reader.GetOrdinal("process_architecture")));

        StoredGpuEvidenceSnapshot? snapshot = null;
        if (GetNullableInt64(reader, "snapshot_id") is long snapshotId)
        {
            uint gpuIndex = checked((uint)reader.GetInt64(reader.GetOrdinal("gpu_index")));
            var gpu = new GpuInfo(
                gpuIndex,
                reader.GetString(reader.GetOrdinal("gpu_name")),
                reader.GetString(reader.GetOrdinal("gpu_uuid")),
                reader.GetString(reader.GetOrdinal("driver_version")),
                reader.GetString(reader.GetOrdinal("nvml_version")));

            BoardIdentity? board = null;
            if (GetNullableInt64(reader, "board_flags") is long boardFlags)
            {
                board = new BoardIdentity(
                    gpuIndex,
                    GetNullableUInt32(reader, "pci_vendor_id"),
                    GetNullableUInt32(reader, "pci_device_id"),
                    GetNullableUInt32(reader, "pci_subsystem_vendor_id"),
                    GetNullableUInt32(reader, "pci_subsystem_device_id"),
                    GetNullableUInt32(reader, "pci_domain"),
                    GetNullableUInt32(reader, "pci_bus"),
                    GetNullableUInt32(reader, "pci_device"),
                    GetNullableUInt32(reader, "pci_function"),
                    (BoardIdentityFlags)checked((uint)boardFlags),
                    GetNullableString(reader, "pci_bus_id") ?? string.Empty,
                    GetNullableString(reader, "vbios_version") ?? string.Empty);
            }

            snapshot = new StoredGpuEvidenceSnapshot(
                snapshotId,
                gpu,
                board,
                ParseBoardState(reader.GetString(reader.GetOrdinal("board_capture_state"))),
                GetNullableString(reader, "board_capture_error"),
                GetNullableString(reader, "profile_key"),
                FromUnixMilliseconds(
                    reader.GetInt64(reader.GetOrdinal("snapshot_observed_at_unix_ms"))));
        }

        string eventType = reader.GetString(reader.GetOrdinal("event_type"));
        return new StoredTelemetryEvidence(
            reader.GetInt64(reader.GetOrdinal("event_id")),
            CurrentSchemaVersion,
            reader.GetInt32(reader.GetOrdinal("event_schema_version")),
            checked((ulong)reader.GetInt64(reader.GetOrdinal("stream_sequence"))),
            ParseEventKind(eventType),
            eventType,
            reader.GetString(reader.GetOrdinal("target_gpu_uuid")),
            FromUnixMilliseconds(reader.GetInt64(reader.GetOrdinal("observed_at_unix_ms"))),
            FromUnixMilliseconds(reader.GetInt64(reader.GetOrdinal("stored_at_unix_ms"))),
            run,
            snapshot,
            reader.GetString(reader.GetOrdinal("event_json")));
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(connectionString)
        {
            DefaultTimeout = options.BusyTimeoutSeconds,
        };
        try
        {
            connection.Open();

            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                $"PRAGMA foreign_keys = ON; PRAGMA busy_timeout = {options.BusyTimeoutSeconds * 1000}; PRAGMA synchronous = NORMAL;";
            command.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            SqliteConnection.ClearPool(connection);
            connection.Dispose();
            throw;
        }
    }

    private void ClearConnectionPool()
    {
        using var connection = new SqliteConnection(connectionString);
        SqliteConnection.ClearPool(connection);
    }

    private static long ReadExistingEventId(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        ulong sequence,
        long? gpuSnapshotId,
        string eventJson)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT event_id, gpu_snapshot_id, event_json
            FROM telemetry_events
            WHERE run_id = $run_id AND stream_sequence = $stream_sequence;
            """;
        command.Parameters.AddWithValue("$run_id", runId);
        command.Parameters.AddWithValue("$stream_sequence", checked((long)sequence));
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new TelemetryStoreException(
                $"O SQLite recusou a sequência {sequence}, mas não retornou o registro existente.");
        }

        long? storedSnapshotId = reader.IsDBNull(1) ? null : reader.GetInt64(1);
        string storedJson = reader.GetString(2);
        if (storedSnapshotId != gpuSnapshotId ||
            !string.Equals(storedJson, eventJson, StringComparison.Ordinal))
        {
            throw new TelemetrySequenceConflictException(runId, sequence);
        }

        return reader.GetInt64(0);
    }

    private static void ValidateEventContext(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        string targetGpuUuid,
        long? gpuSnapshotId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT r.target_gpu_uuid, s.gpu_uuid
            FROM monitor_runs AS r
            LEFT JOIN gpu_snapshots AS s
                ON s.run_id = r.run_id AND s.snapshot_id = $gpu_snapshot_id
            WHERE r.run_id = $run_id;
            """;
        command.Parameters.AddWithValue("$run_id", runId);
        AddNullable(command, "$gpu_snapshot_id", gpuSnapshotId);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new TelemetryStoreException($"A sessão {runId} não existe.");
        }

        string runTarget = reader.GetString(0);
        if (!string.Equals(runTarget, targetGpuUuid, StringComparison.OrdinalIgnoreCase))
        {
            throw new TelemetryStoreException(
                $"O evento pertence à GPU {targetGpuUuid}, mas o run {runId} pertence à GPU {runTarget}.");
        }
        if (gpuSnapshotId is not null)
        {
            if (reader.IsDBNull(1))
            {
                throw new TelemetryStoreException(
                    $"O snapshot {gpuSnapshotId} não pertence ao run {runId}.");
            }

            string snapshotTarget = reader.GetString(1);
            if (!string.Equals(snapshotTarget, targetGpuUuid, StringComparison.OrdinalIgnoreCase))
            {
                throw new TelemetryStoreException(
                    $"O snapshot {gpuSnapshotId} pertence à GPU {snapshotTarget}, não à GPU {targetGpuUuid}.");
            }
        }
    }

    private static bool RunExists(SqliteConnection connection, string runId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM monitor_runs WHERE run_id = $run_id LIMIT 1;";
        command.Parameters.AddWithValue("$run_id", runId);
        return command.ExecuteScalar() is not null;
    }

    private static long ExecuteDelete(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        long? cutoff)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        if (cutoff is long cutoffValue)
        {
            command.Parameters.AddWithValue("$cutoff", cutoffValue);
        }

        return command.ExecuteNonQuery();
    }

    private static void AddBoardParameters(
        SqliteCommand command,
        BoardIdentity? board,
        string? profileKey)
    {
        AddNullable(command, "$board_flags", board is null ? null : (long)(uint)board.Flags);
        AddNullable(command, "$pci_vendor_id", board is null ? null : (long)board.PciVendorId);
        AddNullable(command, "$pci_device_id", board is null ? null : (long)board.PciDeviceId);
        AddNullable(
            command,
            "$pci_subsystem_vendor_id",
            board is null ? null : (long)board.PciSubsystemVendorId);
        AddNullable(
            command,
            "$pci_subsystem_device_id",
            board is null ? null : (long)board.PciSubsystemDeviceId);
        AddNullable(command, "$pci_domain", board is null ? null : (long)board.PciDomain);
        AddNullable(command, "$pci_bus", board is null ? null : (long)board.PciBus);
        AddNullable(command, "$pci_device", board is null ? null : (long)board.PciDevice);
        AddNullable(command, "$pci_function", board is null ? null : (long)board.PciFunction);
        AddNullable(command, "$pci_bus_id", board?.PciBusId);
        AddNullable(
            command,
            "$vbios_version",
            board is { HasVbiosVersion: true } ? board.VbiosVersion : null);
        AddNullable(command, "$profile_key", profileKey);
    }

    private static void AddNullable(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static void ValidateRunOptions(MonitoringRunOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.TargetGpuUuid);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ApplicationVersion);
        if (options.IntervalMilliseconds is < 100 or > 60000)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Intervalo inválido.");
        }
        if (options.BufferCapacity is < 1 or > 65536)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Capacidade do buffer inválida.");
        }
        if (options.AlertThresholdC is < 0 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Limiar de alerta inválido.");
        }
        if (options.AlertHysteresisC < 0 ||
            options.AlertHysteresisC > (options.AlertThresholdC ?? 0))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Histerese de alerta inválida.");
        }
    }

    private static void ValidateSnapshot(GpuEvidenceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshot.Gpu.Uuid);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshot.Gpu.Name);
        if (snapshot.BoardState == BoardEvidenceState.Available && snapshot.Board is null)
        {
            throw new ArgumentException(
                "Um snapshot com board disponível deve conter BoardIdentity.",
                nameof(snapshot));
        }
        if (snapshot.BoardState != BoardEvidenceState.Available && snapshot.Board is not null)
        {
            throw new ArgumentException(
                "BoardIdentity só pode existir quando a captura estiver disponível.",
                nameof(snapshot));
        }
        if (snapshot.Board is not null && snapshot.Board.GpuIndex != snapshot.Gpu.Index)
        {
            throw new ArgumentException(
                "A identidade da placa e a GPU usam índices diferentes.",
                nameof(snapshot));
        }
        if (snapshot.BoardState == BoardEvidenceState.QueryFailed &&
            string.IsNullOrWhiteSpace(snapshot.BoardError))
        {
            throw new ArgumentException(
                "Uma falha de captura deve preservar o diagnóstico.",
                nameof(snapshot));
        }
    }

    private static void ValidateEvent(TelemetryEvent telemetryEvent)
    {
        ArgumentNullException.ThrowIfNull(telemetryEvent);
        ArgumentException.ThrowIfNullOrWhiteSpace(telemetryEvent.TargetGpuUuid);
        if (telemetryEvent.Sequence == 0 || telemetryEvent.Sequence > long.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(telemetryEvent),
                "A sequência deve estar entre 1 e Int64.MaxValue.");
        }
        if (telemetryEvent.ObservedAtUnixMilliseconds > long.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(telemetryEvent),
                "O timestamp excede o intervalo suportado pelo SQLite.");
        }
        if (telemetryEvent.Sample is TemperatureSample sample &&
            sample.TimestampUnixMilliseconds > long.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(telemetryEvent),
                "O timestamp da amostra excede o intervalo suportado pelo SQLite.");
        }
        bool mustHaveSample = telemetryEvent.Kind is
            TelemetryEventKind.Sample or
            TelemetryEventKind.AlertRaised or
            TelemetryEventKind.AlertCleared;
        if (mustHaveSample != (telemetryEvent.Sample is not null))
        {
            throw new ArgumentException(
                $"O evento {telemetryEvent.KindName} possui uma combinação inválida de amostra.",
                nameof(telemetryEvent));
        }
        if (telemetryEvent.Gpu is GpuInfo gpu &&
            !string.Equals(gpu.Uuid, telemetryEvent.TargetGpuUuid, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "A GPU do evento não corresponde ao UUID alvo.",
                nameof(telemetryEvent));
        }
        if (telemetryEvent.Sample is TemperatureSample indexedSample &&
            telemetryEvent.Gpu is GpuInfo indexedGpu &&
            indexedSample.GpuIndex != indexedGpu.Index)
        {
            throw new ArgumentException(
                "A amostra e a GPU do evento usam índices diferentes.",
                nameof(telemetryEvent));
        }
    }

    private static void ValidateQuery(TelemetryEventQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Limit is < 1 or > 10000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(query),
                "O limite da consulta deve estar entre 1 e 10000.");
        }
        if (query.FromUnixMilliseconds < 0 || query.ToUnixMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(query),
                "Os timestamps da consulta não podem ser negativos.");
        }
        if (query.FromUnixMilliseconds > query.ToUnixMilliseconds)
        {
            throw new ArgumentException(
                "O início da consulta não pode ser posterior ao fim.",
                nameof(query));
        }
        if (query.AfterSequence is not null && query.RunId is null)
        {
            throw new ArgumentException(
                "Filtrar por sequência exige um run_id.",
                nameof(query));
        }
        if (query.AfterSequence > long.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(query),
                "A sequência excede Int64.MaxValue.");
        }
        if (query.AfterEventId < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(query),
                "O event_id não pode ser negativo.");
        }
        if (query.ThroughEventId < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(query),
                "O limite superior de event_id não pode ser negativo.");
        }
        if (query.AfterEventId >= query.ThroughEventId)
        {
            throw new ArgumentException(
                "O cursor de event_id deve ser menor que o limite superior.",
                nameof(query));
        }
    }

    private static void ValidateRunId(string runId) =>
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

    private static string EventKindName(TelemetryEventKind kind) => kind switch
    {
        TelemetryEventKind.Sample => "sample",
        TelemetryEventKind.Gap => "gap",
        TelemetryEventKind.Recovered => "recovered",
        TelemetryEventKind.AlertRaised => "alert_raised",
        TelemetryEventKind.AlertCleared => "alert_cleared",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Tipo de evento desconhecido."),
    };

    private static TelemetryEventKind ParseEventKind(string value) => value switch
    {
        "sample" => TelemetryEventKind.Sample,
        "gap" => TelemetryEventKind.Gap,
        "recovered" => TelemetryEventKind.Recovered,
        "alert_raised" => TelemetryEventKind.AlertRaised,
        "alert_cleared" => TelemetryEventKind.AlertCleared,
        _ => throw new TelemetryStoreException($"Tipo de evento desconhecido no banco: {value}"),
    };

    private static BoardEvidenceState ParseBoardState(string value) => value switch
    {
        "not_attempted" => BoardEvidenceState.NotAttempted,
        "available" => BoardEvidenceState.Available,
        "query_failed" => BoardEvidenceState.QueryFailed,
        _ => throw new TelemetryStoreException($"Estado de captura desconhecido no banco: {value}"),
    };

    private static DateTimeOffset FromUnixMilliseconds(long value) =>
        DateTimeOffset.FromUnixTimeMilliseconds(value);

    private static string? GetNullableString(SqliteDataReader reader, string column)
    {
        int ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static int? GetNullableInt32(SqliteDataReader reader, string column)
    {
        int ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
    }

    private static long? GetNullableInt64(SqliteDataReader reader, string column)
    {
        int ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
    }

    private static uint GetNullableUInt32(SqliteDataReader reader, string column) =>
        checked((uint)(GetNullableInt64(reader, column) ?? 0));

    private T ExecuteWithStoreErrors<T>(string operation, Func<T> action)
    {
        try
        {
            return action();
        }
        catch (TelemetryStoreException)
        {
            throw;
        }
        catch (Exception error) when (error is SqliteException or IOException or UnauthorizedAccessException)
        {
            throw new TelemetryStoreException(
                $"Não foi possível {operation} em '{options.DatabasePath}': {error.Message}",
                error);
        }
    }

    private void ExecuteWithStoreErrors(string operation, Action action) =>
        ExecuteWithStoreErrors(
            operation,
            () =>
            {
                action();
                return true;
            });
}
