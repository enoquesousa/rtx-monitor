using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using RtxMonitor.Managed;

namespace RtxMonitor.ConsoleApp;

/// <summary>Owns one isolated private-acquisition process. A failed worker is never reused.</summary>
internal sealed class PrivateWorkerClient : IAsyncDisposable
{
    private const int MaximumLineBytes = 16 * 1024;
    private const int MaximumStderrBytes = 4 * 1024;
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(2);
    private static readonly UTF8Encoding ProtocolEncoding = new(false, true);
    private readonly Process process;
    private readonly string operation;
    private readonly TimeSpan requestTimeout;
    private readonly CancellationTokenSource lifetime = new();
    private readonly object cleanupLock = new();
    private readonly object stderrLock = new();
    private readonly byte[] stderr = new byte[MaximumStderrBytes];
    private readonly byte[] readBuffer = new byte[1024];
    private Task? stderrTask;
    private Task<string?>? cleanupTask;
    private int readOffset;
    private int readCount;
    private int stderrCount;
    private int requestActive;
    private int disposed;
    private bool broken;
    private bool started;
    private long requestId;

    private PrivateWorkerClient(Process process, string operation, TimeSpan requestTimeout)
    {
        this.process = process;
        this.operation = operation;
        this.requestTimeout = requestTimeout;
    }

    internal string GpuUuid { get; private set; } = string.Empty;
    internal uint GpuIndex { get; private set; }
    internal int MinimumIntervalMilliseconds { get; private set; }
    internal int AcquisitionTimeoutMilliseconds { get; private set; }

    internal static async Task<PrivateWorkerClient> StartAsync(
        ProcessStartInfo info,
        string operation,
        CancellationToken cancellationToken,
        TimeSpan? startupTimeout = null,
        TimeSpan? requestTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(info);
        cancellationToken.ThrowIfCancellationRequested();
        if (operation is not ("thermal" or "voltage"))
        {
            throw new ArgumentException("Operação privada inválida.", nameof(operation));
        }
        if (string.IsNullOrWhiteSpace(info.FileName) || !string.IsNullOrEmpty(info.Arguments))
        {
            throw new ArgumentException("O worker requer executável explícito e argumentos em ArgumentList.", nameof(info));
        }
        TimeSpan startupDeadline = ValidateTimeout(startupTimeout ?? TimeSpan.FromSeconds(10), nameof(startupTimeout));
        TimeSpan sampleDeadline = ValidateTimeout(requestTimeout ?? TimeSpan.FromSeconds(5), nameof(requestTimeout));
        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        info.RedirectStandardInput = true;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.StandardInputEncoding = ProtocolEncoding;
        info.StandardOutputEncoding = ProtocolEncoding;
        info.StandardErrorEncoding = ProtocolEncoding;

        var client = new PrivateWorkerClient(new Process { StartInfo = info }, operation, sampleDeadline);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(startupDeadline);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            client.started = client.process.Start();
            if (!client.started)
            {
                throw new InvalidOperationException("Não foi possível iniciar o worker privado.");
            }
            client.stderrTask = client.DrainStderrAsync();
            string line = await client.ReadBoundedLineAsync(deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
            using JsonDocument document = ParseEnvelope(line);
            JsonElement envelope = document.RootElement;
            string kind = RequiredString(envelope, "kind");
            if (kind == "error")
            {
                ThrowWorkerError(envelope, 0);
            }
            Require(kind == "ready", "handshake inesperado");
            Require(RequiredString(envelope, "operation") == operation, "operação divergente no handshake");
            string uuid = RequiredString(envelope, "gpu_uuid");
            Require(uuid.StartsWith("GPU-", StringComparison.Ordinal) &&
                Guid.TryParseExact(uuid.AsSpan(4), "D", out Guid parsedUuid) && parsedUuid != Guid.Empty,
                "UUID inválido no handshake");
            client.GpuUuid = uuid;
            client.GpuIndex = RequiredUInt32(envelope, "gpu_index");
            client.MinimumIntervalMilliseconds = RequiredPositiveInt32(envelope, "minimum_interval_ms");
            client.AcquisitionTimeoutMilliseconds = RequiredPositiveInt32(envelope, "acquisition_timeout_ms");
            deadline.Token.ThrowIfCancellationRequested();
            return client;
        }
        catch (Exception exception)
        {
            await client.FailAsync(NormalizeDeadlineException(exception, cancellationToken, startupDeadline, "inicialização"))
                .ConfigureAwait(false);
            throw;
        }
    }

