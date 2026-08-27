using System.Text.Json;
using RtxMonitor.Managed;

namespace RtxMonitor.Managed.Tests;

internal sealed record ReadOutcome(
    MonitoringStatus Status,
    uint GpuIndex,
    int TemperatureC,
    ulong TimestampUnixMilliseconds);

internal sealed class FakeSession(
    IReadOnlyList<GpuInfo> gpus,
    IEnumerable<ReadOutcome> outcomes) : ITemperatureSession
{
    private readonly Queue<ReadOutcome> outcomes = new(outcomes);

    public IReadOnlyList<GpuInfo> GetGpus() => gpus;

    public TemperatureSample ReadGpuDieTemperature(uint index)
    {
        if (outcomes.Count == 0)
        {
            throw new RtxMonitorException(
                MonitoringStatus.BackendError,
                "A sessão simulada não possui outra leitura.");
        }

        ReadOutcome outcome = outcomes.Dequeue();
        if (outcome.Status != MonitoringStatus.Ok)
        {
            throw new RtxMonitorException(outcome.Status, "Falha simulada.");
        }
        if (outcome.GpuIndex != index)
        {
            throw new RtxMonitorException(
                MonitoringStatus.BackendError,
                "A sessão simulada recebeu um índice inesperado.");
        }

        return new TemperatureSample(
            outcome.GpuIndex,
            outcome.TemperatureC,
            TemperatureBackend.NvmlTemperatureV1,
            "NVML fake",
            DateTimeOffset.FromUnixTimeMilliseconds(
                checked((long)outcome.TimestampUnixMilliseconds)),
            outcome.TimestampUnixMilliseconds);
    }

    public void Dispose()
    {
    }
}

internal static class Program
{
    private static int failures;

    private static int Main()
    {
        TestSuccessfulSampleAndCircularBuffer();
        TestGapRecoveryAndIndexChange();
        TestBackoffCap();
        TestNonrecoverableStatus();
        TestAlertRaisesAndClearsWithoutHysteresis();
        TestAlertHysteresisPreventsFlapping();
        TestAlertInvalidOptionsAreRejected();
        TestComputedMetricsAreReproducible();
        TestPerformanceLimitReasonsAreTranslated();
        TestTelemetryJsonV3PreservesProvenance();
        TestOptionalNativeExportProbeFailsClosed();

        if (failures == 0)
        {
            Console.WriteLine("RtxMonitor.Managed tests passed");
        }

        return failures == 0 ? 0 : 1;
    }

    private static void TestSuccessfulSampleAndCircularBuffer()
    {
        GpuInfo fakeGpu = Gpu(2, "GPU-ABC");
        var session = new FakeSession(
            [fakeGpu],
            [
                Outcome(2, 40, 1_700_000_000_000),
                Outcome(2, 41, 1_700_000_001_000),
                Outcome(2, 42, 1_700_000_002_000),
            ]);

        using var sampler = new ResilientSampler(
            "gpu-abc",
            new SamplingOptions(2, 100, 400),
            () => session);

        IReadOnlyList<TelemetryEvent> first = sampler.Poll();
        sampler.Poll();
        sampler.Poll();
        IReadOnlyList<TelemetryEvent> history = sampler.GetRecentEvents();

        Check(first.Count == 1, "contagem do primeiro poll");
        Check(first[0].Kind == TelemetryEventKind.Sample, "evento de amostra");
        Check(first[0].Sample?.TemperatureC == 40, "temperatura preservada");
        Check(first[0].Gpu?.Index == 2, "UUID sem distinção de maiúsculas");
        Check(history.Count == 2, "buffer limitado");
        Check(history[0].Sequence == 2, "sequência mais antiga preservada");
        Check(history[1].Sequence == 3, "sequência mais nova preservada");
        Check(sampler.NextDelayMilliseconds(1000) == 1000, "intervalo após sucesso");
    }

