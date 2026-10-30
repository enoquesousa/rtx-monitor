using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace RtxMonitor.Service;

/// <summary>Supervises one serialized, fixed-allowlist research collector.</summary>
public sealed class ResearchTelemetryWorker(
    IConfiguration configuration,
    ResearchTelemetryState state,
    ILogger<ResearchTelemetryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        IConfigurationSection section = configuration.GetSection("RtxMonitor");
        if (!section.GetValue("ResearchTelemetryEnabled", false)) return;
        if (!OperatingSystem.IsWindows())
        {
            state.Unavailable("platform_unavailable", "O perfil revisado exige Windows x64.");
            return;
        }
        string readers = Path.GetFullPath(section["ResearchReadersPath"] ?? Path.Combine(AppContext.BaseDirectory, "research-readers"));
        string python = section["ResearchPythonPath"] ?? "python.exe";
        string destination = Path.GetFullPath(section["ResearchSnapshotPath"] ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RtxMonitor", "research-live.json"));
        string script = Path.Combine(readers, "worker.py");
        if (!File.Exists(script))
        {
            state.Unavailable("reader_unavailable", "O pacote de leitores revisados não foi encontrado.");
            return;
        }
        int failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            state.Unavailable(failures == 0 ? "starting" : "restarting", null);
            AttemptResult result = await RunAttemptAsync(python, script, readers, destination, stoppingToken).ConfigureAwait(false);
            if (stoppingToken.IsCancellationRequested) break;
            if (!result.SafeToRestart)
            {
                state.Unavailable("cleanup_failed", "Não foi possível confirmar a saída do coletor anterior. Uma segunda sessão não será iniciada.");
                return;
            }
            if (result.StablePublication) failures = 0;
            TimeSpan delay = RestartDelay(failures);
            failures = Math.Min(failures + 1, 5);
            state.Unavailable("restarting", $"{result.Error} Nova tentativa em {delay.TotalSeconds:0} segundos.");
            logger.LogWarning("Coletor revisado será reiniciado em {DelaySeconds} segundos; leituras anteriores indisponíveis.", delay.TotalSeconds);
            try { await Task.Delay(delay, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
        state.Unavailable("stopped", "Monitoramento encerrado.");
    }

    internal static TimeSpan RestartDelay(int consecutiveFailures) =>
        TimeSpan.FromSeconds(consecutiveFailures switch { <= 0 => 2, 1 => 4, 2 => 8, 3 => 16, _ => 30 });

    internal static bool ShouldResetBackoff(bool published, TimeSpan runtime, TimeSpan publicationAge) =>
        published && runtime - publicationAge >= TimeSpan.FromSeconds(60) &&
        publicationAge >= TimeSpan.Zero && publicationAge < TimeSpan.FromSeconds(75);

    private sealed record AttemptResult(bool SafeToRestart, bool StablePublication, string Error);

    private async Task<AttemptResult> RunAttemptAsync(string python, string script, string readers,
        string destination, CancellationToken stoppingToken)
    {
        var start = new ProcessStartInfo(python)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = readers,
        };
        start.ArgumentList.Add("-B");
        start.ArgumentList.Add("-c");
        // No reviewed worker code or descendants can execute before Assign.
        // argv remains the same as a direct invocation of worker.py.
        start.ArgumentList.Add("import runpy,sys\nif sys.stdin.read(1) != 'G': raise SystemExit(93)\nsys.argv=sys.argv[1:]\nrunpy.run_path(sys.argv[0],run_name='__main__')");
        start.ArgumentList.Add(script);
        start.ArgumentList.Add("--watch");
        start.ArgumentList.Add("--output");
        start.ArgumentList.Add(destination);
        start.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
        using var process = new Process { StartInfo = start };
        using var drainCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        Task<string>? standardOutput = null;
        Task<string>? standardError = null;
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        DateTimeOffset lastPublication = startedAt;
        DateTimeOffset lastFileWrite = DateTimeOffset.MinValue;
        bool published = false;
        bool started = false;
        bool assigned = false;
        ResearchAttemptJob? attemptJob = null;
        bool safeToRestart = true;
        DateTimeOffset endedAt = startedAt;
        string failure = "Coletor encerrou sem uma publicação atual.";
        try
        {
            stoppingToken.ThrowIfCancellationRequested();
            attemptJob = ResearchAttemptJob.Create();
            if (!process.Start()) throw new InvalidOperationException("O processo de coleta não iniciou.");
            started = true;
            standardOutput = DrainBoundedAsync(process.StandardOutput, drainCancellation.Token);
            standardError = DrainBoundedAsync(process.StandardError, drainCancellation.Token);
            attemptJob.Assign(process);
            assigned = true;
            stoppingToken.ThrowIfCancellationRequested();
            await process.StandardInput.WriteAsync("G".AsMemory(), stoppingToken).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(stoppingToken).ConfigureAwait(false);
            process.StandardInput.Close();
            logger.LogInformation("Coletor revisado iniciado no processo {ProcessId}; somente leitura, sem aquisição por HTTP.", process.Id);
            while (!stoppingToken.IsCancellationRequested && !process.HasExited)
            {
                if (File.Exists(destination))
                {
                    DateTimeOffset changed = File.GetLastWriteTimeUtc(destination);
                    if (changed >= startedAt && changed != lastFileWrite)
                    {
                        var file = new FileInfo(destination);
                        if (file.Length > 24 * 1024 * 1024) throw new InvalidDataException("Relatório excede 24 MiB.");
                        await using FileStream stream = new(destination, FileMode.Open, FileAccess.Read,
                            FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.Asynchronous);
                        using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: stoppingToken).ConfigureAwait(false);
                        lastFileWrite = changed;
                        // A previous process can leave a still-fresh file behind. Its
                        // timestamp must never become a new session's live reading.
                        if (document.RootElement.TryGetProperty("updated_at", out JsonElement stamp) &&
                            stamp.ValueKind == JsonValueKind.String && stamp.TryGetDateTimeOffset(out DateTimeOffset acquired) &&
                            acquired < startedAt) continue;
                        state.Record(document.RootElement);
                        published = true;
                        lastPublication = DateTimeOffset.UtcNow;
                    }
                }
                if (DateTimeOffset.UtcNow - lastPublication > TimeSpan.FromSeconds(75))
                    throw new TimeoutException("O coletor ficou 75 segundos sem publicar. A sessão foi encerrada.");
                await Task.Delay(500, stoppingToken).ConfigureAwait(false);
            }
            if (!stoppingToken.IsCancellationRequested)
            {
                failure = $"Coletor encerrou com código {process.ExitCode}.";
                // Invalidate before waiting for pipes: a descendant may still
                // hold them open after its parent has exited.
                state.Unavailable("worker_exited", failure);
                logger.LogWarning("Coletor revisado encerrou com código {ExitCode}.", process.ExitCode);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            state.Unavailable("stopped", "Monitoramento encerrado.");
        }
        catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or System.ComponentModel.Win32Exception or JsonException or TimeoutException)
        {
            failure = error.Message;
            state.Unavailable("worker_unavailable", error.Message);
            logger.LogWarning(error, "Coletor revisado indisponível; valores anteriores foram retirados.");
        }
        finally
        {
            endedAt = DateTimeOffset.UtcNow;
            if (stoppingToken.IsCancellationRequested)
                state.Unavailable("stopped", "Monitoramento encerrado.");
            try
            {
                // Assignment can fail while the bootstrap is waiting. In that
                // case only this gated root exists outside the attempt's Job.
                if (started && !assigned && !process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                }
                // HasExited of the Python root cannot prove that its readers
                // released process.log. Require the entire Job to be empty.
                safeToRestart = attemptJob is null ? !started :
                    attemptJob.TerminateAndWait(TimeSpan.FromSeconds(5)) && (!started || process.HasExited);
            }
            catch (Exception error) when (error is InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
            {
                safeToRestart = false;
                logger.LogWarning(error, "Não foi possível encerrar normalmente o processo revisado.");
            }
            attemptJob?.Dispose();
            // Always bounded, including inherited pipes and service cancellation.
            drainCancellation.Cancel();
            if (standardOutput is not null) _ = await FinishDrainAsync(standardOutput).ConfigureAwait(false);
            if (standardError is not null)
            {
                string error = await FinishDrainAsync(standardError).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(error)) failure += " " + error;
            }
        }
        return new AttemptResult(safeToRestart,
            ShouldResetBackoff(published, endedAt - startedAt, endedAt - lastPublication), failure);
    }

    private static async Task<string> FinishDrainAsync(Task<string> task)
    {
        try { return await task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch (TimeoutException) { return "Saída de diagnóstico não encerrou no prazo."; }
    }

    private static async Task<string> DrainBoundedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var output = new StringBuilder();
        char[] buffer = new char[2048];
        try
        {
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) != 0)
            {
                int remaining = 16384 - output.Length;
                if (remaining > 0) output.Append(buffer, 0, Math.Min(count, remaining));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (IOException) { }
        return output.ToString();
    }
}
