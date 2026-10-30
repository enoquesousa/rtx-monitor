using System.Diagnostics;
using System.Text.Json;
using RtxMonitor.ConsoleApp;
using RtxMonitor.Managed;
using RtxMonitor.Service;
using RtxMonitor.Storage;

namespace RtxMonitor.Service.Tests;

internal static class ExperimentalWorkerTests
{
    internal static async Task RunAsync()
    {
        await DisabledAndProfileGatesAsync().ConfigureAwait(false);
        await RateAndFailureLatchesAsync().ConfigureAwait(false);
        await SlowSiblingStartupAsync().ConfigureAwait(false);
        await CancellationAsync().ConfigureAwait(false);
        SnapshotInvalidation();
    }

    private static async Task DisabledAndProfileGatesAsync()
    {
        var source = new FakeProfileSource();
        var factory = new FakeFactory();
        await using (var disabled = new ExperimentalTelemetryCollector(false, 100, source, factory))
        {
            ExperimentalTelemetryReport report = await disabled.CollectAsync(Gpu(), default);
            Check(All(report, ExperimentalTelemetryState.Disabled), "disabled must return null fields");
            Check(source.Calls == 0 && factory.Starts == 0, "disabled must not diagnose or create workers");
        }
        foreach ((PrivateProfileStatus? profile, ExperimentalTelemetryState expected) in new[]
        {
            ((PrivateProfileStatus?)null, ExperimentalTelemetryState.ProfileUnknown),
            (Profile() with { ProfileId = "unknown" }, ExperimentalTelemetryState.ProfileUnknown),
            (Profile() with { ProfileRevision = 3 }, ExperimentalTelemetryState.ProfileUnknown),
            (Profile() with { ProfileState = PrivateProfileState.Revoked }, ExperimentalTelemetryState.Revoked),
        })
        {
            source = new FakeProfileSource { Observation = new(Gpu(), Board(), profile) };
            factory = new FakeFactory();
            await using var collector = new ExperimentalTelemetryCollector(true, 100, source, factory);
            Check(All(await collector.CollectAsync(Gpu(), default), expected), $"gate {expected} must reject");
            source.Observation = new(Gpu(), Board(), Profile());
            Check(All(await collector.CollectAsync(Gpu(), default), expected), "profile gate rejection remains latched");
            Check(factory.Starts == 0, "unknown, changed or revoked profile must not start child");
        }
        source = new FakeProfileSource { Observation = new(Gpu() with { DriverVersion = "611.00" }, Board(), Profile()) };
        factory = new FakeFactory();
        await using (var changed = new ExperimentalTelemetryCollector(true, 100, source, factory))
        {
            ExperimentalTelemetryReport report = await changed.CollectAsync(Gpu(), default);
            Check(All(report, ExperimentalTelemetryState.IdentityMismatch) && report.Identity.DriverVersion == "611.00",
                "mismatch must preserve newly observed driver and clear payload");
            Check(factory.Starts == 0, "identity drift must fail before process start");
        }
        foreach ((MonitoringStatus status, ExperimentalTelemetryState expected) in new[]
        {
            (MonitoringStatus.Timeout, ExperimentalTelemetryState.Timeout),
            (MonitoringStatus.RateLimited, ExperimentalTelemetryState.RateLimited),
            (MonitoringStatus.GpuLost, ExperimentalTelemetryState.GpuNotFound),
            (MonitoringStatus.BackendNotFound, ExperimentalTelemetryState.ModuleUnavailable),
        })
        {
            source = new FakeProfileSource { Error = new RtxMonitorException(status, "fixture") };
            factory = new FakeFactory();
            await using var collector = new ExperimentalTelemetryCollector(true, 100, source, factory);
            Check(All(await collector.CollectAsync(Gpu(), default), expected), "diagnostic must retain specific native failure");
            await collector.CollectAsync(Gpu(), default);
            Check(source.Calls == 1 && factory.Starts == 0, "diagnostic failure must not be retried");
        }
    }

