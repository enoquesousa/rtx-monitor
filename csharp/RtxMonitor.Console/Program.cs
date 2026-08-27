using System.Globalization;
using System.Text;
using System.Text.Json;
using RtxMonitor.Managed;
using RtxMonitor.Storage;

namespace RtxMonitor.ConsoleApp;

internal enum RunMode
{
    Watch,
    Once,
    List,
    Capabilities,
    Telemetry,
    ThermalWatch,
    History,
    Export,
}

internal sealed record Options(
    RunMode Mode,
    uint GpuIndex,
    string? GpuUuid,
    int IntervalMilliseconds,
    long Count,
    int BufferCapacity,
    bool Json,
    bool Events,
    int? AlertThresholdC,
    int AlertHysteresisC,
    string? DatabasePath,
    int RetentionDays,
    string? RunId,
    TelemetryEventKind? EventKind,
    long? FromUnixMilliseconds,
    long? ToUnixMilliseconds,
    ulong? AfterSequence,
    int QueryLimit);

internal sealed class RunningStatistics
{
    private long sum;

    internal long Count { get; private set; }

    internal int Minimum { get; private set; } = int.MaxValue;

    internal int Maximum { get; private set; } = int.MinValue;

    internal double Average => Count == 0 ? 0 : (double)sum / Count;

