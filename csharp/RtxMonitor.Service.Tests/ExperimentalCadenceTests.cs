using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RtxMonitor.ConsoleApp;
using RtxMonitor.Managed;
using RtxMonitor.Service;
using RtxMonitor.Storage;

namespace RtxMonitor.Service.Tests;

internal static class ExperimentalCadenceTests
{
    internal static async Task RunAsync()
    {
        await NormalCadenceAsync();
        await FasterDiagnosticRetainsSensorMinimumAsync();
        await OverrunAsync();
        await EmptyAndFailedAsync();
    }

    private static async Task NormalCadenceAsync()
    {
        using var fixture = new Fixture();
        await fixture.Worker.StartAsync(default);
        await fixture.WaitForPauseAsync();
        Check(fixture.Source.Starts.SequenceEqual(new long[] { 0 }) && fixture.Clock.GetTimestamp() == 200,
            "first hosted collection includes 200 ms of diagnostic work");
        Check(fixture.State.GetSnapshot(Gpu().Uuid)!.Fields.All(field => field.State == ExperimentalTelemetryState.Available),
            "cadence test drives actual hosted collection and publishes valid samples");
        fixture.Clock.Advance(799);
        Check(fixture.Source.Starts.Count == 1, "next collection cannot start before its configured period");
        fixture.Clock.Advance(1);
        await fixture.WaitForPauseAsync(2);
        fixture.Clock.Advance(800);
        await fixture.WaitForPauseAsync(3);
        Check(fixture.Source.Starts.SequenceEqual(new long[] { 0, 1000, 2000 }),
            "diagnostic overhead must not add 200 ms to every start-to-start period");
        foreach (Sensor child in fixture.Factory.Children)
            Check(child.Reads.SequenceEqual(new long[] { 200, 1200, 2200 }),
                "both sensors retain their sampling cadence and compiled minimum interval");
        await fixture.Worker.StopAsync(default);
        Check(fixture.Factory.Children.All(child => child.Disposed) && fixture.Clock.PendingCount == 0,
            "stopping hosted cadence cancels the pending timer and disposes both sensor sessions");
    }

    private static async Task FasterDiagnosticRetainsSensorMinimumAsync()
    {
        using var fixture = new Fixture(firstWorkMilliseconds: 400);
        await fixture.Worker.StartAsync(default);
        await fixture.WaitForPauseAsync();
        fixture.Clock.Advance(600);
        await WaitUntilAsync(() => fixture.Source.Starts.Count == 2 && fixture.Clock.PendingCount == 2);
        Check(fixture.Factory.Children.All(child => child.Reads.Count == 1),
            "a faster second diagnostic cannot acquire sensors before their existing minimum period");
        fixture.Clock.Advance(200);
        await fixture.WaitForPauseAsync(2);
        Check(fixture.Source.Starts.SequenceEqual(new long[] { 0, 1000 }) &&
            fixture.Factory.Children.All(child => child.Reads.SequenceEqual(new long[] { 400, 1400 })),
            "hosted scheduling and per-sensor start limits agree despite variable diagnostic work");
        await fixture.Worker.StopAsync(default);
    }

    private static async Task OverrunAsync()
    {
        using var fixture = new Fixture(firstWorkMilliseconds: 1500);
        await fixture.Worker.StartAsync(default);
        await fixture.WaitForPauseAsync();
        fixture.Clock.Advance(999);
        Check(fixture.Source.Starts.Count == 1, "slow work does not immediately replay missed periods");
        fixture.Clock.Advance(1);
        await fixture.WaitForPauseAsync(2);
        Check(fixture.Source.Starts.SequenceEqual(new long[] { 0, 2500 }),
            "an overrun waits one new interval and resumes without a catch-up burst");
        await fixture.Worker.StopAsync(default);
    }