    private static async Task RateAndFailureLatchesAsync()
    {
        var source = new FakeProfileSource();
        var factory = new FakeFactory();
        await using var collector = new ExperimentalTelemetryCollector(true, 100, source, factory);
        Check(All(await collector.CollectAsync(Gpu(), default), ExperimentalTelemetryState.Available), "valid pair available");
        Check(All(await collector.CollectAsync(Gpu(), default), ExperimentalTelemetryState.Available), "valid pair reused");
        Check(factory.Starts == 2, "each operation starts exactly once");
        foreach (FakeWorker child in factory.Children.Values)
            Check(Stopwatch.GetElapsedTime(child.ReadStarts[0], child.ReadStarts[1]) >= TimeSpan.FromMilliseconds(99),
                "request interval honors compiled minimum");
        factory.Children["thermal"].Error = new PrivateWorkerException("Timeout", "fixture native deadline");
        ExperimentalTelemetryReport failed = await collector.CollectAsync(Gpu(), default);
        Check(failed.Fields[0].State == ExperimentalTelemetryState.Timeout && failed.Fields[0].Value is null &&
            failed.Fields[1].State == ExperimentalTelemetryState.Available, "native timeout clears only its failed operation");
        int reads = factory.Children["thermal"].ReadStarts.Count;
        failed = await collector.CollectAsync(Gpu(), default);
        Check(failed.Fields[0].State == ExperimentalTelemetryState.Timeout && factory.Starts == 2 &&
            factory.Children["thermal"].ReadStarts.Count == reads && factory.Children["thermal"].Disposed,
            "timeout child is disposed and never restarted or read again");
        source.Observation = new(Gpu(), Board(), Profile() with
        {
            ProfileState = PrivateProfileState.Revoked,
            ThermalState = PrivateOperationState.Revoked,
            VoltageState = PrivateOperationState.Revoked,
        });
        Check(All(await collector.CollectAsync(Gpu(), default), ExperimentalTelemetryState.Revoked), "live revocation clears both");
        Check(factory.Children.Values.All(child => child.Disposed), "live revocation stops remaining child");
    }

    private static async Task SlowSiblingStartupAsync()
    {
        var clock = new ManualClock();
        var factory = new FakeFactory(clock)
        {
            OnStarting = operation =>
            {
                if (operation == "voltage")
                {
                    clock.Advance(TimeSpan.FromSeconds(12));
                    throw new TimeoutException("fixture slow voltage handshake and cleanup");
                }
            },
        };
        await using var collector = new ExperimentalTelemetryCollector(true, 100, new FakeProfileSource(), factory, clock);
        ExperimentalTelemetryReport report = await collector.CollectAsync(Gpu(), default);
        Check(report.Fields[0].State == ExperimentalTelemetryState.Available &&
            report.Fields[0].CapturedAt == clock.GetUtcNow() && report.Fields[1].State == ExperimentalTelemetryState.Timeout,
            "thermal is acquired after sibling startup timeout and remains fresh");
        clock.Advance(TimeSpan.FromMilliseconds(100));
        report = await collector.CollectAsync(Gpu(), default);
        Check(report.Fields[0].State == ExperimentalTelemetryState.Available && !factory.Children["thermal"].Disposed &&
            report.Fields[1].State == ExperimentalTelemetryState.Timeout && factory.Starts == 2,
            "slow sibling cannot retire or restart healthy thermal operation");
    }

    private static async Task CancellationAsync()
    {
        var factory = new FakeFactory { BlockReads = true };
        await using var collector = new ExperimentalTelemetryCollector(true, 100, new FakeProfileSource(), factory);
        using var cancellation = new CancellationTokenSource();
        Task<ExperimentalTelemetryReport> pending = collector.CollectAsync(Gpu(), cancellation.Token);
        await factory.BothReading.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        bool cancelled = false;
        try { await pending.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled && factory.Children.Values.All(child => child.Disposed), "cancellation disposes both in-flight children");
        Check(All(await collector.CollectAsync(Gpu(), default), ExperimentalTelemetryState.Cancelled) && factory.Starts == 2,
            "cancelled session cannot restart");
    }

