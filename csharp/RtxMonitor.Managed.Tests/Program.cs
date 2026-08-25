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
