using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RtxMonitor.Service;

namespace RtxMonitor.Service.Tests;

internal static partial class Program
{
    private static async Task TestResearchWorkerSupervisionAsync()
    {
        foreach ((int failures, int seconds) in new[] { (0, 2), (1, 4), (2, 8), (3, 16), (4, 30), (5, 30), (int.MaxValue, 30) })
            Check(ResearchTelemetryWorker.RestartDelay(failures) == TimeSpan.FromSeconds(seconds),
                "restart delay must grow and remain capped at thirty seconds");
        Check(!ResearchTelemetryWorker.ShouldResetBackoff(false, TimeSpan.FromMinutes(3), TimeSpan.Zero),
            "running without a publication must not reset backoff");
        Check(!ResearchTelemetryWorker.ShouldResetBackoff(true, TimeSpan.FromSeconds(3), TimeSpan.Zero),
            "one quick publication followed by failure must not reset backoff");
        Check(!ResearchTelemetryWorker.ShouldResetBackoff(true, TimeSpan.FromSeconds(80), TimeSpan.FromSeconds(76)),
            "a publication timeout must not reset backoff despite long process uptime");
        Check(!ResearchTelemetryWorker.ShouldResetBackoff(true, TimeSpan.FromSeconds(70), TimeSpan.FromSeconds(69)),
            "one early publication followed by silence must not count as a stable publishing session");
        Check(ResearchTelemetryWorker.ShouldResetBackoff(true, TimeSpan.FromSeconds(70), TimeSpan.FromSeconds(2)),
            "a session still publishing after sixty seconds may reset backoff");

        using (var temporary = new TemporaryWorkspace())
        {
            var fixture = CreateResearchSupervisorFixture(temporary, "recover");
            using var worker = fixture.Worker;
            await worker.StartAsync(default);
            try
            {
                await WaitResearchAsync(() => HasResearchValue(fixture.State, 1), "first synthetic publication");
                await WaitResearchAsync(() => fixture.State.GetSnapshot().GetProperty("state").GetString() == "restarting", "restart after janitor-like child failure");
                foreach (bool summary in new[] { false, true })
                    Check(fixture.State.GetSnapshot(summary).GetProperty("collection").ValueKind == JsonValueKind.Null,
                        "exit must remove both cached payloads during backoff");
                await WaitResearchAsync(() => HasResearchValue(fixture.State, 2), "publication from recovered child", 10);
                string[] starts = ReadResearchCounterLines(fixture.Counter);
                Check(starts.Length == 2, "recovery must create exactly one replacement child");
                DateTimeOffset firstExit = DateTimeOffset.Parse(File.ReadAllText(fixture.Counter + ".exit"));
                DateTimeOffset secondStart = DateTimeOffset.Parse(starts[1].Split('|')[1]);
                Check(secondStart - firstExit >= TimeSpan.FromMilliseconds(1900), "failed child must not trigger a busy restart loop");
            }
            finally { await worker.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(8)); }
            Check(fixture.State.GetSnapshot().GetProperty("state").GetString() == "stopped", "stop must invalidate the recovered publication");
            AssertSyntheticResearchChildrenExited(fixture.Counter);
        }