    private static void SnapshotInvalidation()
    {
        var clock = new ManualClock();
        var monitoring = new FakeMonitoring();
        var state = new ExperimentalTelemetrySnapshotState(true, 1000, monitoring, clock);
        ExperimentalTelemetryReport valid = ValidReport(clock);
        state.Record(valid);
        Check(ReferenceEquals(state.GetSnapshot(Gpu().Uuid), valid), "unchanged snapshot is reused exactly");
        clock.Advance(TimeSpan.FromSeconds(11));
        ExperimentalTelemetryReport expired = state.GetSnapshot(Gpu().Uuid)!;
        Check(All(expired, ExperimentalTelemetryState.Timeout), "stalled snapshot expires to explicit null");
        clock.Advance(TimeSpan.FromSeconds(1));
        state.Record(ValidReport(clock));
        Check(ReferenceEquals(expired, state.GetSnapshot(Gpu().Uuid)), "expiry is latched once including timestamp");

        state = new ExperimentalTelemetrySnapshotState(true, 1000, monitoring, clock);
        state.Record(ValidReport(clock));
        monitoring.Current = monitoring.Current with { Present = false };
        Check(All(state.GetSnapshot(Gpu().Uuid)!, ExperimentalTelemetryState.GpuNotFound), "removed GPU revokes cached values");

        monitoring = new FakeMonitoring();
        state = new ExperimentalTelemetrySnapshotState(true, 1000, monitoring, clock);
        state.Record(ValidReport(clock));
        monitoring.Current = monitoring.Current with { Gpu = Gpu() with { DriverVersion = "611.00" } };
        ExperimentalTelemetryReport changed = state.GetSnapshot(Gpu().Uuid)!;
        Check(All(changed, ExperimentalTelemetryState.IdentityMismatch) && changed.Identity.DriverVersion == "611.00" &&
            changed.ProfileRevision == 2, "snapshot drift preserves current identity and reviewed profile metadata");
    }

    private static ExperimentalTelemetryReport ValidReport(TimeProvider clock) => ExperimentalTelemetryReport.Create(
        Gpu(), Board(), Profile(), Payload("thermal", clock), Payload("voltage", clock), evaluatedAt: clock.GetUtcNow());

    private static JsonElement Payload(string operation, TimeProvider clock)
    {
        DateTimeOffset now = clock.GetUtcNow();
        ulong timestamp = checked((ulong)now.ToUnixTimeMilliseconds());
        return operation == "thermal"
            ? PrivateSampleJson.ThermalRaw(Gpu(), new PrivateThermalRawSample(0, 10272, 12864, 40125, 50250, 0, now, timestamp, now, timestamp, 1000, 1001))
            : PrivateSampleJson.Voltage(Gpu(), new PrivateVoltageSample(0, 800000, 0, now, timestamp));
    }

    private static GpuInfo Gpu() => new(0, "NVIDIA GeForce RTX 3060", ExperimentalTelemetryReport.SupportedGpuUuid, "610.88", "13.610.88");
    private static BoardIdentity Board() => new(0, 0x10de, 0x2504, 0x10de, 0x1536, 0, 1, 0, 0,
        BoardIdentityFlags.PciValid | BoardIdentityFlags.VbiosValid, "00000000:01:00.0", "94.06.25.00.fc");
    private static PrivateProfileStatus Profile() => new(0, ExperimentalTelemetryReport.SupportedProfileId, 2,
        PrivateProfileState.Active, null, new[] { "pci_vendor_id", "pci_device_id", "pci_subsystem_vendor_id",
            "pci_subsystem_device_id", "gpu_uuid", "vbios_version", "driver_version" }
            .Select(field => new PrivateProfileIdentityCheck(field, "matched")).ToArray(),
        PrivateOperationState.Compatible, PrivateOperationState.Compatible, 100, 2000, 100, 2000);