    private static void TestGapRecoveryAndIndexChange()
    {
        var sessions = new Queue<ITemperatureSession>(
        [
            new FakeSession(
                [Gpu(1, "GPU-STABLE")],
                [new ReadOutcome(MonitoringStatus.GpuLost, 1, 0, 0)]),
            new FakeSession(
                [Gpu(4, "GPU-STABLE")],
                [Outcome(4, 44, 1_700_000_003_000)]),
        ]);

        using var sampler = new ResilientSampler(
            "GPU-STABLE",
            new SamplingOptions(3, 125, 500),
            () => sessions.Dequeue());

        IReadOnlyList<TelemetryEvent> failed = sampler.Poll();
        IReadOnlyList<TelemetryEvent> recovered = sampler.Poll();
        IReadOnlyList<TelemetryEvent> history = sampler.GetRecentEvents();

        Check(failed.Count == 1, "contagem do gap");
        Check(failed[0].Kind == TelemetryEventKind.Gap, "tipo gap");
        Check(failed[0].Status == MonitoringStatus.GpuLost, "status do gap");
        Check(failed[0].RetryAfterMilliseconds == 125, "backoff inicial");
        Check(recovered.Count == 2, "recuperação gera dois eventos");
        Check(recovered[0].Kind == TelemetryEventKind.Recovered, "tipo recovered");
        Check(recovered[0].ConsecutiveFailures == 1, "falhas antes da recuperação");
        Check(recovered[1].Kind == TelemetryEventKind.Sample, "amostra após recuperação");
        Check(recovered[1].Gpu?.Index == 4, "UUID reencontrado em novo índice");
        Check(
            recovered[0].ObservedAtUnixMilliseconds <= recovered[1].ObservedAtUnixMilliseconds,
            "timestamps da recuperação não retrocedem");
        Check(history.Select(item => item.Sequence).SequenceEqual([1UL, 2UL, 3UL]), "ordem do histórico");
    }

    private static void TestBackoffCap()
    {
        using var sampler = new ResilientSampler(
            "GPU-OFFLINE",
            new SamplingOptions(8, 100, 250),
            () => throw new RtxMonitorException(
                MonitoringStatus.DriverNotLoaded,
                "Driver indisponível."));

        uint[] delays =
        [
            sampler.Poll()[0].RetryAfterMilliseconds,
            sampler.Poll()[0].RetryAfterMilliseconds,
            sampler.Poll()[0].RetryAfterMilliseconds,
            sampler.Poll()[0].RetryAfterMilliseconds,
        ];

        Check(delays.SequenceEqual([100U, 200U, 250U, 250U]), "limite do backoff");
        Check(sampler.ConsecutiveFailures == 4, "contador de falhas consecutivas");
    }

    private static void TestNonrecoverableStatus()
    {
        using var sampler = new ResilientSampler(
            "GPU-DENIED",
            sessionFactory: () => throw new RtxMonitorException(
                MonitoringStatus.NoPermission,
                "Permissão negada."));

        try
        {
            sampler.Poll();
            Check(false, "falha fatal deve escapar do sampler");
        }
        catch (RtxMonitorException error)
        {
            Check(error.Status == MonitoringStatus.NoPermission, "status fatal preservado");
        }
    }

    private static void TestAlertRaisesAndClearsWithoutHysteresis()
    {
        var evaluator = new AlertEvaluator(new AlertOptions(80, 0));

        Check(evaluator.Observe(60) is null, "sem alerta abaixo do limiar");
        Check(!evaluator.Alarmed, "não alarmado abaixo do limiar");

        TelemetryEventKind? raised = evaluator.Observe(80);
        Check(raised == TelemetryEventKind.AlertRaised, "alerta disparado no limiar");
        Check(evaluator.Alarmed, "alarmado após cruzar o limiar");
        Check(
            evaluator.Observe(80) is null,
            "não encerra enquanto a temperatura permanece exatamente no limiar");
        Check(evaluator.Alarmed, "continua alarmado exatamente no limiar");
        Check(evaluator.Observe(85) is null, "sem repetição de alerta enquanto quente");

        TelemetryEventKind? cleared = evaluator.Observe(79);
        Check(cleared == TelemetryEventKind.AlertCleared, "alerta encerrado logo abaixo do limiar");
        Check(!evaluator.Alarmed, "não alarmado após encerrar");
    }