    internal void Add(int value)
    {
        Count++;
        sum += value;
        Minimum = Math.Min(Minimum, value);
        Maximum = Math.Max(Maximum, value);
    }
}

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
            Options options = ParseOptions(args);

            if (options.Mode == RunMode.History)
            {
                return PrintHistory(options);
            }
            if (options.Mode == RunMode.Export)
            {
                return ExportHistory(options);
            }

            if (options.Mode == RunMode.Watch)
            {
                GpuInfo? target = null;
                string targetUuid;
                if (options.GpuUuid is not null)
                {
                    targetUuid = options.GpuUuid;
                }
                else
                {
                    using NvidiaMonitor initialMonitor = NvidiaMonitor.Open();
                    target = ResolveGpu(initialMonitor, options);
                    targetUuid = target.Uuid;
                }

                return await WatchAsync(targetUuid, target, options).ConfigureAwait(false);
            }

            if (options.Mode == RunMode.ThermalWatch)
            {
                using NvidiaMonitor thermalMonitor = NvidiaMonitor.Open();
                GpuInfo thermalGpu = ResolveGpu(thermalMonitor, options);
                return await ThermalWatchAsync(thermalMonitor, thermalGpu, options).ConfigureAwait(false);
            }

            using NvidiaMonitor monitor = NvidiaMonitor.Open();

            if (options.Mode == RunMode.List)
            {
                PrintGpuList(monitor.GetGpus(), options.Json);
                return 0;
            }

            GpuInfo gpu = ResolveGpu(monitor, options);
            if (options.Mode == RunMode.Capabilities)
            {
                BoardIdentity board = monitor.GetBoardIdentity(gpu.Index);
                ThermalReport report = monitor.ScanThermalCapabilities(gpu.Index);
                PrintCapabilities(gpu, board, report, options.Json);
                return 0;
            }

            if (options.Mode == RunMode.Telemetry)
            {
                BoardIdentity board = monitor.GetBoardIdentity(gpu.Index);
                PublicTelemetryReport report = monitor.ReadPublicTelemetry(gpu.Index);
                using var metrics = new ComputedMetricsEngine();
                ComputedMetricsReport computed = metrics.Observe(report);
                PrintPublicTelemetry(gpu, board, report, computed, options.Json);
                return 0;
            }

            if (options.Mode == RunMode.Once)
            {
                TemperatureSample sample = monitor.ReadGpuDieTemperature(gpu.Index);
                PrintSample(gpu, sample, options.Json);
                return 0;
            }

            throw new InvalidOperationException("Modo de execução não tratado.");
        }
        catch (ArgumentException error)
        {
            Console.Error.WriteLine($"rtxmon-csharp: {error.Message}");
            return 2;
        }
        catch (DllNotFoundException error)
        {
            Console.Error.WriteLine(
                "rtxmon-csharp: rtxmon_native.dll não foi encontrada. Execute scripts/build.ps1 primeiro.");
            Console.Error.WriteLine(error.Message);
            return 1;
        }
        catch (BadImageFormatException error)
        {
            Console.Error.WriteLine(
                "rtxmon-csharp: a arquitetura da DLL nativa não corresponde ao processo .NET (use x64).");
            Console.Error.WriteLine(error.Message);
            return 1;
        }
        catch (RtxMonitorException error)
        {
            Console.Error.WriteLine($"rtxmon-csharp: {error.Message}");
            return 1;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"rtxmon-csharp: {error.Message}");
            return 1;
        }
    }

    private static Options ParseOptions(string[] args)
    {
        RunMode mode = RunMode.Watch;
        uint gpuIndex = 0;
        bool gpuIndexSet = false;
        string? gpuUuid = null;
        int interval = 1000;
        long count = 0;
        int bufferCapacity = 256;
        bool json = false;
        bool events = false;
        int? alertThresholdC = null;
        int? alertHysteresisC = null;
        string? databasePath = null;
        int retentionDays = 30;
        string? runId = null;
        TelemetryEventKind? eventKind = null;
        long? fromUnixMilliseconds = null;
        long? toUnixMilliseconds = null;
        ulong? afterSequence = null;
        int queryLimit = 100;
        bool intervalSet = false;
        bool countSet = false;
        bool bufferSet = false;
        bool retentionSet = false;
        bool queryFilterSet = false;
        bool queryLimitSet = false;

        for (int index = 0; index < args.Length; index++)
        {
            string argument = args[index];
            switch (argument)
            {
                case "--help":
                case "-h":
                    PrintHelp();
                    Environment.Exit(0);
                    break;
                case "--once":
                    mode = RunMode.Once;
                    break;
                case "--watch":
                    mode = RunMode.Watch;
                    break;
                case "--list":
                    mode = RunMode.List;
                    break;
                case "--capabilities":
                    mode = RunMode.Capabilities;
                    break;
                case "--telemetry":
                    mode = RunMode.Telemetry;
                    break;
                case "--thermal-watch":
                    mode = RunMode.ThermalWatch;
                    break;
                case "--history":
                    mode = RunMode.History;
                    break;
                case "--export":
                    mode = RunMode.Export;
                    break;
                case "--json":
                    json = true;
                    break;
                case "--events":
                    events = true;
                    break;
                case "--gpu":
                    gpuIndex = ParseUInt32(NextValue(args, ref index, argument), argument);
                    gpuIndexSet = true;
                    break;
                case "--gpu-uuid":
                    gpuUuid = NextValue(args, ref index, argument);
                    if (string.IsNullOrWhiteSpace(gpuUuid))
                    {
                        throw new ArgumentException("--gpu-uuid não pode estar vazio");
                    }
                    break;
                case "--interval":
                    interval = ParseInt32(NextValue(args, ref index, argument), argument);
                    intervalSet = true;
                    break;
                case "--count":
                    count = ParseInt64(NextValue(args, ref index, argument), argument);
                    countSet = true;
                    break;
                case "--buffer":
                    bufferCapacity = ParseInt32(NextValue(args, ref index, argument), argument);
                    bufferSet = true;
                    break;
                case "--alert-threshold":
                    alertThresholdC = ParseInt32(NextValue(args, ref index, argument), argument);
                    break;
                case "--alert-hysteresis":
                    alertHysteresisC = ParseInt32(NextValue(args, ref index, argument), argument);
                    break;
                case "--database":
                    databasePath = NextValue(args, ref index, argument);
                    if (string.IsNullOrWhiteSpace(databasePath))
                    {
                        throw new ArgumentException("--database não pode estar vazio");
                    }
                    break;
                case "--retention-days":
                    retentionDays = ParseInt32(NextValue(args, ref index, argument), argument);
                    retentionSet = true;
                    break;
                case "--run-id":
                    runId = NextValue(args, ref index, argument);
                    if (string.IsNullOrWhiteSpace(runId))
                    {
                        throw new ArgumentException("--run-id não pode estar vazio");
                    }
                    queryFilterSet = true;
                    break;
                case "--event-type":
                    eventKind = ParseEventKind(NextValue(args, ref index, argument));
                    queryFilterSet = true;
                    break;
                case "--from-unix-ms":
                    fromUnixMilliseconds = ParseInt64(
                        NextValue(args, ref index, argument),
                        argument);
                    queryFilterSet = true;
                    break;
                case "--to-unix-ms":
                    toUnixMilliseconds = ParseInt64(
                        NextValue(args, ref index, argument),
                        argument);
                    queryFilterSet = true;
                    break;
                case "--after-sequence":
                    afterSequence = ParseUInt64(
                        NextValue(args, ref index, argument),
                        argument);
                    queryFilterSet = true;
                    break;
                case "--limit":
                    queryLimit = ParseInt32(NextValue(args, ref index, argument), argument);
                    queryLimitSet = true;
                    break;
                default:
                    throw new ArgumentException($"opção desconhecida: {argument}; use --help");
            }
        }

        if (interval is < 100 or > 60000)
        {
            throw new ArgumentException("--interval deve estar entre 100 e 60000 ms");
        }

        if (count < 0)
        {
            throw new ArgumentException("--count não pode ser negativo");
        }
        if (bufferCapacity is < 1 or > 65536)
        {
            throw new ArgumentException("--buffer deve estar entre 1 e 65536 eventos");
        }
        if (gpuIndexSet && gpuUuid is not null)
        {
            throw new ArgumentException("--gpu e --gpu-uuid são mutuamente exclusivos");
        }
        if (events && mode != RunMode.Watch)
        {
            throw new ArgumentException("--events exige --watch");
        }
        if (alertThresholdC is not null && mode != RunMode.Watch)
        {
            throw new ArgumentException("--alert-threshold exige --watch");
        }
        if (alertThresholdC is null && alertHysteresisC is not null)
        {
            throw new ArgumentException("--alert-hysteresis exige --alert-threshold");
        }
        if (alertThresholdC is < 0 or > 500)
        {
            throw new ArgumentException("--alert-threshold deve estar entre 0 e 500 °C");
        }
        if (alertHysteresisC < 0 || alertHysteresisC > (alertThresholdC ?? 0))
        {
            throw new ArgumentException("--alert-hysteresis deve estar entre 0 e o limiar");
        }
        if (retentionDays is < 1 or > 3650)
        {
            throw new ArgumentException("--retention-days deve estar entre 1 e 3650");
        }
        if (queryLimit is < 1 or > 10000)
        {
            throw new ArgumentException("--limit deve estar entre 1 e 10000");
        }
        if (fromUnixMilliseconds < 0 || toUnixMilliseconds < 0)
        {
            throw new ArgumentException("os timestamps da consulta não podem ser negativos");
        }
        if (fromUnixMilliseconds > toUnixMilliseconds)
        {
            throw new ArgumentException("--from-unix-ms não pode ser posterior a --to-unix-ms");
        }

        bool queryMode = mode is RunMode.History or RunMode.Export;
        if (queryMode && databasePath is null)
        {
            throw new ArgumentException("--history e --export exigem --database PATH");
        }
        if (databasePath is not null &&
            mode is not (RunMode.Watch or RunMode.History or RunMode.Export))
        {
            throw new ArgumentException("--database só pode ser usado com --watch, --history ou --export");
        }
        if (retentionSet && (mode != RunMode.Watch || databasePath is null))
        {
            throw new ArgumentException("--retention-days exige --watch --database PATH");
        }
        if ((queryFilterSet || queryLimitSet) && !queryMode)
        {
            throw new ArgumentException(
                "--run-id, --event-type, filtros de tempo, --after-sequence e --limit exigem --history ou --export");
        }
        if (queryMode && gpuIndexSet)
        {
            throw new ArgumentException("consultas históricas usam --gpu-uuid, não --gpu");
        }
        if (queryMode && (events || alertThresholdC is not null || intervalSet || countSet || bufferSet))
        {
            throw new ArgumentException(
                "opções de coleta não podem ser usadas com --history ou --export");
        }
        if (mode == RunMode.Export && queryLimitSet)
        {
            throw new ArgumentException("--export percorre todo o recorte; --limit é exclusivo de --history");
        }
        if (afterSequence is not null && runId is null)
        {
            throw new ArgumentException("--after-sequence exige --run-id");
        }

        return new Options(
            mode,
            gpuIndex,
            gpuUuid,
            interval,
            count,
            bufferCapacity,
            json,
            events,
            alertThresholdC,
            alertHysteresisC ?? 0,
            databasePath,
            retentionDays,
            runId,
            eventKind,
            fromUnixMilliseconds,
            toUnixMilliseconds,
            afterSequence,
            queryLimit);
    }

    private static async Task<int> ThermalWatchAsync(
        NvidiaMonitor monitor,
        GpuInfo gpu,
        Options options)
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += handler;
        try
        {
            long emitted = 0;
            while (!cancellation.IsCancellationRequested &&
                   (options.Count == 0 || emitted < options.Count))
            {
                PrivateThermalSample sample = monitor.ReadPrivateThermalChannels(gpu.Index);
                if (options.Json)
                {
                    Console.WriteLine(JsonSerializer.Serialize(new
                    {
                        gpu_index = sample.GpuIndex,
                        gpu_uuid = gpu.Uuid,
                        captured_at_unix_ms = sample.TimestampUnixMilliseconds,
                        gpu_die_temperature_c = sample.GpuDieTemperatureC,
                        gpu_hotspot_temperature_c = sample.GpuHotspotTemperatureC,
                        delta_c = Math.Round(sample.DeltaC, 3),
                        source = PrivateThermalSample.Source,
                    }));
                }
                else
                {
                    Console.WriteLine(
                        $"{sample.CapturedAt:yyyy-MM-dd HH:mm:ss.fff zzz} | " +
                        $"GPU Die {sample.GpuDieTemperatureC:F2} °C | " +
                        $"Hotspot {sample.GpuHotspotTemperatureC:F2} °C | " +
                        $"Delta {sample.DeltaC:F2} °C | {PrivateThermalSample.Source}");
                }
                emitted++;
                if (options.Count == 0 || emitted < options.Count)
                {
                    await Task.Delay(options.IntervalMilliseconds, cancellation.Token)
                        .ConfigureAwait(false);
                }
            }
            return 0;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return 0;
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }
    }

    private static string NextValue(string[] args, ref int index, string option)
    {
        if (++index >= args.Length)
        {
            throw new ArgumentException($"valor ausente para {option}");
        }

        return args[index];
    }

    private static uint ParseUInt32(string value, string option) =>
        uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out uint parsed)
            ? parsed
            : throw new ArgumentException($"valor inválido para {option}: {value}");

    private static int ParseInt32(string value, string option) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : throw new ArgumentException($"valor inválido para {option}: {value}");

    private static long ParseInt64(string value, string option) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long parsed)
            ? parsed
            : throw new ArgumentException($"valor inválido para {option}: {value}");

    private static ulong ParseUInt64(string value, string option) =>
        ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out ulong parsed)
            ? parsed
            : throw new ArgumentException($"valor inválido para {option}: {value}");

    private static TelemetryEventKind ParseEventKind(string value) => value switch
    {
        "sample" => TelemetryEventKind.Sample,
        "gap" => TelemetryEventKind.Gap,
        "recovered" => TelemetryEventKind.Recovered,
        "alert_raised" => TelemetryEventKind.AlertRaised,
        "alert_cleared" => TelemetryEventKind.AlertCleared,
        _ => throw new ArgumentException(
            $"valor inválido para --event-type: {value}; use sample, gap, recovered, alert_raised ou alert_cleared"),
    };

    private static void PrintHelp()
    {
        Console.WriteLine(
            """
            RtxMonitor.Console - monitor do sensor térmico do die NVIDIA

            Uso:
              dotnet RtxMonitor.Console.dll [--watch] [--gpu INDEX | --gpu-uuid UUID] [--interval MS]
              dotnet RtxMonitor.Console.dll --watch --events [--gpu INDEX | --gpu-uuid UUID]
              dotnet RtxMonitor.Console.dll --watch --alert-threshold C [--alert-hysteresis C]
              dotnet RtxMonitor.Console.dll --once [--gpu INDEX | --gpu-uuid UUID] [--json]
              dotnet RtxMonitor.Console.dll --list [--json]
              dotnet RtxMonitor.Console.dll --capabilities [--gpu INDEX | --gpu-uuid UUID] [--json]
              dotnet RtxMonitor.Console.dll --telemetry [--gpu INDEX | --gpu-uuid UUID] [--json]
              dotnet RtxMonitor.Console.dll --thermal-watch [--gpu INDEX | --gpu-uuid UUID] [--interval MS] [--count N] [--json]
              dotnet RtxMonitor.Console.dll --history --database PATH [filtros] [--json]
              dotnet RtxMonitor.Console.dll --export --database PATH [filtros]

            Opções:
              --watch         Monitor contínuo (padrão)
              --once          Lê uma amostra e encerra
              --list          Lista as GPUs NVIDIA
              --capabilities Inventaria capabilities térmicas públicas e o estado das fontes
              --telemetry    Lê o catálogo público documentado e as métricas calculadas
              --thermal-watch Lê die e hotspot diretamente da NVAPI, sem GPU-Z
              --gpu INDEX     Índice da GPU, começando em zero
              --gpu-uuid UUID Seleciona a GPU por UUID persistente; não use junto com --gpu
              --interval MS   Intervalo de 100 a 60000 ms (padrão: 1000)
              --count N       Encerra após N amostras; zero é ilimitado
              --buffer N      Retém de 1 a 65536 eventos recentes (padrão: 256)
              --json          JSON; em watch, mantém o schema de amostra v1
              --events        Emite o stream completo de eventos (schema v4) como JSON Lines
              --alert-threshold C   Dispara um alerta durante --watch ao atingir C °C (0-500)
              --alert-hysteresis C  Limpa em limiar-C; com 0, somente abaixo do limiar
              --database PATH Persiste --watch em SQLite ou seleciona o banco de uma consulta
              --retention-days N  Retém de 1 a 3650 dias (padrão: 30; exige --watch --database)
              --history       Consulta até --limit eventos; --json emite evidence records JSON Lines
              --export        Exporta todo o recorte como evidence records JSON Lines no stdout
              --run-id ID     Filtra uma sessão de monitoramento
              --event-type T  Filtra sample, gap, recovered, alert_raised ou alert_cleared
              --from-unix-ms N  Inclui eventos observados a partir do timestamp
              --to-unix-ms N    Inclui eventos observados até o timestamp
              --after-sequence N  Exclui sequências até N; exige --run-id
              --limit N       Limite de 1 a 10000 no modo --history (padrão: 100)
              --help          Mostra esta ajuda
            """);
    }

    private static int PrintHistory(Options options)
    {
        SqliteTelemetryStore store = OpenExistingStore(options);
        IReadOnlyList<StoredTelemetryEvidence> records = store.QueryEvents(
            BuildHistoryQuery(options, options.QueryLimit, ascending: false));

        foreach (StoredTelemetryEvidence record in records)
        {
            if (options.Json)
            {
                Console.WriteLine(EvidenceJson.Serialize(record));
            }
            else
            {
                PrintHistoryRecord(record);
            }
        }

        if (records.Count == 0 && !options.Json)
        {
            Console.WriteLine("Nenhum evento corresponde aos filtros informados.");
        }

        return 0;
    }

    private static int ExportHistory(Options options)
    {
        const int pageSize = 1000;
        SqliteTelemetryStore store = OpenExistingStore(options);
        long? maximumEventId = store.GetMaximumEventId();
        if (maximumEventId is null)
        {
            return 0;
        }

        long? afterEventId = null;
        while (afterEventId is null || afterEventId < maximumEventId)
        {
            TelemetryEventQuery query = BuildHistoryQuery(
                options,
                pageSize,
                ascending: true) with
            {
                AfterEventId = afterEventId,
                ThroughEventId = maximumEventId,
            };
            IReadOnlyList<StoredTelemetryEvidence> records = store.QueryEvents(query);
            if (records.Count == 0)
            {
                break;
            }

            foreach (StoredTelemetryEvidence record in records)
            {
                Console.WriteLine(EvidenceJson.Serialize(record));
            }

            afterEventId = records[^1].EventId;
        }

        return 0;
    }

    private static SqliteTelemetryStore OpenExistingStore(Options options) =>
        SqliteTelemetryStore.Open(
            new TelemetryStoreOptions(
                options.DatabasePath!,
                openMode: TelemetryStoreOpenMode.OpenExisting));

    private static TelemetryEventQuery BuildHistoryQuery(
        Options options,
        int limit,
        bool ascending) =>
        new(
            RunId: options.RunId,
            TargetGpuUuid: options.GpuUuid,
            EventKind: options.EventKind,
            FromUnixMilliseconds: options.FromUnixMilliseconds,
            ToUnixMilliseconds: options.ToUnixMilliseconds,
            AfterSequence: options.AfterSequence,
            Limit: limit,
            Ascending: ascending);

    private static void PrintHistoryRecord(StoredTelemetryEvidence record)
    {
        using JsonDocument document = JsonDocument.Parse(record.EventJson);
        JsonElement root = document.RootElement;
        string value = root.GetProperty("sample").ValueKind == JsonValueKind.Object
            ? $"{root.GetProperty("sample").GetProperty("temperature_c").GetInt32()} °C"
            : root.GetProperty("status").GetString() ?? "estado desconhecido";
        string profile = record.DeviceSnapshot?.ProfileKey ?? "perfil indisponível";
        Console.WriteLine(
            $"{record.EventId,8} | {record.ObservedAt:O} | {record.EventKindName,-13} | " +
            $"run {record.Run.RunId} seq {record.StreamSequence} | {value} | {profile}");
    }

    private static void PrintGpuList(IReadOnlyList<GpuInfo> gpus, bool json)
    {
        if (json)
        {
            var payload = gpus.Select(gpu => new
            {
                index = gpu.Index,
                name = gpu.Name,
                uuid = gpu.Uuid,
                driver_version = gpu.DriverVersion,
                nvml_version = gpu.NvmlVersion,
            });
            Console.WriteLine(JsonSerializer.Serialize(payload));
            return;
        }

        foreach (GpuInfo gpu in gpus)
        {
            Console.WriteLine(
                $"[{gpu.Index}] {gpu.Name} | {gpu.Uuid} | driver {gpu.DriverVersion} | NVML {gpu.NvmlVersion}");
        }
    }

    private static GpuInfo ResolveGpu(NvidiaMonitor monitor, Options options) =>
        options.GpuUuid is null
            ? monitor.GetGpu(options.GpuIndex)
            : monitor.GetGpuByUuid(options.GpuUuid);

    private static void PrintSample(GpuInfo gpu, TemperatureSample sample, bool json)
    {
        if (json)
        {
            var payload = new
            {
                schema_version = 1,
                gpu_index = sample.GpuIndex,
                gpu_name = gpu.Name,
                gpu_uuid = gpu.Uuid,
                temperature_c = sample.TemperatureC,
                sensor = "gpu_die",
                backend = sample.BackendName,
                timestamp_unix_ms = sample.TimestampUnixMilliseconds,
            };
            Console.WriteLine(JsonSerializer.Serialize(payload));
            return;
        }

        Console.WriteLine(
            $"{sample.CapturedAt:O} | GPU {sample.GpuIndex} {gpu.Name} | die {sample.TemperatureC} °C | {sample.BackendName}");
    }

    private static void PrintCapabilities(
        GpuInfo gpu,
        BoardIdentity board,
        ThermalReport report,
        bool json)
    {
        if (json)
        {
            var payload = new
            {
                schema_version = 2,
                gpu = new
                {
                    index = gpu.Index,
                    name = gpu.Name,
                    uuid = gpu.Uuid,
                    driver_version = gpu.DriverVersion,
                    nvml_version = gpu.NvmlVersion,
                },
                board = new
                {
                    pci_identity_available = board.HasPciIdentity,
                    pci_bus_id = board.PciBusId,
                    pci_vendor_id = HexId(board.PciVendorId),
                    pci_device_id = HexId(board.PciDeviceId),
                    pci_subsystem_vendor_id = HexId(board.PciSubsystemVendorId),
                    pci_subsystem_device_id = HexId(board.PciSubsystemDeviceId),
                    vbios_available = board.HasVbiosVersion,
                    vbios_version = board.HasVbiosVersion ? board.VbiosVersion : null,
                    profile_key = BoardProfileKey(board),
                },
                captured_at_unix_ms = report.TimestampUnixMilliseconds,
                providers = report.Providers.Select(provider => new
                {
                    provider = provider.ProviderName,
                    state = provider.StateName,
                    native_status = provider.NativeStatus,
                    capability_count = provider.CapabilityCount,
                }),
                thermal_capabilities = report.Capabilities.Select(sensor => new
                {
                    provider = sensor.ProviderName,
                    provider_native_id = sensor.ProviderNativeId,
                    target = sensor.TargetName,
                    controller = sensor.ControllerName,
                    state = sensor.StateName,
                    confidence = sensor.ConfidenceName,
                    current_temperature_c = sensor.CurrentTemperatureC,
                    default_min_temperature_c = sensor.DefaultMinimumTemperatureC,
                    default_max_temperature_c = sensor.DefaultMaximumTemperatureC,
                    native_status = sensor.NativeStatus,
                }),
            };
            Console.WriteLine(JsonSerializer.Serialize(payload));
            return;
        }

        Console.WriteLine($"GPU {gpu.Index}  {gpu.Name}");
        Console.WriteLine($"Driver {gpu.DriverVersion}  NVML {gpu.NvmlVersion}");
        Console.WriteLine(
            $"PCI {board.PciBusId}  {HexId(board.PciVendorId)}:{HexId(board.PciDeviceId)[2..]}  " +
            $"subsystem {HexId(board.PciSubsystemVendorId)}:{HexId(board.PciSubsystemDeviceId)[2..]}");
        Console.WriteLine($"VBIOS {(board.HasVbiosVersion ? board.VbiosVersion : "indisponível")}");
        Console.WriteLine();
        Console.WriteLine("Fontes:");
        foreach (ThermalProviderResult provider in report.Providers)
        {
            Console.WriteLine(
                $"  {provider.ProviderName} | {provider.StateName} | capabilities {provider.CapabilityCount} | " +
                $"status nativo {provider.NativeStatus}");
        }

        Console.WriteLine();
        Console.WriteLine("Capacidades térmicas:");
        foreach (ThermalCapability sensor in report.Capabilities)
        {
            string line =
                $"  {sensor.ProviderName}[{sensor.ProviderNativeId}] | alvo {sensor.TargetName} | " +
                $"controlador {sensor.ControllerName} | {sensor.StateName} | {sensor.ConfidenceName}";
            if (sensor.CurrentTemperatureC is int current)
            {
                line += $" | atual {current} °C";
            }
            if (sensor.DefaultMinimumTemperatureC is int minimum &&
                sensor.DefaultMaximumTemperatureC is int maximum)
            {
                line += $" | padrões do driver {minimum}..{maximum} °C";
            }
            Console.WriteLine($"{line} | status nativo {sensor.NativeStatus}");
        }

        Console.WriteLine();
        Console.WriteLine(
            "Somente canais públicos reportados pelo driver são listados; leituras indisponíveis de hotspot, memória ou VRM não são inferidas.");
    }

    private static void PrintPublicTelemetry(
        GpuInfo gpu,
        BoardIdentity board,
        PublicTelemetryReport report,
        ComputedMetricsReport computed,
        bool json)
    {
        if (json)
        {
            PublicTelemetryCoverage coverage = report.Coverage;
            var payload = new
            {
                schema_version = 2,
                gpu = new
                {
                    index = gpu.Index,
                    name = gpu.Name,
                    uuid = gpu.Uuid,
                    driver_version = gpu.DriverVersion,
                    nvml_version = gpu.NvmlVersion,
                },
                profile_key = BoardProfileKey(board),
                captured_at_unix_ms = report.TimestampUnixMilliseconds,
                coverage = new
                {
                    total = coverage.Total,
                    available = coverage.Available,
                    not_supported = coverage.NotSupported,
                    provider_unavailable = coverage.ProviderUnavailable,
                    query_failed = coverage.QueryFailed,
                },
                performance_limit_reasons = PerformanceLimitReasons.From(report) is { } reasons
                    ? new
                    {
                        raw_bitmask = reasons.RawBitmask,
                        active_reasons = reasons.ActiveReasons,
                        primary_reason = reasons.PrimaryReason,
                    }
                    : null,
                fields = report.Fields.Select(field => new
                {
                    field = field.FieldName,
                    provider = field.ProviderName,
                    provider_native_id = field.ProviderNativeId,
                    state = field.StateName,
                    origin = field.OriginName,
                    value_type = field.ValueTypeName,
                    unit = field.UnitName,
                    value_u64 = field.UnsignedValue,
                    value_i64 = field.SignedValue,
                    value_f64 = field.DoubleValue,
                    native_status = field.NativeStatus,
                    timestamp_unix_ms = field.TimestampUnixMilliseconds,
                }),
                computed_metrics = computed.Metrics.Select(metric => new
                {
                    metric = metric.KindName,
                    state = metric.StateName,
                    origin = metric.OriginName,
                    unit = metric.UnitName,
                    formula = metric.Formula,
                    value = metric.Value,
                    window_ms = metric.WindowMilliseconds,
                    sample_count = metric.SampleCount,
                    temperature_threshold_c = metric.TemperatureThresholdC,
                    inputs = metric.InputNames,
                }),
            };
            Console.WriteLine(JsonSerializer.Serialize(payload));
            return;
        }

        Console.WriteLine($"GPU {gpu.Index}  {gpu.Name}");
        Console.WriteLine($"Perfil {BoardProfileKey(board)}");
        Console.WriteLine($"Capturado {report.CapturedAt:O}");
        Console.WriteLine();
        Console.WriteLine("Campos documentados:");
        foreach (PublicTelemetryValue field in report.Fields)
        {
            string value = field.NumericValue?.ToString(CultureInfo.InvariantCulture) ?? "indisponível";
            Console.WriteLine(
                $"  {field.FieldName} | {field.StateName} | " +
                $"{field.ProviderName}[{field.ProviderNativeId}] | {value} {field.UnitName} | " +
                $"status nativo {field.NativeStatus}");
        }

        Console.WriteLine();
        Console.WriteLine("Métricas calculadas:");
        foreach (ComputedMetric metric in computed.Metrics)
        {
            string value = metric.Value?.ToString("G12", CultureInfo.InvariantCulture) ?? "indisponível";
            Console.WriteLine(
                $"  {metric.KindName} | {metric.StateName} | {value} {metric.UnitName} | " +
                $"janela {metric.WindowMilliseconds} ms | amostras {metric.SampleCount} | {metric.Formula}");
        }
    }

    private static string HexId(uint value) => $"0x{value & 0xffffU:x4}";

    private static string BoardProfileKey(BoardIdentity board) =>
        $"{board.PciVendorId & 0xffffU:x4}:{board.PciDeviceId & 0xffffU:x4}/" +
        $"{board.PciSubsystemVendorId & 0xffffU:x4}:{board.PciSubsystemDeviceId & 0xffffU:x4}@" +
        (board.HasVbiosVersion ? board.VbiosVersion : "unknown");

    private static async Task<int> WatchAsync(
        string targetUuid,
        GpuInfo? initialGpu,
        Options options)
    {
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        using var sampler = new ResilientSampler(
            targetUuid,
            new SamplingOptions(options.BufferCapacity, 250, 5000));
        bool dashboard = !options.Json && !options.Events && !Console.IsOutputRedirected;
        var statistics = new RunningStatistics();
        long samples = 0;
        AlertEvaluator? alertEvaluator = options.AlertThresholdC is int alertThresholdC
            ? new AlertEvaluator(new AlertOptions(alertThresholdC, options.AlertHysteresisC))
            : null;
        ulong streamSequence = 0;
        SqliteTelemetryStore? store = null;
        string? runId = null;
        long? currentSnapshotId = null;
        string? currentSnapshotFingerprint = null;
        string completionReason = "error";
        Exception? monitoringError = null;

        if (options.DatabasePath is not null)
        {
            store = SqliteTelemetryStore.Open(
                new TelemetryStoreOptions(
                    options.DatabasePath,
                    TimeSpan.FromDays(options.RetentionDays)));
            RetentionResult retention = store.ApplyRetention(DateTimeOffset.UtcNow);
            runId = store.StartRun(
                new MonitoringRunOptions(
                    targetUuid,
                    options.IntervalMilliseconds,
                    options.BufferCapacity,
                    options.AlertThresholdC,
                    options.AlertHysteresisC,
                    ApplicationVersion(),
                    DateTimeOffset.UtcNow));
            Console.Error.WriteLine(
                $"rtxmon-csharp: persistência ativa em {store.DatabasePath} | run {runId} | " +
                $"retenção {options.RetentionDays} dia(s) | removidos " +
                $"{retention.EventsDeleted} evento(s)");
        }

        try
        {
            if (dashboard)
            {
                Console.Write("\u001b[2J");
            }

            while (!cancellation.IsCancellationRequested &&
                   (options.Count == 0 || samples < options.Count))
            {
                IReadOnlyList<TelemetryEvent> events = sampler.Poll();
                foreach (TelemetryEvent sampledEvent in events)
                {
                    TelemetryEvent telemetryEvent = sampledEvent with
                    {
                        Sequence = ++streamSequence,
                    };

                    if (store is not null && runId is not null)
                    {
                        if (telemetryEvent.Gpu is GpuInfo observedGpu)
                        {
                            string fingerprint = GpuFingerprint(observedGpu);
                            if (currentSnapshotId is null ||
                                !string.Equals(
                                    fingerprint,
                                    currentSnapshotFingerprint,
                                    StringComparison.Ordinal))
                            {
                                GpuEvidenceSnapshot snapshot = CaptureGpuEvidence(observedGpu);
                                currentSnapshotId = store.RegisterGpuSnapshot(runId, snapshot);
                                currentSnapshotFingerprint = fingerprint;
                            }
                        }

                        store.AppendEvent(runId, telemetryEvent, currentSnapshotId);
                    }

                    if (telemetryEvent.Sample is TemperatureSample sample)
                    {
                        statistics.Add(sample.TemperatureC);
                        samples++;
                    }

                    if (dashboard)
                    {
                        RenderDashboard(
                            targetUuid,
                            initialGpu,
                            telemetryEvent,
                            statistics,
                            options.IntervalMilliseconds,
                            alertEvaluator);
                    }
                    else if (options.Events)
                    {
                        PrintTelemetryEvent(telemetryEvent);
                    }
                    else if (telemetryEvent.Sample is TemperatureSample current &&
                             telemetryEvent.Gpu is GpuInfo gpu)
                    {
                        PrintSample(gpu, current, options.Json);
                    }
                    else
                    {
                        PrintTelemetryDiagnostic(telemetryEvent);
                    }

                    if (alertEvaluator is not null &&
                        telemetryEvent.Kind == TelemetryEventKind.Sample &&
                        telemetryEvent.Sample is TemperatureSample sampleForAlert)
                    {
                        TelemetryEventKind? alertKind = alertEvaluator.Observe(sampleForAlert.TemperatureC);
                        if (alertKind is TelemetryEventKind kind)
                        {
                            TelemetryEvent alertEvent = telemetryEvent with
                            {
                                Sequence = ++streamSequence,
                                Kind = kind,
                                AlertThresholdC = alertEvaluator.Options.ThresholdC,
                                AlertHysteresisC = alertEvaluator.Options.HysteresisC,
                                PublicTelemetry = null,
                                ComputedMetrics = null,
                                Message = AlertMessage(
                                    kind,
                                    sampleForAlert.TemperatureC,
                                    alertEvaluator.Options),
                            };

                            store?.AppendEvent(runId!, alertEvent, currentSnapshotId);

                            if (dashboard)
                            {
                                RenderDashboard(
                                    targetUuid,
                                    initialGpu,
                                    alertEvent,
                                    statistics,
                                    options.IntervalMilliseconds,
                                    alertEvaluator);
                            }
                            else if (options.Events)
                            {
                                PrintTelemetryEvent(alertEvent);
                            }
                            else
                            {
                                PrintTelemetryDiagnostic(alertEvent);
                            }
                        }
                    }
                }

                if (options.Count != 0 && samples >= options.Count)
                {
                    break;
                }

                try
                {
                    uint delay = sampler.NextDelayMilliseconds(
                        checked((uint)options.IntervalMilliseconds));
                    await Task.Delay(TimeSpan.FromMilliseconds(delay), cancellation.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            if (dashboard)
            {
                Console.WriteLine();
            }

            completionReason = cancellation.IsCancellationRequested ? "cancelled" : "completed";
            return 0;
        }
        catch (Exception error)
        {
            monitoringError = error;
            throw;
        }
        finally
        {
            if (store is not null && runId is not null)
            {
                try
                {
                    store.CompleteRun(runId, completionReason, DateTimeOffset.UtcNow);
                }
                catch (Exception completionError) when (monitoringError is not null)
                {
                    Console.Error.WriteLine(
                        $"rtxmon-csharp: também não foi possível encerrar o run {runId}: " +
                        completionError.Message);
                }
            }
        }
    }

    private static string ApplicationVersion() =>
        typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "unknown";

    private static string GpuFingerprint(GpuInfo gpu) =>
        $"{gpu.Uuid}\u001f{gpu.Index}\u001f{gpu.Name}\u001f{gpu.DriverVersion}\u001f{gpu.NvmlVersion}";

    private static GpuEvidenceSnapshot CaptureGpuEvidence(GpuInfo gpu)
    {
        DateTimeOffset observedAt = DateTimeOffset.UtcNow;
        try
        {
            using NvidiaMonitor monitor = NvidiaMonitor.Open();
            GpuInfo current = monitor.GetGpuByUuid(gpu.Uuid);
            if (current.Index != gpu.Index)
            {
                return new GpuEvidenceSnapshot(
                    gpu,
                    null,
                    BoardEvidenceState.QueryFailed,
                    $"O índice mudou de {gpu.Index} para {current.Index} durante a captura da identidade.",
                    observedAt);
            }

            BoardIdentity board = monitor.GetBoardIdentity(current.Index);
            return new GpuEvidenceSnapshot(
                gpu,
                board,
                BoardEvidenceState.Available,
                null,
                observedAt);
        }
        catch (Exception error) when (error is
            RtxMonitorException or
            DllNotFoundException or
            EntryPointNotFoundException or
            BadImageFormatException)
        {
            return new GpuEvidenceSnapshot(
                gpu,
                null,
                BoardEvidenceState.QueryFailed,
                error.Message,
                observedAt);
        }
    }

    private static void PrintTelemetryEvent(TelemetryEvent telemetryEvent) =>
        Console.WriteLine(TelemetryJson.Serialize(telemetryEvent));

    private static void PrintTelemetryDiagnostic(TelemetryEvent telemetryEvent)
    {
        if (telemetryEvent.Kind == TelemetryEventKind.Gap)
        {
            Console.Error.WriteLine(
                $"{telemetryEvent.ObservedAt:O} | GPU {telemetryEvent.TargetGpuUuid} | gap | " +
                $"{telemetryEvent.StatusName} | nova tentativa em " +
                $"{telemetryEvent.RetryAfterMilliseconds} ms | {telemetryEvent.Message}");
            return;
        }

        if (telemetryEvent.Kind == TelemetryEventKind.Recovered)
        {
            Console.Error.WriteLine(
                $"{telemetryEvent.ObservedAt:O} | GPU {telemetryEvent.TargetGpuUuid} | " +
                $"monitoramento recuperado após {telemetryEvent.ConsecutiveFailures} falha(s)");
            return;
        }

        Console.Error.WriteLine(
            $"{telemetryEvent.ObservedAt:O} | GPU {telemetryEvent.TargetGpuUuid} | " +
            $"{telemetryEvent.KindName} | {telemetryEvent.Message}");
    }

    private static string AlertMessage(
        TelemetryEventKind kind,
        int temperatureC,
        AlertOptions alertOptions) =>
        kind == TelemetryEventKind.AlertRaised
            ? $"die temperature {temperatureC} C reached alert threshold {alertOptions.ThresholdC} C"
            : $"die temperature {temperatureC} C cleared alert threshold {alertOptions.ThresholdC} C " +
              $"(hysteresis {alertOptions.HysteresisC} C)";

    private static void RenderDashboard(
        string targetUuid,
        GpuInfo? initialGpu,
        TelemetryEvent telemetryEvent,
        RunningStatistics statistics,
        int intervalMilliseconds,
        AlertEvaluator? alertEvaluator)
    {
        int width = Math.Max(40, Console.WindowWidth - 1);
        GpuInfo? gpu = telemetryEvent.Gpu ?? initialGpu;
        string gpuDescription = gpu is null
            ? "aguardando disponibilidade"
            : $"[{gpu.Index}] {gpu.Name}";
        string driverDescription = gpu is null
            ? "indisponível"
            : $"{gpu.DriverVersion}    NVML {gpu.NvmlVersion}";
        string temperature = telemetryEvent.Sample is TemperatureSample sample
            ? $"{sample.TemperatureC,3} °C"
            : "--- °C";
        string sessionStatistics = statistics.Count == 0
            ? "sem amostras válidas"
            : $"mín {statistics.Minimum,3} °C | média {statistics.Average,5:F1} °C | máx {statistics.Maximum,3} °C";
        string status = telemetryEvent.Kind switch
        {
            TelemetryEventKind.Sample => "leitura disponível",
            TelemetryEventKind.Gap =>
                $"lacuna: {telemetryEvent.StatusName}; nova tentativa em {telemetryEvent.RetryAfterMilliseconds} ms",
            TelemetryEventKind.Recovered =>
                $"recuperado após {telemetryEvent.ConsecutiveFailures} falha(s)",
            TelemetryEventKind.AlertRaised => "alerta disparado",
            TelemetryEventKind.AlertCleared => "alerta encerrado",
            _ => "estado desconhecido",
        };
        string source = telemetryEvent.Sample?.BackendName ?? "nenhuma leitura atual";
        string alertLine = alertEvaluator is null
            ? "ALERTA    desabilitado"
            : alertEvaluator.Alarmed
                ? $"ALERTA    ativo — limiar {alertEvaluator.Options.ThresholdC} °C " +
                  $"(histerese {alertEvaluator.Options.HysteresisC} °C)"
                : $"ALERTA    normal — limiar {alertEvaluator.Options.ThresholdC} °C " +
                  $"(histerese {alertEvaluator.Options.HysteresisC} °C)";

        string[] lines =
        [
            "RTX MONITOR — leitura térmica resiliente",
            string.Empty,
            $"GPU       {gpuDescription}",
            $"UUID      {targetUuid}",
            $"Driver    {driverDescription}",
            string.Empty,
            $"DIE       {temperature}",
            $"SESSÃO    {sessionStatistics}",
            $"ESTADO    {status}",
            $"EVENTO    {telemetryEvent.ObservedAt:yyyy-MM-dd HH:mm:ss.fff zzz} | intervalo {intervalMilliseconds} ms",
            $"FONTE     {source}",
            alertLine,
            string.Empty,
            "Uma lacuna nunca reutiliza a última temperatura como se fosse atual.",
            "Ctrl+C para encerrar.",
        ];

        Console.Write("\u001b[H");
        foreach (string line in lines)
        {
            string visible = line.Length > width ? line[..width] : line;
            Console.WriteLine(visible.PadRight(width));
        }
    }
}
