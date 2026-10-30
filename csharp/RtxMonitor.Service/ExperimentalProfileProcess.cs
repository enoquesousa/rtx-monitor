using System.Diagnostics;
using System.Text;

namespace RtxMonitor.Service;

/// <summary>Contains profile discovery, including native initialization and disposal, in a bounded child.</summary>
internal static class ExperimentalProfileProcess
{
    internal const int MaximumOutputBytes = 16 * 1024;
    private static readonly TimeSpan MaximumDeadline = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CleanupDeadline = TimeSpan.FromSeconds(2);
    private static readonly UTF8Encoding ProtocolEncoding = new(false, true);

    internal static Task<ExperimentalProfileObservation> CaptureAsync(string gpuUuid, CancellationToken cancellationToken) =>
        CaptureAsync(CreateStartInfo(gpuUuid), gpuUuid, MaximumDeadline, cancellationToken);

    private static ProcessStartInfo CreateStartInfo(string gpuUuid)
    {
        string executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("Executável do serviço indisponível.");
        var info = new ProcessStartInfo(executable);
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add(typeof(ExperimentalProfileProcess).Assembly.Location);
        info.ArgumentList.Add("--private-profile-worker");
        info.ArgumentList.Add("--gpu-uuid");
        info.ArgumentList.Add(gpuUuid);
        return info;
    }

    // Injectable process command and a shorter deadline are reserved for synthetic process tests.
    internal static async Task<ExperimentalProfileObservation> CaptureAsync(
        ProcessStartInfo info, string gpuUuid, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(info);
        ExperimentalProfileProtocol.ValidateUuid(gpuUuid);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(info.FileName) || !string.IsNullOrEmpty(info.Arguments))
            throw new ArgumentException("O diagnóstico requer executável explícito e ArgumentList.", nameof(info));
        if (timeout <= TimeSpan.Zero || timeout > MaximumDeadline)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        info.RedirectStandardInput = true;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.StandardOutputEncoding = ProtocolEncoding;
        info.StandardErrorEncoding = ProtocolEncoding;

        using var process = new Process { StartInfo = info };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        bool started = false;
        Task<string>? output = null;
        Task? stderr = null;
        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            started = process.Start();
            if (!started) throw new InvalidOperationException("Não foi possível iniciar o diagnóstico experimental.");
            process.StandardInput.Close();
            output = ReadOutputAsync(process.StandardOutput.BaseStream, deadline.Token);
            stderr = DrainStderrAsync(process.StandardError.BaseStream, deadline.Token);
            Task exited = process.WaitForExitAsync(deadline.Token);
            // Await output first so an oversized stream is rejected immediately, even if its child keeps running.
            string payload = await output.WaitAsync(deadline.Token).ConfigureAwait(false);
            await Task.WhenAll(exited, stderr).WaitAsync(deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            ExperimentalProfileObservation observation = ExperimentalProfileProtocol.Parse(payload, gpuUuid);
            if (process.ExitCode != 0)
                throw new InvalidDataException("O diagnóstico experimental encerrou sem confirmar sucesso.");
            return observation;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("O diagnóstico experimental excedeu o prazo do processo supervisionado.");
        }
        finally
        {
            deadline.Cancel();
            if (started)
            {
                await EnsureExitedAsync(process).ConfigureAwait(false);
                try
                {
                    await Task.WhenAll(ObserveAsync(output), ObserveAsync(stderr))
                        .WaitAsync(CleanupDeadline).ConfigureAwait(false);
                }
                catch (TimeoutException error)
                {
                    throw new InvalidOperationException(
                        $"A drenagem do diagnóstico experimental PID {process.Id} não encerrou no prazo.", error);
                }
            }
        }
    }

    private static async Task<string> ReadOutputAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[1024];
        using var output = new MemoryStream();
        while (true)
        {
            int count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0) return ProtocolEncoding.GetString(output.GetBuffer(), 0, checked((int)output.Length));
            if (output.Length + count > MaximumOutputBytes)
                throw new InvalidDataException("O diagnóstico experimental excedeu 16 KiB de saída.");
            output.Write(buffer, 0, count);
        }
    }

    private static async Task DrainStderrAsync(Stream stream, CancellationToken cancellationToken)
    {
        // Diagnostics cannot accumulate memory or block the fixed stdout protocol.
        byte[] buffer = new byte[1024];
        while (await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false) != 0) { }
    }

    private static async Task EnsureExitedAsync(Process process)
    {
        if (process.HasExited) return;
        try { process.Kill(); }
        catch (InvalidOperationException) when (process.HasExited) { }
        catch (Exception error)
        {
            throw new InvalidOperationException($"Não foi possível encerrar o diagnóstico experimental PID {process.Id}.", error);
        }
        try { await process.WaitForExitAsync().WaitAsync(CleanupDeadline).ConfigureAwait(false); }
        catch (TimeoutException error)
        {
            throw new InvalidOperationException($"O encerramento do diagnóstico experimental PID {process.Id} não foi confirmado.", error);
        }
        if (!process.HasExited)
            throw new InvalidOperationException($"O diagnóstico experimental PID {process.Id} permanece ativo.");
    }

    private static async Task ObserveAsync(Task? task)
    {
        if (task is null) return;
        try { await task.ConfigureAwait(false); }
        catch { /* The acquisition path reports the original stream/protocol failure. */ }
    }
}