    private static void TestAlertHysteresisPreventsFlapping()
    {
        var evaluator = new AlertEvaluator(new AlertOptions(80, 5));

        Check(evaluator.Observe(80) is not null, "alerta disparado no limiar");
        Check(evaluator.Observe(76) is null, "sem encerramento dentro da faixa de histerese");
        Check(evaluator.Alarmed, "ainda alarmado dentro da faixa de histerese");
        Check(evaluator.Observe(75) is not null, "encerra abaixo do limiar menos a histerese");
        Check(!evaluator.Alarmed, "não alarmado após encerrar com histerese");
    }

    private static void TestAlertInvalidOptionsAreRejected()
    {
        Check(
            Throws<ArgumentOutOfRangeException>(() => new AlertEvaluator(new AlertOptions(80, -1))),
            "histerese negativa deve ser rejeitada");
        Check(
            Throws<ArgumentOutOfRangeException>(() => new AlertEvaluator(new AlertOptions(80, 81))),
            "histerese acima do limiar deve ser rejeitada");
    }

    private static void TestComputedMetricsAreReproducible()
    {
        using var engine = new ComputedMetricsEngine(new ComputedMetricOptions(5000, 45, 16));

        ComputedMetricsReport first = engine.Observe(Telemetry(1000, 40, 35));
        Check(first.Metrics.Count == 4, "quantidade de métricas calculadas");
        Check(first.Metrics[0].Value == 40, "média com uma amostra");
        Check(
            first.Metrics[1].State == ComputedMetricState.InsufficientData,
            "inclinação exige duas amostras");
        Check(first.Metrics[3].Value == 5, "delta térmico entre canais conhecidos");

        ComputedMetricsReport second = engine.Observe(Telemetry(2000, 50, null));
        Check(second.Metrics[0].Value == 45, "média móvel reproduzível");
        Check(second.Metrics[1].Value == 10, "inclinação reproduzível");
        Check(second.Metrics[2].Value == 0, "zero legítimo não vira indisponível");
        Check(
            second.Metrics[3].State == ComputedMetricState.InputUnavailable,
            "canal de memória ausente não vira zero");

        ComputedMetricsReport third = engine.Observe(Telemetry(3000, 60, 37));
        Check(third.Metrics[0].Value == 50, "média de três amostras");
        Check(third.Metrics[1].Value == 10, "inclinação de três amostras");
        Check(third.Metrics[2].Value == 1, "tempo acima do limiar");
        Check(
            third.Metrics[2].Formula.Contains("threshold_c", StringComparison.Ordinal),
            "fórmula acompanha a métrica");
        Check(
            third.Metrics[2].InputNames.SequenceEqual(["gpu_die_temperature_c"]),
            "entradas acompanham a métrica");

        engine.Reset();
        ComputedMetricsReport reset = engine.Observe(Telemetry(4000, 55, 38));
        Check(
            reset.Metrics[1].State == ComputedMetricState.InsufficientData,
            "reset limpa a janela histórica");
    }