        using (var temporary = new TemporaryWorkspace())
        {
            var fixture = CreateResearchSupervisorFixture(temporary, "orphan");
            using var worker = fixture.Worker;
            await worker.StartAsync(default);
            string orphanMarker = fixture.Counter + ".orphan";
            try
            {
                await WaitResearchAsync(() => File.Exists(orphanMarker), "grandchild holding process.log");
                await WaitResearchAsync(() => fixture.State.GetSnapshot().GetProperty("state").GetString() == "restarting", "orphan job cleanup before backoff");
                int orphanPid = int.Parse(File.ReadAllText(orphanMarker));
                AssertSyntheticResearchProcessExited(orphanPid);
                using (var unlocked = new FileStream(fixture.Counter + ".process.log", FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    Check(unlocked.CanWrite, "orphan process.log handle must be released before another attempt starts");
                await WaitResearchAsync(() => HasResearchValue(fixture.State, 2), "fresh publication after orphan job termination", 10);
                Check(ReadResearchCounterLines(fixture.Counter).Length == 2, "orphan recovery must create one serial replacement");
            }
            finally
            {
                await worker.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(8));
                if (File.Exists(orphanMarker)) AssertSyntheticResearchProcessExited(int.Parse(File.ReadAllText(orphanMarker)));
            }
            AssertSyntheticResearchChildrenExited(fixture.Counter);
        }

        using (var temporary = new TemporaryWorkspace())
        {
            var fixture = CreateResearchSupervisorFixture(temporary, "invalid-contract");
            using var worker = fixture.Worker;
            await worker.StartAsync(default);
            try
            {
                await WaitResearchAsync(() => HasResearchValue(fixture.State, 1), "publication before invalid contract");
                await WaitResearchAsync(() => fixture.State.GetSnapshot().GetProperty("state").GetString() == "restarting", "invalid contract must restart rather than fault the hosted task");
                foreach (bool summary in new[] { false, true })
                    Check(fixture.State.GetSnapshot(summary).GetProperty("collection").ValueKind == JsonValueKind.Null,
                        "invalid JSON contract must remove previous values from both views");
                Check(fixture.State.GetSnapshot().GetProperty("error").ToString().Contains("Relatório de pesquisa inválido", StringComparison.Ordinal),
                    "invalid contract reason must survive restart notification");
                await WaitResearchAsync(() => HasResearchValue(fixture.State, 2), "recovery after InvalidDataException", 10);
            }
            finally { await worker.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(8)); }
            AssertSyntheticResearchChildrenExited(fixture.Counter);
        }

