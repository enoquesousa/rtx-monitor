using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using RtxMonitor.Managed;
using RtxMonitor.Service;

namespace RtxMonitor.Service.Tests;

internal static class ExperimentalProfileProcessTests
{
    internal static async Task RunAsync()
    {
        ProtocolValidation();
        foreach (string mode in new[] { "success", "stderr-flood" })
        {
            using var child = new Child(mode);
            ExperimentalProfileObservation actual = await child.CaptureAsync(default);
            Check(actual.Gpu == Gpu() && actual.Board == Board() && actual.Profile?.ProfileRevision == 2,
                "profile process preserves identity, board, and catalog revision");
            child.AssertExited();
        }
        foreach (string mode in new[] { "oversized", "invalid", "duplicate", "missing", "wrong-uuid", "exit-failure", "empty" })
        {
            using var child = new Child(mode);
            bool rejected = false;
            try { await child.CaptureAsync(default); }
            catch (Exception error) when (error is InvalidDataException or JsonException) { rejected = true; }
            Check(rejected, $"profile process rejects {mode}");
            child.AssertExited();
        }
        await TimeoutAndLatchAsync();
        await CancellationAndLatchAsync();
        await DisabledAndPreCancelledAsync();
        Console.WriteLine("Experimental profile supervisor tests passed (synthetic children only)");
    }

    private static void ProtocolValidation()
    {
        string payload = ExperimentalProfileProtocol.Serialize(Observation());
        ExperimentalProfileObservation observation = ExperimentalProfileProtocol.Parse(payload, Gpu().Uuid);
        Check(observation.Gpu == Gpu() && observation.Board == Board() &&
            observation.Profile!.IsEligibleForAcquisition(PrivateOperationState.Compatible),
            "protocol roundtrip retains a complete eligible profile");
        JsonObject node = JsonNode.Parse(payload)!.AsObject();
        node["observation"]!["profile"]!["identity_checks"] = null;
        bool rejected = false;
        try { ExperimentalProfileProtocol.Parse(node.ToJsonString(), Gpu().Uuid); }
        catch (InvalidDataException) { rejected = true; }
        Check(rejected, "missing identity checks cannot be treated as eligibility");
        foreach (MonitoringStatus status in new[] { MonitoringStatus.Timeout, MonitoringStatus.GpuLost, MonitoringStatus.BackendNotFound })
        {
            rejected = false;
            try { ExperimentalProfileProtocol.Parse(ExperimentalProfileProtocol.SerializeError(new RtxMonitorException(status, "fixture")), Gpu().Uuid); }
            catch (RtxMonitorException error) when (error.Status == status) { rejected = true; }
            Check(rejected, "profile diagnostic preserves specific native failure");
        }
    }

    private static async Task TimeoutAndLatchAsync()
    {
        using var child = new Child("hang", TimeSpan.FromSeconds(2));
        var source = new Source(child);
        var workers = new NoSensorWorkers();
        await using var collector = new ExperimentalTelemetryCollector(true, 100, source, workers);
        long started = Stopwatch.GetTimestamp();
        ExperimentalTelemetryReport report = await collector.CollectAsync(Gpu(), default);
        Check(All(report, ExperimentalTelemetryState.Timeout), "hung diagnostic returns timeout with no values");
        Check(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(6), "diagnostic wait and cleanup are bounded");
        child.AssertExited();
        report = await collector.CollectAsync(Gpu(), default);
        Check(All(report, ExperimentalTelemetryState.Timeout) && source.Calls == 1 && workers.Starts == 0,
            "timed out diagnostic is not restarted and starts no acquisition");
    }