    private static bool All(ExperimentalTelemetryReport report, ExperimentalTelemetryState state) =>
        report.Fields.All(field => field.State == state && (state == ExperimentalTelemetryState.Available ||
            (field.Value is null && field.RawValue is null && field.CapturedAt is null)));

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class FakeProfileSource : IExperimentalProfileSource
    {
        internal int Calls;
        internal Exception? Error { get; set; }
        internal ExperimentalProfileObservation Observation { get; set; } = new(Gpu(), Board(), Profile());
        public Task<ExperimentalProfileObservation> CaptureAsync(string gpuUuid, CancellationToken cancellationToken)
        {
            Calls++;
            if (Error is not null) throw Error;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Observation);
        }
    }

    private sealed class FakeFactory(TimeProvider? clock = null) : IExperimentalWorkerFactory
    {
        internal int Starts;
        internal bool BlockReads { get; init; }
        internal Action<string>? OnStarting { get; init; }
        internal Dictionary<string, FakeWorker> Children { get; } = new();
        internal TaskCompletionSource BothReading { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int reading;
        public Task<IExperimentalWorker> StartAsync(ExperimentalProfileObservation observation, string operation, CancellationToken cancellationToken)
        {
            Starts++;
            OnStarting?.Invoke(operation);
            var child = new FakeWorker(operation, clock ?? TimeProvider.System, BlockReads, () =>
            {
                if (Interlocked.Increment(ref reading) == 2) BothReading.TrySetResult();
            });
            Children.Add(operation, child);
            return Task.FromResult<IExperimentalWorker>(child);
        }
    }

    private sealed class FakeWorker(string operation, TimeProvider clock, bool block, Action started) : IExperimentalWorker
    {
        public string GpuUuid => Gpu().Uuid;
        public uint GpuIndex => 0;
        public int MinimumIntervalMilliseconds => 100;
        public int AcquisitionTimeoutMilliseconds => 2000;
        internal Exception? Error { get; set; }
        internal bool Disposed { get; private set; }
        internal List<long> ReadStarts { get; } = new();
        public async Task<JsonElement> ReadSampleAsync(CancellationToken cancellationToken)
        {
            ReadStarts.Add(Stopwatch.GetTimestamp());
            started();
            if (Error is not null) throw Error;
            if (block) await Task.Delay(Timeout.Infinite, cancellationToken);
            return Payload(operation, clock);
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private sealed class ManualClock : TimeProvider
    {
        private readonly DateTimeOffset initial = DateTimeOffset.UtcNow;
        private long milliseconds;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Interlocked.Read(ref milliseconds);
        public override DateTimeOffset GetUtcNow() => initial.AddMilliseconds(GetTimestamp());
        internal void Advance(TimeSpan duration) => Interlocked.Add(ref milliseconds, checked((long)duration.TotalMilliseconds));
    }

    private sealed class FakeMonitoring : IMonitoringSnapshotSource
    {
        internal GpuRuntimeSnapshot Current { get; set; } = CreateGpu();
        public MonitoringRuntimeSnapshot GetSnapshot() => new(DateTimeOffset.UtcNow, "healthy", true,
            new("available", "fixture.db", 1, DateTimeOffset.UtcNow, null),
            new("available", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null), [Current]);
        private static GpuRuntimeSnapshot CreateGpu()
        {
            var evidence = new GpuEvidenceSnapshot(Gpu(), Board(), BoardEvidenceState.Available, null, DateTimeOffset.UtcNow);
            var discovered = new DiscoveredGpu(Gpu(), evidence, null, null, DateTimeOffset.UtcNow);
            return new(Gpu(), true, "running", "fixture-run", null, "available", null,
                null, null, null, null, 0, null, discovered);
        }
    }
}