    private static void TestOptionalNativeExportProbeFailsClosed()
    {
        Check(
            NativeMethods.PrivateVoltageStatusExportAvailable,
            "biblioteca atual expõe a capacidade opcional de tensão");

        bool lookupCalled = false;
        bool releaseCalled = false;
        Check(
            !NativeExportProbe.Probe(
                () => IntPtr.Zero,
                _ =>
                {
                    lookupCalled = true;
                    return true;
                },
                _ => releaseCalled = true) &&
            !lookupCalled &&
            !releaseCalled,
            "export opcional ausente não consulta nem libera handle nulo");

        IntPtr fakeHandle = new(42);
        Check(
            !NativeExportProbe.Probe(
                () => fakeHandle,
                handle => handle != fakeHandle,
                handle => releaseCalled = handle == fakeHandle) &&
            releaseCalled,
            "export opcional ausente libera a biblioteca consultada");

        bool invoked = false;
        Check(
            NativeExportProbe.InvokeOptional(
                available: false,
                () =>
                {
                    invoked = true;
                    return NativeStatus.Ok;
                }) == NativeStatus.NotSupported &&
            !invoked,
            "entry point opcional ausente retorna not_supported sem invocação");
        Check(
            NativeExportProbe.InvokeOptional(
                available: true,
                () => throw new EntryPointNotFoundException()) == NativeStatus.NotSupported,
            "corrida de resolução do entry point opcional falha fechada");
        Check(
            PrivateThermalSample.SourceKind == "nvapi_thermal_channel" &&
            PrivateThermalSample.ProfileEvidenceStage == "matched_external_reference" &&
            PrivateThermalSample.InterfaceId == "0x65fe3aad" &&
            PrivateThermalSample.StructureVersion == "0x000200a8" &&
            PrivateThermalSample.FunctionRva == "0x001e0bc0" &&
            PrivateThermalSample.NvapiModuleSha256 ==
                "df6455ccf83e43cfe68f405af1eec4e053c7f95da998bf358053b7583980c2f4",
            "contrato gerenciado da amostra térmica preserva todos os pins do perfil");
        Check(
            PrivateVoltageSample.SourceKind == "nvapi_voltage_status" &&
            PrivateVoltageSample.ProfileEvidenceStage == "matched_external_reference" &&
            PrivateVoltageSample.InterfaceId == "0x465f9bcf" &&
            PrivateVoltageSample.StructureVersion == "0x0001004c" &&
            PrivateVoltageSample.FunctionRva == "0x001c9070" &&
            PrivateVoltageSample.NvapiModuleSha256 ==
                "df6455ccf83e43cfe68f405af1eec4e053c7f95da998bf358053b7583980c2f4",
            "contrato gerenciado da amostra de tensão preserva todos os pins do perfil");
        var thermal = new PrivateThermalSample(
            0,
            40.0,
            50.25,
            0,
            DateTimeOffset.UnixEpoch,
            0);
        var voltage = new PrivateVoltageSample(
            0,
            956_250,
            0,
            DateTimeOffset.UnixEpoch,
            0);
        Check(
            thermal.DeltaC == 10.25 && voltage.GpuCoreVoltageV == 0.95625,
            "valores derivados privados são calculados pela record e não aceitam contradição do chamador");
    }

    private static void TestTelemetryJsonV3PreservesProvenance()
    {
        const ulong timestamp = 1_700_000_000_000;
        GpuInfo gpu = Gpu(2, "GPU-JSON");
        PublicTelemetryReport telemetry = Telemetry(timestamp, 40, null);
        using var engine = new ComputedMetricsEngine(new ComputedMetricOptions(5000, 80, 16));
        ComputedMetricsReport computed = engine.Observe(telemetry);
        var sample = new TemperatureSample(
            gpu.Index,
            40,
            TemperatureBackend.NvmlTemperatureV1,
            "NVML fake",
            DateTimeOffset.FromUnixTimeMilliseconds(checked((long)timestamp)),
            timestamp);
        var telemetryEvent = new TelemetryEvent(
            1,
            TelemetryEventKind.Sample,
            gpu.Uuid,
            gpu,
            sample,
            sample.CapturedAt,
            timestamp,
            MonitoringStatus.Ok,
            "ok",
            string.Empty,
            0,
            0,
            PublicTelemetry: telemetry,
            ComputedMetrics: computed);

        using JsonDocument document = JsonDocument.Parse(TelemetryJson.Serialize(telemetryEvent));
        JsonElement root = document.RootElement;
        Check(root.GetProperty("schema_version").GetInt32() == 4, "evento enriquecido usa schema 4");
        JsonElement field = root.GetProperty("public_telemetry").GetProperty("fields")[0];
        Check(field.GetProperty("provider").GetString() == "NVML fake", "provedor é persistido");
        Check(field.GetProperty("origin").GetString() == "driver_reported", "origem é persistida");
        Check(field.GetProperty("value_i64").GetInt64() == 40, "valor bruto é persistido");
        JsonElement metric = root.GetProperty("computed_metrics").GetProperty("metrics")[0];
        Check(metric.GetProperty("formula").GetString()!.Length > 0, "fórmula é persistida");
        Check(metric.GetProperty("inputs").GetArrayLength() == 1, "entradas são persistidas");
    }