    internal async Task<string> ReadSampleAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref requestActive, 1, 0) != 0)
        {
            throw new InvalidOperationException("O worker privado já possui uma aquisição em andamento.");
        }
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            if (broken)
            {
                throw new InvalidOperationException("O worker privado falhou e não pode ser reutilizado.");
            }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
            deadline.CancelAfter(requestTimeout);
            try
            {
                deadline.Token.ThrowIfCancellationRequested();
                long id = checked(++requestId);
                await process.StandardInput.WriteLineAsync($"sample {id.ToString(CultureInfo.InvariantCulture)}".AsMemory(), deadline.Token)
                    .WaitAsync(deadline.Token).ConfigureAwait(false);
                await process.StandardInput.FlushAsync(deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
                string line = await ReadBoundedLineAsync(deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
                using JsonDocument document = ParseEnvelope(line);
                JsonElement envelope = document.RootElement;
                string kind = RequiredString(envelope, "kind");
                if (kind == "error")
                {
                    ThrowWorkerError(envelope, id);
                }
                Require(kind == "sample", "tipo de resposta inesperado");
                Require(RequiredInt64(envelope, "request_id") == id, "identificador de resposta divergente");
                Require(envelope.TryGetProperty("payload", out JsonElement payload) && payload.ValueKind == JsonValueKind.Object,
                    "payload ausente ou inválido");
                ValidateSample(payload);
                deadline.Token.ThrowIfCancellationRequested();
                return payload.GetRawText();
            }
            catch (Exception exception)
            {
                await FailAsync(NormalizeDeadlineException(exception, cancellationToken, requestTimeout, "aquisição", Volatile.Read(ref disposed) != 0))
                    .ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            Volatile.Write(ref requestActive, 0);
        }
    }

    public async ValueTask DisposeAsync()
    {
        bool firstDisposal = Interlocked.Exchange(ref disposed, 1) == 0;
        if (firstDisposal)
        {
            lifetime.Cancel();
        }
        string? failure = await EnsureStoppedAsync(!broken && Volatile.Read(ref requestActive) == 0).ConfigureAwait(false);
        if (failure is not null)
        {
            throw new InvalidOperationException(failure);
        }
    }

    private static TimeSpan ValidateTimeout(TimeSpan timeout, string name)
    {
        if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(name, "O prazo deve ser positivo e finito.");
        }
        return timeout;
    }

    private static Exception NormalizeDeadlineException(Exception exception, CancellationToken caller, TimeSpan timeout, string phase, bool disposing = false)
    {
        if (exception is OperationCanceledException)
        {
            if (caller.IsCancellationRequested)
            {
                return new OperationCanceledException("Worker privado cancelado.", exception, caller);
            }
            return disposing ? new ObjectDisposedException(nameof(PrivateWorkerClient), "O worker privado foi encerrado durante a aquisição.")
                : new TimeoutException($"Worker privado excedeu o prazo de {phase} ({timeout.TotalMilliseconds:0} ms).", exception);
        }
        return exception;
    }

    private async Task FailAsync(Exception exception)
    {
        broken = true;
        lifetime.Cancel();
        string? cleanupFailure = await EnsureStoppedAsync(false).ConfigureAwait(false);
        if (cleanupFailure is not null)
        {
            throw new InvalidOperationException($"{exception.Message} {cleanupFailure}", exception);
        }
        ExceptionDispatchInfo.Capture(exception).Throw();
    }

    private Task<string?> EnsureStoppedAsync(bool graceful)
    {
        lock (cleanupLock)
        {
            return cleanupTask ??= StopCoreAsync(graceful);
        }
    }

    private async Task<string?> StopCoreAsync(bool graceful)
    {
        string? failure = null;
        CancellationTokenSource? cleanupDeadline = null;
        try
        {
            if (started && !process.HasExited)
            {
                if (graceful)
                {
                    using var stopDeadline = new CancellationTokenSource(CleanupTimeout);
                    try
                    {
                        await process.StandardInput.WriteLineAsync("stop".AsMemory(), stopDeadline.Token).WaitAsync(stopDeadline.Token).ConfigureAwait(false);
                        await process.StandardInput.FlushAsync(stopDeadline.Token).WaitAsync(stopDeadline.Token).ConfigureAwait(false);
                        await process.WaitForExitAsync(stopDeadline.Token).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is OperationCanceledException or IOException or InvalidOperationException)
                    {
                        // A worker that cannot stop cooperatively must be terminated below.
                    }
                }
                if (!process.HasExited)
                {
                    cleanupDeadline = new CancellationTokenSource(CleanupTimeout);
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                    {
                        if (!process.HasExited)
                        {
                            failure = $"Não foi possível encerrar o worker privado PID {process.Id}: {exception.Message}";
                        }
                    }
                    try
                    {
                        await process.WaitForExitAsync(cleanupDeadline.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        failure ??= $"O encerramento do worker privado PID {process.Id} não foi confirmado em 2000 ms.";
                    }
                }
            }
        }
        catch (Exception exception)
        {
            failure ??= $"Falha ao encerrar o worker privado: {exception.Message}";
        }
        finally
        {
            lifetime.Cancel();
            // Disposing the handles also releases pending pipe reads. Never wait unboundedly for stderr.
            process.Dispose();
            if (stderrTask is not null)
            {
                cleanupDeadline ??= new CancellationTokenSource(CleanupTimeout);
                try
                {
                    await stderrTask.WaitAsync(cleanupDeadline.Token).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException)
                {
                    failure ??= exception is OperationCanceledException ? "A drenagem do stderr do worker não encerrou dentro do prazo de cleanup de 2000 ms." : null;
                }
            }
            cleanupDeadline?.Dispose();
        }
        return failure;
    }

    private async Task DrainStderrAsync()
    {
        var buffer = new byte[512];
        try
        {
            while (true)
            {
                int count = await process.StandardError.BaseStream.ReadAsync(buffer.AsMemory(), lifetime.Token).ConfigureAwait(false);
                if (count == 0)
                {
                    return;
                }
                lock (stderrLock)
                {
                    int remaining = MaximumStderrBytes - stderrCount;
                    if (remaining > 0)
                    {
                        int copied = Math.Min(count, remaining);
                        buffer.AsSpan(0, copied).CopyTo(stderr.AsSpan(stderrCount));
                        stderrCount += copied;
                    }
                }
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            // Diagnostics are optional and must never prevent bounded worker cleanup.
        }
    }

    private async Task<string> ReadBoundedLineAsync(CancellationToken cancellationToken)
    {
        var line = new byte[MaximumLineBytes];
        int length = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (readOffset == readCount)
            {
                readCount = await process.StandardOutput.BaseStream.ReadAsync(readBuffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                readOffset = 0;
                if (readCount == 0)
                {
                    string diagnostic;
                    lock (stderrLock)
                    {
                        diagnostic = Encoding.UTF8.GetString(stderr, 0, stderrCount).Trim();
                    }
                    throw new InvalidDataException("O worker privado encerrou a saída antes de uma resposta completa." +
                        (diagnostic.Length == 0 ? string.Empty : $" stderr: {diagnostic}"));
                }
            }
            byte value = readBuffer[readOffset++];
            if (value == (byte)'\n')
            {
                if (length > 0 && line[length - 1] == (byte)'\r')
                {
                    length--;
                }
                return ProtocolEncoding.GetString(line, 0, length);
            }
            Require(length < MaximumLineBytes, "linha excede 16 KiB");
            line[length++] = value;
        }
    }

    private static JsonDocument ParseEnvelope(string line)
    {
        JsonDocument document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 16 });
        try
        {
            JsonElement envelope = document.RootElement;
            Require(envelope.ValueKind == JsonValueKind.Object, "envelope não é um objeto");
            RejectDuplicateProperties(envelope);
            Require(RequiredInt64(envelope, "protocol_version") == 1, "versão de protocolo inválida");
            return document;
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    private static void RejectDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in value.EnumerateObject())
            {
                Require(names.Add(property.Name), "propriedade JSON duplicada");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in value.EnumerateArray())
            {
                RejectDuplicateProperties(item);
            }
        }
    }

    private static void ThrowWorkerError(JsonElement envelope, long expectedId)
    {
        Require(RequiredInt64(envelope, "request_id") == expectedId, "identificador de erro divergente");
        string status = RequiredString(envelope, "status");
        string message = RequiredString(envelope, "message");
        throw new InvalidOperationException($"Worker privado: {status}: {message}");
    }

    private void ValidateSample(JsonElement payload)
    {
        Require(RequiredInt64(payload, "schema_version") == 1, "schema da amostra inválido");
        Require(RequiredString(payload, "source_kind") == (operation == "thermal" ? PrivateThermalSample.SourceKind : PrivateVoltageSample.SourceKind),
            "origem da amostra divergente");
        Require(RequiredUInt32(payload, "gpu_index") == GpuIndex, "índice da GPU divergente");
        Require(RequiredString(payload, "gpu_uuid") == GpuUuid, "UUID da amostra divergente");
        Require(RequiredInt64(payload, "native_status") == 0, "amostra contém erro nativo");
        long captured = RequiredInt64(payload, "captured_at_unix_ms");
        Require(captured > 0 && DateTimeOffset.TryParse(RequiredString(payload, "captured_at_utc"), CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out DateTimeOffset timestamp) && timestamp.Offset == TimeSpan.Zero &&
            timestamp.ToUnixTimeMilliseconds() == captured, "timestamp da amostra inválido");
        Require(RequiredInt64(payload, "monotonic_ns") >= 0 && RequiredInt64(payload, "monotonic_frequency_hz") > 0,
            "relógio monotônico inválido");
        if (operation == "thermal")
        {
            RequiredFiniteDouble(payload, "gpu_die_temperature_c");
            RequiredFiniteDouble(payload, "gpu_hotspot_temperature_c");
            RequiredFiniteDouble(payload, "delta_c");
        }
        else
        {
            Require(RequiredInt64(payload, "gpu_core_voltage_microvolts") > 0, "tensão em microvolts inválida");
            Require(RequiredFiniteDouble(payload, "gpu_core_voltage_v") > 0, "tensão inválida");
        }
    }

    private static string RequiredString(JsonElement value, string name)
    {
        Require(value.TryGetProperty(name, out JsonElement field) && field.ValueKind == JsonValueKind.String,
            $"campo {name} ausente ou inválido");
        string result = field.GetString()!;
        Require(!string.IsNullOrWhiteSpace(result), $"campo {name} vazio");
        return result;
    }

    private static long RequiredInt64(JsonElement value, string name)
    {
        Require(value.TryGetProperty(name, out JsonElement field) && field.ValueKind == JsonValueKind.Number && field.TryGetInt64(out _),
            $"campo {name} ausente ou inválido");
        return field.GetInt64();
    }

    private static uint RequiredUInt32(JsonElement value, string name)
    {
        long result = RequiredInt64(value, name);
        Require(result >= 0 && result <= uint.MaxValue, $"campo {name} fora do intervalo");
        return (uint)result;
    }

    private static int RequiredPositiveInt32(JsonElement value, string name)
    {
        long result = RequiredInt64(value, name);
        Require(result > 0 && result <= int.MaxValue, $"campo {name} fora do intervalo");
        return (int)result;
    }

    private static double RequiredFiniteDouble(JsonElement value, string name)
    {
        Require(value.TryGetProperty(name, out JsonElement field) && field.ValueKind == JsonValueKind.Number &&
            field.TryGetDouble(out double number) && double.IsFinite(number), $"campo {name} ausente ou inválido");
        return field.GetDouble();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidDataException($"Protocolo do worker privado inválido: {message}.");
        }
    }
}