    private static async Task CancellationAndLatchAsync()
    {
        using var child = new Child("hang");
        var source = new Source(child);
        var workers = new NoSensorWorkers();
        await using var collector = new ExperimentalTelemetryCollector(true, 100, source, workers);
        using var cancellation = new CancellationTokenSource();
        Task<ExperimentalTelemetryReport> pending = collector.CollectAsync(Gpu(), cancellation.Token);
        await child.WaitUntilStartedAsync();
        cancellation.Cancel();
        bool cancelled = false;
        try { await pending.WaitAsync(TimeSpan.FromSeconds(4)); }
        catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled, "cancellation interrupts profile diagnosis");
        child.AssertExited();
        Check(All(await collector.CollectAsync(Gpu(), default), ExperimentalTelemetryState.Cancelled) &&
            source.Calls == 1 && workers.Starts == 0, "cancelled diagnostic cannot restart or acquire sensors");
    }

    private static async Task DisabledAndPreCancelledAsync()
    {
        using var child = new Child("success");
        var source = new Source(child);
        var workers = new NoSensorWorkers();
        await using (var collector = new ExperimentalTelemetryCollector(false, 100, source, workers))
        {
            Check(All(await collector.CollectAsync(Gpu(), default), ExperimentalTelemetryState.Disabled) &&
                source.Calls == 0 && workers.Starts == 0 && !File.Exists(child.PidPath),
                "default disabled mode never starts a profile diagnostic or sensor process");
        }
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        bool cancelled = false;
        try { await child.CaptureAsync(cancellation.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled && !File.Exists(child.PidPath), "pre-cancelled diagnostic does not launch a child");
    }

    internal static async Task<int> RunChildAsync(string mode, string pidPath)
    {
        File.WriteAllText(pidPath, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (mode == "hang") { await Task.Delay(Timeout.Infinite); return 1; }
        if (mode == "empty") return 0;
        if (mode == "stderr-flood") await Console.Error.WriteAsync(new string('x', 128 * 1024));
        string payload = ExperimentalProfileProtocol.Serialize(Observation());
        if (mode == "oversized") payload = new string('x', ExperimentalProfileProcess.MaximumOutputBytes + 1);
        if (mode == "invalid") payload = "invalid JSON";
        if (mode == "duplicate") payload = payload.Insert(1, "\"protocol_version\":1,");
        if (mode == "missing")
        {
            JsonObject node = JsonNode.Parse(payload)!.AsObject();
            node["observation"]!["gpu"]!.AsObject().Remove("driver_version");
            payload = node.ToJsonString();
        }
        if (mode == "wrong-uuid") payload = payload.Replace(Gpu().Uuid, "GPU-aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", StringComparison.Ordinal);
        await Console.Out.WriteLineAsync(payload);
        await Console.Out.FlushAsync();
        if (mode == "oversized") await Task.Delay(Timeout.Infinite);
        return mode == "exit-failure" ? 1 : 0;
    }

    private static ExperimentalProfileObservation Observation() => new(Gpu(), Board(), new PrivateProfileStatus(
        0, ExperimentalTelemetryReport.SupportedProfileId, 2, PrivateProfileState.Active, null,
        new[] { "pci_vendor_id", "pci_device_id", "pci_subsystem_vendor_id", "pci_subsystem_device_id", "gpu_uuid", "vbios_version", "driver_version" }
            .Select(field => new PrivateProfileIdentityCheck(field, "matched")).ToArray(),
        PrivateOperationState.Compatible, PrivateOperationState.Compatible, 100, 2000, 100, 2000));

    private static GpuInfo Gpu() => new(0, "NVIDIA GeForce RTX 3060", ExperimentalTelemetryReport.SupportedGpuUuid, "610.88", "13.610.88");
    private static BoardIdentity Board() => new(0, 0x10de, 0x2504, 0x10de, 0x1536, 0, 1, 0, 0,
        BoardIdentityFlags.PciValid | BoardIdentityFlags.VbiosValid, "00000000:01:00.0", "94.06.25.00.fc");

    private static bool All(ExperimentalTelemetryReport report, ExperimentalTelemetryState expected) =>
        report.Fields.All(field => field.State == expected && field.Value is null && field.RawValue is null);
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Source(Child child) : IExperimentalProfileSource
    {
        internal int Calls;
        public Task<ExperimentalProfileObservation> CaptureAsync(string gpuUuid, CancellationToken cancellationToken)
        {
            Calls++;
            return child.CaptureAsync(cancellationToken);
        }
    }

    private sealed class NoSensorWorkers : IExperimentalWorkerFactory
    {
        internal int Starts;
        public Task<IExperimentalWorker> StartAsync(ExperimentalProfileObservation observation, string operation, CancellationToken cancellationToken)
        {
            Starts++;
            throw new InvalidOperationException("A rejected diagnostic cannot create an acquisition process.");
        }
    }

    private sealed class Child : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "rtxmon-profile-test-" + Guid.NewGuid().ToString("N"));
        private readonly string mode;
        private readonly TimeSpan timeout;
        internal Child(string mode, TimeSpan? timeout = null)
        {
            this.mode = mode;
            // Process startup can take longer under a loaded Windows CI host.
            // Timeout behavior itself is covered by the explicit 2-second case.
            this.timeout = timeout ?? TimeSpan.FromSeconds(10);
            Directory.CreateDirectory(directory);
            PidPath = Path.Combine(directory, "child.pid");
        }
        internal string PidPath { get; }
        internal Task<ExperimentalProfileObservation> CaptureAsync(CancellationToken cancellationToken)
        {
            string executable = Environment.ProcessPath!;
            var info = new ProcessStartInfo(executable);
            if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                info.ArgumentList.Add(typeof(ExperimentalProfileProcessTests).Assembly.Location);
            info.ArgumentList.Add("--fake-experimental-profile-worker");
            info.ArgumentList.Add(mode);
            info.ArgumentList.Add(PidPath);
            return ExperimentalProfileProcess.CaptureAsync(info, Gpu().Uuid, timeout, cancellationToken);
        }
        internal async Task WaitUntilStartedAsync()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (!File.Exists(PidPath)) await Task.Delay(20, deadline.Token);
        }
        internal void AssertExited()
        {
            Check(File.Exists(PidPath), "synthetic diagnostic PID was recorded");
            using Process? child = FindChild();
            Check(child is null || child.HasExited, "supervised diagnostic child is confirmed stopped");
        }
        private Process? FindChild()
        {
            if (!File.Exists(PidPath)) return null;
            int pid = int.Parse(File.ReadAllText(PidPath), System.Globalization.CultureInfo.InvariantCulture);
            try { return Process.GetProcessById(pid); }
            catch (ArgumentException) { return null; }
        }
        public void Dispose()
        {
            using Process? child = FindChild();
            if (child is not null && !child.HasExited)
            {
                child.Kill();
                if (!child.WaitForExit(2000)) throw new InvalidOperationException("Synthetic diagnostic cleanup failed.");
            }
            File.Delete(PidPath);
            Directory.Delete(directory);
        }
    }
}