        using (var temporary = new TemporaryWorkspace())
        {
            var fixture = CreateResearchSupervisorFixture(temporary, "always-exit");
            using var worker = fixture.Worker;
            await worker.StartAsync(default);
            await WaitResearchAsync(() => File.Exists(fixture.Counter) && ReadResearchCounterLines(fixture.Counter).Length == 2 &&
                fixture.State.GetSnapshot().GetProperty("error").ToString().Contains("4 segundos", StringComparison.Ordinal),
                "second failure keeps growing backoff", 10);
            await worker.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(8));
            await Task.Delay(250);
            Check(ReadResearchCounterLines(fixture.Counter).Length == 2, "cancellation during backoff must not launch another child");
            AssertSyntheticResearchChildrenExited(fixture.Counter);
        }

        using (var temporary = new TemporaryWorkspace())
        {
            var fixture = CreateResearchSupervisorFixture(temporary, "idle");
            string old = ResearchFixture(DateTimeOffset.UtcNow.AddSeconds(-10));
            File.WriteAllText(fixture.Destination, old);
            File.SetLastWriteTimeUtc(fixture.Destination, DateTime.UtcNow.AddSeconds(10));
            using var worker = fixture.Worker;
            await worker.StartAsync(default);
            try
            {
                await WaitResearchAsync(() => File.Exists(fixture.Counter), "idle synthetic child startup");
                await Task.Delay(700);
                Check(fixture.State.GetSnapshot().GetProperty("collection").ValueKind == JsonValueKind.Null,
                    "prior-session snapshot must remain unavailable even when its filesystem timestamp is newer");
                Check(File.ReadAllText(fixture.Destination) == old, "supervisor must not delete or rewrite a previous snapshot");
            }
            finally { await worker.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(8)); }
            AssertSyntheticResearchChildrenExited(fixture.Counter);
        }
    }

    private static (ResearchTelemetryWorker Worker, ResearchTelemetryState State, string Counter, string Destination)
        CreateResearchSupervisorFixture(TemporaryWorkspace temporary, string mode)
    {
        string directory = Path.GetDirectoryName(temporary.DatabasePath)!;
        File.WriteAllText(Path.Combine(directory, "worker.py"), "synthetic-research-supervisor:" + mode);
        string executable = Path.Combine(AppContext.BaseDirectory, "RtxMonitor.Service.Tests.exe");
        Check(File.Exists(executable), "synthetic worker requires the test apphost, never the real collector");
        string destination = Path.Combine(directory, "synthetic-live.json");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RtxMonitor:ResearchTelemetryEnabled"] = "true",
            ["RtxMonitor:ResearchReadersPath"] = directory,
            ["RtxMonitor:ResearchPythonPath"] = executable,
            ["RtxMonitor:ResearchSnapshotPath"] = destination,
        }).Build();
        var state = new ResearchTelemetryState();
        return (new ResearchTelemetryWorker(configuration, state, NullLogger<ResearchTelemetryWorker>.Instance),
            state, destination + ".starts", destination);
    }

    private static bool HasResearchValue(ResearchTelemetryState state, int expected)
    {
        JsonElement report = state.GetSnapshot();
        return report.GetProperty("collection").ValueKind == JsonValueKind.Object &&
            report.GetProperty("collection").GetProperty("readings")[0].GetProperty("value").GetInt32() == expected;
    }

    private static async Task WaitResearchAsync(Func<bool> ready, string description, int seconds = 6)
    {
        var elapsed = Stopwatch.StartNew();
        while (!ready())
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(seconds)) throw new TimeoutException(description);
            await Task.Delay(30);
        }
    }

    private static string[] ReadResearchCounterLines(string counter)
    {
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            try { return File.ReadAllLines(counter); }
            catch (IOException) when (elapsed.Elapsed < TimeSpan.FromSeconds(1))
            {
                // The synthetic child briefly holds this file while appending its start marker.
                Thread.Sleep(10);
            }
        }
    }

    private static void AssertSyntheticResearchChildrenExited(string counter)
    {
        foreach (string row in ReadResearchCounterLines(counter))
            AssertSyntheticResearchProcessExited(int.Parse(row.Split('|')[0]));
    }

    private static void AssertSyntheticResearchProcessExited(int pid)
    {
        try
        {
            using Process child = Process.GetProcessById(pid);
            Check(child.HasExited, "only the fixture's identified process must have exited after stop");
        }
        catch (ArgumentException) { }
    }

    private static async Task<int> RunSyntheticResearchChildAsync(string script, string destination)
    {
        // Mirror the production stdin gate: no collection or descendants may
        // start until the supervisor has assigned this process to its Job.
        char[] gate = new char[1];
        if (await Console.In.ReadAsync(gate.AsMemory()) != 1 || gate[0] != 'G') return 95;
        string specification = await File.ReadAllTextAsync(script);
        const string prefix = "synthetic-research-supervisor:";
        if (!specification.StartsWith(prefix, StringComparison.Ordinal)) return 93;
        string mode = specification[prefix.Length..];
        string counter = destination + ".starts";
        int attempt = File.Exists(counter) ? ReadResearchCounterLines(counter).Length + 1 : 1;
        await File.AppendAllTextAsync(counter, $"{Environment.ProcessId}|{DateTimeOffset.UtcNow:O}{Environment.NewLine}");
        if (mode == "always-exit") return 32;
        if (mode == "idle") { await Task.Delay(Timeout.Infinite); return 0; }
        if (mode is not ("recover" or "invalid-contract" or "orphan")) return 94;
        if (mode == "orphan" && attempt == 1)
        {
            var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "RtxMonitor.Service.Tests.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.ArgumentList.Add("--fake-research-orphan");
            start.ArgumentList.Add(counter + ".orphan");
            start.ArgumentList.Add(counter + ".process.log");
            using Process child = Process.Start(start) ?? throw new InvalidOperationException("synthetic orphan did not start");
            await WaitResearchAsync(() => File.Exists(counter + ".orphan"), "synthetic orphan readiness");
            return 32;
        }
        await Task.Delay(150);
        string publication = ResearchFixture(DateTimeOffset.UtcNow).Replace("\"value\":0.9", "\"value\":" + attempt, StringComparison.Ordinal);
        string pending = destination + ".pending";
        await File.WriteAllTextAsync(pending, publication);
        File.Move(pending, destination, overwrite: true);
        if (attempt == 1)
        {
            await Task.Delay(1000);
            if (mode == "invalid-contract")
            {
                publication = ResearchFixture(DateTimeOffset.UtcNow).Replace("\"settings_writes_requested\":false", "\"settings_writes_requested\":true", StringComparison.Ordinal);
                await File.WriteAllTextAsync(pending, publication);
                File.Move(pending, destination, overwrite: true);
                await Task.Delay(Timeout.Infinite);
                return 0;
            }
            await Console.Error.WriteLineAsync("Synthetic WinError32 process.log cleanup failure; no hardware collector executed.");
            await File.WriteAllTextAsync(counter + ".exit", DateTimeOffset.UtcNow.ToString("O"));
            return 32;
        }
        await Task.Delay(Timeout.Infinite);
        return 0;
    }

    private static async Task<int> RunSyntheticResearchOrphanAsync(string marker, string processLog)
    {
        using var locked = new FileStream(processLog, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        await File.WriteAllTextAsync(marker + ".pending", Environment.ProcessId.ToString());
        File.Move(marker + ".pending", marker);
        await Task.Delay(Timeout.Infinite);
        return 0;
    }
}
