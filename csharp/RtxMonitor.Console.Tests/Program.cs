using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using RtxMonitor.ConsoleApp;

namespace RtxMonitor.Console.Tests;

internal static class Program
{
    private const string GpuUuid = "GPU-11111111-2222-3333-4444-555555555555";
    private static readonly TimeSpan StartupDeadline = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan RequestDeadline = TimeSpan.FromMilliseconds(300);
    private static int failures;

    private static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--fake-private-worker")
        {
            return await RunFakeWorkerAsync(args[1], args[2], args[3]);
        }

        await RunTestAsync("thermal and voltage samples, request sequencing, graceful disposal", TestSamplesAsync);
        await RunTestAsync("startup watchdog terminates hung child", TestStartupTimeoutAsync);
        await RunTestAsync("startup cancellation terminates child", TestStartupCancellationAsync);
        await RunTestAsync("pre-canceled startup never launches child", TestPreCanceledStartupAsync);
        await RunTestAsync("request watchdog rejects late result and permanently closes worker", TestRequestTimeoutAsync);
        await RunTestAsync("active and pre-canceled requests terminate child", TestRequestCancellationAsync);
        await RunTestAsync("idle interval does not count towards request deadline", TestLongIdleAsync);
        await RunTestAsync("concurrent requests are rejected without interleaving", TestConcurrentRequestsAsync);
        await RunTestAsync("disposal interrupts pending request and remains idempotent", TestActiveDisposalAsync);
        await RunTestAsync("uncooperative stop is killed within bounded deadline", TestUncooperativeStopAsync);
        await RunTestAsync("stderr flooding cannot block handshake", TestStderrFloodAsync);
        await RunTestAsync("EOF diagnostics capture at most 4 KiB", TestStderrBoundAsync);
        foreach (string mode in new[] { "startup-eof", "startup-invalid", "startup-oversized", "startup-bad-kind", "startup-bad-version", "startup-bad-operation", "startup-bad-uuid", "startup-bad-interval", "startup-error" })
        {
            await RunTestAsync(mode, () => TestInvalidStartupAsync(mode));
        }
        foreach (string mode in new[] { "read-eof", "read-invalid", "read-oversized", "read-bad-kind", "read-bad-id", "read-error", "read-duplicate", "read-bad-source", "read-bad-index", "read-bad-uuid", "read-bad-timestamp", "read-bad-measurement", "read-bad-status", "read-partial" })
        {
            await RunTestAsync(mode, () => TestInvalidResponseAsync(mode));
        }
        System.Console.WriteLine(failures == 0 ? "RtxMonitor.Console tests passed (35 cases, no hardware)" : $"RtxMonitor.Console tests failed: {failures}");
        return failures == 0 ? 0 : 1;
    }

    private static async Task TestSamplesAsync()
    {
        foreach (string operation in new[] { "thermal", "voltage" })
        {
            using var fixture = new FakeFixture("success", operation);
            PrivateWorkerClient client = await fixture.StartAsync();
            try
            {
                Assert(client.GpuUuid == GpuUuid && client.GpuIndex == 0, "handshake identity");
                Assert(client.MinimumIntervalMilliseconds == 100 && client.AcquisitionTimeoutMilliseconds == 2000, "handshake policy");
                for (int id = 1; id <= 2; id++)
                {
                    using JsonDocument sample = JsonDocument.Parse(await client.ReadSampleAsync(CancellationToken.None));
                    Assert(sample.RootElement.GetProperty("sample_sequence").GetInt64() == id, "request id increments");
                    Assert(sample.RootElement.GetProperty("source_kind").GetString() ==
                        (operation == "thermal" ? "nvapi_thermal_channel" : "nvapi_voltage_status"), "sample operation");
                }
            }
            finally
            {
                await client.DisposeAsync();
            }
            await fixture.AssertChildDeadAsync();
            Assert(File.Exists(fixture.StoppedPath), "graceful stop was delivered");
            await client.DisposeAsync();
            await ExpectAsync<ObjectDisposedException>(() => client.ReadSampleAsync(CancellationToken.None));
        }
    }

    private static async Task TestStartupTimeoutAsync()
    {
        using var fixture = new FakeFixture("startup-hang");
        Stopwatch timer = Stopwatch.StartNew();
        await ExpectAsync<TimeoutException>(() => fixture.StartAsync(startupTimeout: TimeSpan.FromMilliseconds(700)));
        Assert(timer.Elapsed < TimeSpan.FromSeconds(4), "startup timeout is bounded");
        await fixture.AssertChildDeadAsync();
    }

    private static async Task TestStartupCancellationAsync()
    {
        using var fixture = new FakeFixture("startup-hang");
        using var cancellation = new CancellationTokenSource();
        Task<PrivateWorkerClient> starting = fixture.StartAsync(cancellation.Token);
        await fixture.WaitForChildAsync();
        cancellation.Cancel();
        await ExpectAsync<OperationCanceledException>(() => starting);
        await fixture.AssertChildDeadAsync();
    }

    private static async Task TestPreCanceledStartupAsync()
    {
        using var fixture = new FakeFixture("success");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await ExpectAsync<OperationCanceledException>(() => fixture.StartAsync(cancellation.Token));
        Assert(!File.Exists(fixture.PidPath), "pre-canceled startup created no process");
    }

    private static async Task TestRequestTimeoutAsync()
    {
        foreach (string mode in new[] { "read-hang", "read-late" })
        {
            using var fixture = new FakeFixture(mode);
            await using PrivateWorkerClient client = await fixture.StartAsync();
            Stopwatch timer = Stopwatch.StartNew();
            await ExpectAsync<TimeoutException>(() => client.ReadSampleAsync(CancellationToken.None));
            Assert(timer.Elapsed < TimeSpan.FromSeconds(3), "request watchdog is bounded");
            await fixture.AssertChildDeadAsync();
            await ExpectAsync<InvalidOperationException>(() => client.ReadSampleAsync(CancellationToken.None));
            Assert(!File.Exists(fixture.LatePath), "timed out child cannot emit a late sample");
        }
    }

    private static async Task TestRequestCancellationAsync()
    {
        foreach (bool preCanceled in new[] { false, true })
        {
            using var fixture = new FakeFixture("read-hang");
            await using PrivateWorkerClient client = await fixture.StartAsync(requestTimeout: TimeSpan.FromSeconds(10));
            using var cancellation = new CancellationTokenSource();
            if (preCanceled)
            {
                cancellation.Cancel();
            }
            else
            {
                cancellation.CancelAfter(100);
            }
            await ExpectAsync<OperationCanceledException>(() => client.ReadSampleAsync(cancellation.Token));
            await fixture.AssertChildDeadAsync();
        }
    }

    private static async Task TestLongIdleAsync()
    {
        using var fixture = new FakeFixture("success");
        await using PrivateWorkerClient client = await fixture.StartAsync();
        await client.ReadSampleAsync(CancellationToken.None);
        await Task.Delay(RequestDeadline + RequestDeadline);
        using JsonDocument second = JsonDocument.Parse(await client.ReadSampleAsync(CancellationToken.None));
        Assert(second.RootElement.GetProperty("sample_sequence").GetInt64() == 2, "worker remains healthy while idle");
    }

    private static async Task TestConcurrentRequestsAsync()
    {
        using var fixture = new FakeFixture("read-hang");
        await using PrivateWorkerClient client = await fixture.StartAsync(requestTimeout: TimeSpan.FromSeconds(10));
        using var cancellation = new CancellationTokenSource();
        Task<string> first = client.ReadSampleAsync(cancellation.Token);
        await ExpectAsync<InvalidOperationException>(() => client.ReadSampleAsync(CancellationToken.None));
        cancellation.Cancel();
        await ExpectAsync<OperationCanceledException>(() => first);
        await fixture.AssertChildDeadAsync();
    }

    private static async Task TestActiveDisposalAsync()
    {
        using var fixture = new FakeFixture("read-hang");
        PrivateWorkerClient client = await fixture.StartAsync(requestTimeout: TimeSpan.FromSeconds(10));
        Task<string> pending = client.ReadSampleAsync(CancellationToken.None);
        await client.DisposeAsync();
        await ExpectAsync<ObjectDisposedException>(() => pending);
        await client.DisposeAsync();
        await fixture.AssertChildDeadAsync();
    }

    private static async Task TestUncooperativeStopAsync()
    {
        using var fixture = new FakeFixture("stop-hang");
        PrivateWorkerClient client = await fixture.StartAsync();
        Stopwatch timer = Stopwatch.StartNew();
        await client.DisposeAsync();
        Assert(timer.Elapsed < TimeSpan.FromSeconds(5), "forced stop remains bounded");
        await fixture.AssertChildDeadAsync();
    }

    private static async Task TestStderrFloodAsync()
    {
        using var fixture = new FakeFixture("stderr-flood");
        await using PrivateWorkerClient client = await fixture.StartAsync();
        await client.ReadSampleAsync(CancellationToken.None);
    }

    private static async Task TestStderrBoundAsync()
    {
        using var fixture = new FakeFixture("stderr-eof");
        Exception exception = await ExpectAsync<InvalidDataException>(() => fixture.StartAsync());
        Assert(exception.Message.Length < 4300, "stderr diagnostic is bounded to 4 KiB plus prefix");
        await fixture.AssertChildDeadAsync();
    }

    private static async Task TestInvalidStartupAsync(string mode)
    {
        using var fixture = new FakeFixture(mode);
        Exception exception = await ExpectAsync<Exception>(() => fixture.StartAsync());
        Assert(exception is not TimeoutException, "invalid startup must fail before timeout");
        await fixture.AssertChildDeadAsync();
    }

    private static async Task TestInvalidResponseAsync(string mode)
    {
        using var fixture = new FakeFixture(mode);
        await using PrivateWorkerClient client = await fixture.StartAsync();
        Exception exception = await ExpectAsync<Exception>(() => client.ReadSampleAsync(CancellationToken.None));
        Assert(mode == "read-partial" || exception is not TimeoutException, "invalid response must fail before timeout");
        await fixture.AssertChildDeadAsync();
        await ExpectAsync<InvalidOperationException>(() => client.ReadSampleAsync(CancellationToken.None));
    }

    private static async Task<T> ExpectAsync<T>(Func<Task> action) where T : Exception
    {
        try
        {
            await action();
        }
        catch (T exception)
        {
            return exception;
        }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static void Assert(bool condition, string description)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Assertion failed: {description}");
        }
    }

    private static async Task RunTestAsync(string name, Func<Task> test)
    {
        try
        {
            await test();
            System.Console.WriteLine($"PASS {name}");
        }
        catch (Exception exception)
        {
            failures++;
            System.Console.Error.WriteLine($"FAIL {name}: {exception}");
        }
    }

    private sealed class FakeFixture : IDisposable
    {
        private readonly string mode;
        private readonly string operation;
        private readonly string directory;

        internal FakeFixture(string mode, string operation = "thermal")
        {
            this.mode = mode;
            this.operation = operation;
            directory = Path.Combine(Path.GetTempPath(), "rtxmon-worker-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
        }

        internal string PidPath => Path.Combine(directory, "child.pid");
        internal string StoppedPath => PidPath + ".stopped";
        internal string LatePath => PidPath + ".late";

        internal Task<PrivateWorkerClient> StartAsync(CancellationToken cancellation = default, TimeSpan? startupTimeout = null, TimeSpan? requestTimeout = null)
        {
            string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Current executable unavailable.");
            var info = new ProcessStartInfo(executable);
            if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
            {
                info.ArgumentList.Add(typeof(Program).Assembly.Location);
            }
            info.ArgumentList.Add("--fake-private-worker");
            info.ArgumentList.Add(mode);
            info.ArgumentList.Add(operation);
            info.ArgumentList.Add(PidPath);
            return PrivateWorkerClient.StartAsync(info, operation, cancellation,
                startupTimeout ?? StartupDeadline, requestTimeout ?? RequestDeadline);
        }

        internal async Task WaitForChildAsync()
        {
            Stopwatch timer = Stopwatch.StartNew();
            while (!File.Exists(PidPath) && timer.Elapsed < StartupDeadline)
            {
                await Task.Delay(20);
            }
            Assert(File.Exists(PidPath), $"child wrote its PID ({mode})");
        }

        internal async Task AssertChildDeadAsync()
        {
            await WaitForChildAsync();
            int pid = int.Parse(await File.ReadAllTextAsync(PidPath), CultureInfo.InvariantCulture);
            Stopwatch timer = Stopwatch.StartNew();
            while (IsAlive(pid) && timer.Elapsed < TimeSpan.FromSeconds(2))
            {
                await Task.Delay(20);
            }
            Assert(!IsAlive(pid), $"owned child PID {pid} terminated ({mode})");
        }

        private static bool IsAlive(int pid)
        {
            try
            {
                using Process child = Process.GetProcessById(pid);
                return !child.HasExited;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        public void Dispose()
        {
            // A failed assertion must not leave a simulated hang running after the test executable exits.
            if (File.Exists(PidPath) && int.TryParse(File.ReadAllText(PidPath), out int pid) && IsAlive(pid))
            {
                using Process child = Process.GetProcessById(pid);
                child.Kill(entireProcessTree: true);
                Assert(child.WaitForExit(2000), $"test fixture cleanup failed for PID {pid}");
            }
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<int> RunFakeWorkerAsync(string mode, string operation, string pidPath)
    {
        await File.WriteAllTextAsync(pidPath, Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        if (mode == "startup-hang")
        {
            await Task.Delay(Timeout.InfiniteTimeSpan);
        }
        if (mode is "stderr-flood" or "stderr-eof")
        {
            await System.Console.Error.WriteAsync(new string('x', 64 * 1024));
            await System.Console.Error.FlushAsync();
        }
        if (mode is "startup-eof" or "stderr-eof")
        {
            return 0;
        }
        if (mode == "startup-invalid")
        {
            await SendAsync("not-json");
            await Task.Delay(Timeout.InfiniteTimeSpan);
        }
        if (mode == "startup-oversized")
        {
            await SendAsync(new string('x', 16385));
            await Task.Delay(Timeout.InfiniteTimeSpan);
        }
        if (mode == "startup-error")
        {
            await SendAsync(JsonSerializer.Serialize(new { protocol_version = 1, kind = "error", request_id = 0, status = "unsupported", message = "Fake startup failure" }));
            await Task.Delay(Timeout.InfiniteTimeSpan);
        }
        await SendAsync(JsonSerializer.Serialize(new
        {
            protocol_version = mode == "startup-bad-version" ? 2 : 1,
            kind = mode == "startup-bad-kind" ? "sample" : "ready",
            operation = mode == "startup-bad-operation" ? "other" : operation,
            gpu_uuid = mode == "startup-bad-uuid" ? "GPU-unknown" : GpuUuid,
            gpu_index = 0,
            minimum_interval_ms = mode == "startup-bad-interval" ? 0 : 100,
            acquisition_timeout_ms = 2000,
        }));
        while (await System.Console.In.ReadLineAsync() is string command)
        {
            if (command == "stop")
            {
                if (mode == "stop-hang")
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan);
                }
                await File.WriteAllTextAsync(pidPath + ".stopped", "stop");
                return 0;
            }
            if (!command.StartsWith("sample ", StringComparison.Ordinal) ||
                !long.TryParse(command.AsSpan(7), NumberStyles.None, CultureInfo.InvariantCulture, out long id) || id < 1)
            {
                return 2;
            }
            if (mode == "read-hang")
            {
                await Task.Delay(Timeout.InfiniteTimeSpan);
            }
            if (mode == "read-late")
            {
                await Task.Delay(1200);
                await File.WriteAllTextAsync(pidPath + ".late", "late");
            }
            if (mode == "read-eof")
            {
                return 0;
            }
            if (mode == "read-partial")
            {
                await System.Console.Out.WriteAsync("{\"protocol_version\":1");
                await System.Console.Out.FlushAsync();
                await Task.Delay(Timeout.InfiniteTimeSpan);
            }
            if (mode is "read-invalid" or "read-oversized" or "read-error" or "read-duplicate")
            {
                string malformed = mode switch
                {
                    "read-invalid" => "not-json",
                    "read-oversized" => new string('x', 16385),
                    "read-error" => JsonSerializer.Serialize(new { protocol_version = 1, kind = "error", request_id = id, status = "timeout", message = "Fake acquisition timeout" }),
                    _ => "{\"protocol_version\":1,\"protocol_version\":1,\"kind\":\"sample\"}",
                };
                await SendAsync(malformed);
                continue;
            }
            var timestamp = DateTimeOffset.UtcNow;
            var payload = new Dictionary<string, object?>
            {
                ["schema_version"] = 1,
                ["source_kind"] = mode == "read-bad-source" ? "other" : operation == "thermal" ? "nvapi_thermal_channel" : "nvapi_voltage_status",
                ["gpu_index"] = mode == "read-bad-index" ? 1 : 0,
                ["gpu_uuid"] = mode == "read-bad-uuid" ? "GPU-aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee" : GpuUuid,
                ["captured_at_unix_ms"] = mode == "read-bad-timestamp" ? -1 : timestamp.ToUnixTimeMilliseconds(),
                ["captured_at_utc"] = timestamp.ToString("O", CultureInfo.InvariantCulture),
                ["monotonic_ns"] = Stopwatch.GetTimestamp(),
                ["monotonic_frequency_hz"] = Stopwatch.Frequency,
                ["native_status"] = mode == "read-bad-status" ? -1 : 0,
                ["sample_sequence"] = id,
            };
            if (operation == "thermal")
            {
                payload["gpu_die_temperature_c"] = mode == "read-bad-measurement" ? "invalid" : 50.5;
                payload["gpu_hotspot_temperature_c"] = 60.5;
                payload["delta_c"] = 10.0;
            }
            else
            {
                payload["gpu_core_voltage_microvolts"] = 850000;
                payload["gpu_core_voltage_v"] = 0.85;
            }
            await SendAsync(JsonSerializer.Serialize(new
            {
                protocol_version = 1,
                kind = mode == "read-bad-kind" ? "ready" : "sample",
                request_id = mode == "read-bad-id" ? id + 1 : id,
                payload,
            }));
        }
        return 0;
    }

    private static async Task SendAsync(string value)
    {
        await System.Console.Out.WriteLineAsync(value);
        await System.Console.Out.FlushAsync();
    }
}