    private static async Task EmptyAndFailedAsync()
    {
        using (var empty = new Fixture(hasGpu: false))
        {
            await empty.Worker.StartAsync(default);
            await empty.WaitForPauseAsync(expectedCaptures: 0);
            int snapshots = empty.Monitoring.Calls;
            empty.Clock.Advance(999);
            Check(empty.Monitoring.Calls == snapshots, "an empty GPU inventory remains asleep between iterations");
            empty.Clock.Advance(1);
            await WaitUntilAsync(() => empty.Monitoring.Calls > snapshots && empty.Clock.PendingCount == 1);
            Check(empty.Source.Starts.IsEmpty && empty.Factory.Children.IsEmpty,
                "empty inventory starts neither profile nor acquisition workers");
            await empty.Worker.StopAsync(default);
        }
        using (var failed = new Fixture(profileFailure: true))
        {
            await failed.Worker.StartAsync(default);
            await failed.WaitForPauseAsync();
            Check(failed.State.GetSnapshot(Gpu().Uuid)!.Fields.All(field => field.State == ExperimentalTelemetryState.Timeout),
                "a failed profile still publishes an explicit null timeout");
            failed.Clock.Advance(800);
            await WaitUntilAsync(() => failed.Clock.PendingCount == 1);
            failed.Clock.Advance(1000);
            await WaitUntilAsync(() => failed.Clock.PendingCount == 1);
            Check(failed.Source.Starts.Count == 1 && failed.Factory.Children.IsEmpty,
                "scheduler changes cannot restart the terminally failed profile session");
            await failed.Worker.StopAsync(default);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition()) await Task.Delay(1, timeout.Token);
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static GpuInfo Gpu() => new(0, "NVIDIA GeForce RTX 3060", ExperimentalTelemetryReport.SupportedGpuUuid, "610.88", "13.610.88");
    private static BoardIdentity Board() => new(0, 0x10de, 0x2504, 0x10de, 0x1536, 0, 1, 0, 0,
        BoardIdentityFlags.PciValid | BoardIdentityFlags.VbiosValid, "00000000:01:00.0", "94.06.25.00.fc");
    private static PrivateProfileStatus Profile() => new(0, ExperimentalTelemetryReport.SupportedProfileId, 2,
        PrivateProfileState.Active, null,
        new[] { "pci_vendor_id", "pci_device_id", "pci_subsystem_vendor_id", "pci_subsystem_device_id", "gpu_uuid", "vbios_version", "driver_version" }
            .Select(field => new PrivateProfileIdentityCheck(field, "matched")).ToArray(),
        PrivateOperationState.Compatible, PrivateOperationState.Compatible, 100, 2000, 100, 2000);

    private sealed class Fixture : IDisposable
    {
        internal Fixture(bool hasGpu = true, int firstWorkMilliseconds = 200, bool profileFailure = false)
        {
            var options = new RtxMonitorServiceOptions(11899, "unused-cadence-fixture.db", 1000, 32, 1,
                TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 8, 2, TimeSpan.FromSeconds(1), 10, null, 0,
                ExperimentalTelemetryEnabled: true, ExperimentalIntervalMilliseconds: 1000);
            Monitoring = new Monitoring(Clock, hasGpu);
            State = new ExperimentalTelemetrySnapshotState(true, 1000, Monitoring, Clock);
            Source = new Source(Clock, firstWorkMilliseconds, profileFailure);
            Factory = new Factory(Clock);
            Worker = new ExperimentalTelemetryWorker(options, Monitoring, State, Source, Factory,
                NullLogger<ExperimentalTelemetryWorker>.Instance, Clock);
        }
        internal ManualClock Clock { get; } = new();
        internal Monitoring Monitoring { get; }
        internal ExperimentalTelemetrySnapshotState State { get; }
        internal Source Source { get; }
        internal Factory Factory { get; }
        internal ExperimentalTelemetryWorker Worker { get; }
        internal Task WaitForPauseAsync(int expectedCaptures = 1) => WaitUntilAsync(() =>
            Source.Starts.Count == expectedCaptures && Clock.PendingCount == 1);
        public void Dispose() => Worker.Dispose();
    }

    private sealed class Source(ManualClock clock, int firstWorkMilliseconds, bool failure) : IExperimentalProfileSource
    {
        internal ConcurrentQueue<long> Starts { get; } = new();
        public Task<ExperimentalProfileObservation> CaptureAsync(string gpuUuid, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Starts.Enqueue(clock.GetTimestamp());
            clock.Advance(Starts.Count == 1 ? firstWorkMilliseconds : 200);
            if (failure) throw new TimeoutException("synthetic profile diagnostic deadline");
            return Task.FromResult(new ExperimentalProfileObservation(Gpu(), Board(), Profile()));
        }
    }

    private sealed class Factory(ManualClock clock) : IExperimentalWorkerFactory
    {
        internal ConcurrentBag<Sensor> Children { get; } = new();
        public Task<IExperimentalWorker> StartAsync(ExperimentalProfileObservation observation, string operation, CancellationToken cancellationToken)
        {
            var sensor = new Sensor(clock, operation);
            Children.Add(sensor);
            return Task.FromResult<IExperimentalWorker>(sensor);
        }
    }

    private sealed class Sensor(ManualClock clock, string operation) : IExperimentalWorker
    {
        public string GpuUuid => Gpu().Uuid;
        public uint GpuIndex => 0;
        public int MinimumIntervalMilliseconds => 100;
        public int AcquisitionTimeoutMilliseconds => 2000;
        internal bool Disposed { get; private set; }
        internal ConcurrentQueue<long> Reads { get; } = new();
        public Task<JsonElement> ReadSampleAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Reads.Enqueue(clock.GetTimestamp());
            DateTimeOffset now = clock.GetUtcNow();
            ulong unix = checked((ulong)now.ToUnixTimeMilliseconds());
            ulong monotonic = checked((ulong)clock.GetTimestamp());
            return Task.FromResult(operation == "thermal"
                ? PrivateSampleJson.ThermalRaw(Gpu(), new PrivateThermalRawSample(0, 10240, 12800, 40000, 50000, 0,
                    now, unix, now, unix, monotonic, monotonic))
                : PrivateSampleJson.Voltage(Gpu(), new PrivateVoltageSample(0, 800000, 0, now, unix)));
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private sealed class Monitoring(ManualClock clock, bool hasGpu) : IMonitoringSnapshotSource
    {
        private int calls;
        internal int Calls => Volatile.Read(ref calls);
        public MonitoringRuntimeSnapshot GetSnapshot()
        {
            Interlocked.Increment(ref calls);
            DateTimeOffset now = clock.GetUtcNow();
            var evidence = new GpuEvidenceSnapshot(Gpu(), Board(), BoardEvidenceState.Available, null, now);
            var capabilities = new DiscoveredGpu(Gpu(), evidence, null, null, now);
            GpuRuntimeSnapshot gpu = new(Gpu(), true, "running", "cadence-fixture", null, "available", null,
                null, null, null, null, 0, null, capabilities);
            return new(now, "healthy", true, new("available", "fixture.db", 1, now, null),
                new("available", now, now, null), hasGpu ? [gpu] : []);
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private readonly object gate = new();
        private readonly DateTimeOffset initial = DateTimeOffset.UtcNow;
        private readonly HashSet<ManualTimer> timers = new();
        private long milliseconds;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() { lock (gate) return milliseconds; }
        public override DateTimeOffset GetUtcNow() => initial.AddMilliseconds(GetTimestamp());
        internal int PendingCount { get { lock (gate) return timers.Count(timer => timer.Due != long.MaxValue); } }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            return timer;
        }
        internal void Advance(long amount)
        {
            ManualTimer[] due;
            lock (gate)
            {
                milliseconds += amount;
                due = timers.Where(timer => timer.Due <= milliseconds).ToArray();
                foreach (ManualTimer timer in due) timer.Due = long.MaxValue;
            }
            foreach (ManualTimer timer in due) timer.Callback(timer.State);
        }
        private sealed class ManualTimer(ManualClock owner, TimerCallback callback, object? state) : ITimer
        {
            internal long Due { get; set; } = long.MaxValue;
            internal TimerCallback Callback { get; } = callback;
            internal object? State { get; } = state;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                Check(period == Timeout.InfiniteTimeSpan, "cadence fixture expects one-shot delay timers");
                lock (owner.gate)
                {
                    Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : owner.milliseconds + checked((long)dueTime.TotalMilliseconds);
                    owner.timers.Add(this);
                }
                return true;
            }
            public void Dispose() { lock (owner.gate) { Due = long.MaxValue; owner.timers.Remove(this); } }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