    private static void TestPerformanceLimitReasonsAreTranslated()
    {
        const ulong timestamp = 1_700_000_000_000;
        var field = new PublicTelemetryValue(
            PublicTelemetryField.ClockEventReasonsCurrent,
            "clock_event_reasons_current",
            PublicTelemetryProvider.NvmlClockEventReasons,
            "NVML fake",
            CapabilityState.Available,
            "available",
            DataOrigin.DriverReported,
            "driver_reported",
            TelemetryValueType.Bitmask,
            "bitmask",
            TelemetryUnit.Bitmask,
            "bitmask",
            0,
            0,
            (1UL << 0) | (1UL << 5),
            null,
            null,
            timestamp);
        var report = new PublicTelemetryReport(
            0,
            DateTimeOffset.FromUnixTimeMilliseconds(checked((long)timestamp)),
            timestamp,
            [field]);

        PerformanceLimitReasonReport? reasons = PerformanceLimitReasons.From(report);
        Check(reasons?.RawBitmask == 33, "PerfCap preserva a máscara bruta");
        Check(
            reasons?.ActiveReasons.SequenceEqual(["gpu_idle", "software_thermal"]) == true,
            "PerfCap decompõe todos os bits ativos");
        Check(reasons?.PrimaryReason == "idle", "PerfCap escolhe razão primária estável");
    }

    private static PublicTelemetryReport Telemetry(
        ulong timestampUnixMilliseconds,
        long gpuTemperatureC,
        long? memoryTemperatureC)
    {
        var fields = new List<PublicTelemetryValue>
        {
            TelemetryValue(
                PublicTelemetryField.GpuDieTemperatureC,
                "gpu_die_temperature_c",
                PublicTelemetryProvider.NvmlTemperatureV1,
                0,
                gpuTemperatureC,
                timestampUnixMilliseconds),
        };
        if (memoryTemperatureC is long memory)
        {
            fields.Add(TelemetryValue(
                PublicTelemetryField.MemoryTemperatureC,
                "memory_temperature_c",
                PublicTelemetryProvider.NvmlFieldValues,
                82,
                memory,
                timestampUnixMilliseconds));
        }

        return new PublicTelemetryReport(
            2,
            DateTimeOffset.FromUnixTimeMilliseconds(checked((long)timestampUnixMilliseconds)),
            timestampUnixMilliseconds,
            fields);
    }

    private static PublicTelemetryValue TelemetryValue(
        PublicTelemetryField field,
        string fieldName,
        PublicTelemetryProvider provider,
        uint providerNativeId,
        long value,
        ulong timestampUnixMilliseconds) => new(
            field,
            fieldName,
            provider,
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
            providerNativeId,
            null,
            value,
            null,
            timestampUnixMilliseconds);

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

    private static GpuInfo Gpu(uint index, string uuid) =>
        new(index, "Fake NVIDIA RTX", uuid, "test-driver", "test-nvml");

    private static ReadOutcome Outcome(
        uint index,
        int temperature,
        ulong timestamp) =>
        new(MonitoringStatus.Ok, index, temperature, timestamp);

    private static void Check(bool condition, string message)
    {
        if (condition)
        {
            return;
        }

        failures++;
        Console.Error.WriteLine($"FAILED: {message}");
    }
}
