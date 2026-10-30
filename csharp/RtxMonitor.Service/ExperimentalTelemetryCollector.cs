using System.Text.Json;
using RtxMonitor.ConsoleApp;
using RtxMonitor.Managed;

namespace RtxMonitor.Service;

/// <summary>One opt-in collection session. Failed or invalidated operations cannot restart.</summary>
internal sealed class ExperimentalTelemetryCollector : IAsyncDisposable
{
    private readonly bool enabled;
    private readonly int intervalMilliseconds;
    private readonly IExperimentalProfileSource profiles;
    private readonly IExperimentalWorkerFactory workers;
    private readonly TimeProvider clock;
    private readonly Operation thermal = new("thermal");
    private readonly Operation voltage = new("voltage");
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim gate = new(1, 1);
    private ExperimentalProfileObservation? observation;
    private ExperimentalTelemetryState? sessionFailure;
    private bool disposed;

    internal ExperimentalTelemetryCollector(
        bool enabled, int intervalMilliseconds, IExperimentalProfileSource profiles,
        IExperimentalWorkerFactory workers, TimeProvider? clock = null)
    {
        if (intervalMilliseconds is < 100 or > 60000)
            throw new ArgumentOutOfRangeException(nameof(intervalMilliseconds));
        this.enabled = enabled;
        this.intervalMilliseconds = intervalMilliseconds;
        this.profiles = profiles;
        this.workers = workers;
        this.clock = clock ?? TimeProvider.System;
    }

    internal async Task<ExperimentalTelemetryReport> CollectAsync(GpuInfo expectedGpu, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        await gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!enabled)
                return Empty(expectedGpu, ExperimentalTelemetryState.Disabled);
            if (sessionFailure is ExperimentalTelemetryState failed)
                return Empty(expectedGpu, failed);

            try
            {
                linked.Token.ThrowIfCancellationRequested();
                observation = await profiles.CaptureAsync(expectedGpu.Uuid, linked.Token).ConfigureAwait(false);
                linked.Token.ThrowIfCancellationRequested();
                if (observation.Gpu != expectedGpu)
                {
                    await InvalidateCoreAsync(ExperimentalTelemetryState.IdentityMismatch).ConfigureAwait(false);
                    GpuInfo observedGpu = string.Equals(observation.Gpu.Uuid, expectedGpu.Uuid, StringComparison.OrdinalIgnoreCase)
                        ? observation.Gpu : expectedGpu;
                    return Empty(observedGpu, ExperimentalTelemetryState.IdentityMismatch);
                }
                ExperimentalTelemetryReport eligibility = ExperimentalTelemetryReport.Create(
                    observation.Gpu, observation.Board, observation.Profile, null, null, evaluatedAt: clock.GetUtcNow());
                ExperimentalTelemetryState thermalGate = eligibility.Fields[0].State;
                ExperimentalTelemetryState voltageGate = eligibility.Fields[1].State;
                // Finish bounded startup for both children before capturing either sensor. A slow
                // sibling handshake must not turn a healthy fresh sample into a stale payload.
                await Task.WhenAll(
                    PrepareOperationAsync(thermal, thermalGate, linked.Token),
                    PrepareOperationAsync(voltage, voltageGate, linked.Token)).ConfigureAwait(false);
                JsonElement?[] payloads = await Task.WhenAll(
                    ReadOperationAsync(thermal, linked.Token),
                    ReadOperationAsync(voltage, linked.Token)).ConfigureAwait(false);
                JsonElement? thermalPayload = payloads[0];
                JsonElement? voltagePayload = payloads[1];
                ExperimentalTelemetryReport report = ExperimentalTelemetryReport.Create(
                    observation.Gpu, observation.Board, observation.Profile, thermalPayload, voltagePayload,
                    thermal.Failure ?? thermalGate, voltage.Failure ?? voltageGate, clock.GetUtcNow());
                // Decoder rejection is also terminal; a rejected buffer is never used to find a later success.
                await LatchRejectedPayloadAsync(thermal, thermalPayload, report.Fields[0].State).ConfigureAwait(false);
                await LatchRejectedPayloadAsync(voltage, voltagePayload, report.Fields[1].State).ConfigureAwait(false);
                return report;
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                await InvalidateCoreAsync(ExperimentalTelemetryState.Cancelled).ConfigureAwait(false);
                throw;
            }
            catch (Exception error)
            {
                ExperimentalTelemetryState failure = FailureState(error);
                await InvalidateCoreAsync(failure).ConfigureAwait(false);
                return Empty(expectedGpu, failure);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task InvalidateAsync(ExperimentalTelemetryState failure)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try { await InvalidateCoreAsync(failure).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    private async Task PrepareOperationAsync(
        Operation operation, ExperimentalTelemetryState eligibility, CancellationToken cancellationToken)
    {
        if (eligibility != ExperimentalTelemetryState.NotRequested)
        {
            operation.Failure = eligibility;
            await StopAsync(operation).ConfigureAwait(false);
            return;
        }
        if (operation.Failure.HasValue) return;
        try
        {
            PrivateProfileStatus profile = observation!.Profile!;
            uint minimum = operation.Name == "thermal" ? profile.ThermalMinIntervalMilliseconds : profile.VoltageMinIntervalMilliseconds;
            uint timeout = operation.Name == "thermal" ? profile.ThermalTimeoutMilliseconds : profile.VoltageTimeoutMilliseconds;
            if (minimum is < 100 or > 60000 || timeout is 0 or > 5000)
                throw new InvalidDataException("Política compilada de prazo/intervalo inválida.");
            if (operation.Worker is null)
            {
                operation.Worker = await workers.StartAsync(observation, operation.Name, cancellationToken).ConfigureAwait(false);
                if (operation.Worker.GpuUuid != observation.Gpu.Uuid || operation.Worker.GpuIndex != observation.Gpu.Index ||
                    operation.Worker.MinimumIntervalMilliseconds != minimum || operation.Worker.AcquisitionTimeoutMilliseconds != timeout)
                    throw new InvalidDataException("Identidade ou política de aquisição divergiu no handshake.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            operation.Failure = FailureState(error);
            await StopAsync(operation).ConfigureAwait(false);
        }
    }

    private async Task<JsonElement?> ReadOperationAsync(Operation operation, CancellationToken cancellationToken)
    {
        if (operation.Failure.HasValue || operation.Worker is null) return null;
        try
        {
            if (operation.LastStartedAt.HasValue)
            {
                TimeSpan minimum = TimeSpan.FromMilliseconds(Math.Max(operation.Worker.MinimumIntervalMilliseconds, intervalMilliseconds));
                while (true)
                {
                    TimeSpan remaining = minimum - clock.GetElapsedTime(operation.LastStartedAt.Value);
                    if (remaining <= TimeSpan.Zero) break;
                    // A timer can wake before the monotonic deadline. Round up and
                    // recheck so every request honors the compiled minimum interval.
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Ceiling(remaining.TotalMilliseconds)), clock, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            operation.LastStartedAt = clock.GetTimestamp();
            return await operation.Worker.ReadSampleAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            operation.Failure = FailureState(error);
            await StopAsync(operation).ConfigureAwait(false);
            return null;
        }
    }

    private static async Task LatchRejectedPayloadAsync(Operation operation, JsonElement? payload, ExperimentalTelemetryState state)
    {
        if (payload.HasValue && state != ExperimentalTelemetryState.Available)
        {
            operation.Failure = state;
            await StopAsync(operation).ConfigureAwait(false);
        }
    }

    private ExperimentalTelemetryReport Empty(GpuInfo gpu, ExperimentalTelemetryState failure)
    {
        bool sameTarget = string.Equals(gpu.Uuid, observation?.Gpu.Uuid, StringComparison.OrdinalIgnoreCase);
        return ExperimentalTelemetryReport.Create(gpu,
            sameTarget ? observation?.Board : null, sameTarget ? observation?.Profile : null, null, null,
            failure, failure, clock.GetUtcNow()).WithFailure(failure, clock.GetUtcNow());
    }

    private async Task InvalidateCoreAsync(ExperimentalTelemetryState failure)
    {
        sessionFailure = failure;
        thermal.Failure = failure;
        voltage.Failure = failure;
        await StopAsync(thermal).ConfigureAwait(false);
        await StopAsync(voltage).ConfigureAwait(false);
    }

    private static async Task StopAsync(Operation operation)
    {
        if (operation.Worker is not IExperimentalWorker worker) return;
        operation.Worker = null;
        try { await worker.DisposeAsync().ConfigureAwait(false); }
        catch { operation.Failure = ExperimentalTelemetryState.WorkerUnavailable; }
    }

    private static ExperimentalTelemetryState FailureState(Exception error) => error switch
    {
        PrivateWorkerException worker => worker.Status switch
        {
            "Timeout" or "profile_timeout" => ExperimentalTelemetryState.Timeout,
            "RateLimited" or "profile_rate_limited" => ExperimentalTelemetryState.RateLimited,
            "GpuNotFound" or "GpuLost" or "profile_gpu_not_found" => ExperimentalTelemetryState.GpuNotFound,
            "profile_revoked" => ExperimentalTelemetryState.Revoked,
            "profile_identity_mismatch" => ExperimentalTelemetryState.IdentityMismatch,
            "profile_identity_unavailable" => ExperimentalTelemetryState.IdentityUnavailable,
            "profile_identity_ambiguous" => ExperimentalTelemetryState.IdentityAmbiguous,
            "profile_module_unavailable" => ExperimentalTelemetryState.ModuleUnavailable,
            "profile_unknown" => ExperimentalTelemetryState.ProfileUnknown,
            "profile_query_failed" => ExperimentalTelemetryState.QueryFailed,
            _ => ExperimentalTelemetryState.WorkerUnavailable,
        },
        TimeoutException => ExperimentalTelemetryState.Timeout,
        JsonException or InvalidDataException => ExperimentalTelemetryState.PayloadInvalid,
        DllNotFoundException or EntryPointNotFoundException or BadImageFormatException => ExperimentalTelemetryState.ModuleUnavailable,
        RtxMonitorException native => native.Status switch
        {
            MonitoringStatus.Timeout => ExperimentalTelemetryState.Timeout,
            MonitoringStatus.RateLimited => ExperimentalTelemetryState.RateLimited,
            MonitoringStatus.GpuNotFound or MonitoringStatus.GpuLost => ExperimentalTelemetryState.GpuNotFound,
            MonitoringStatus.BackendNotFound or MonitoringStatus.BackendSymbolMissing or MonitoringStatus.DriverNotLoaded or MonitoringStatus.AbiMismatch => ExperimentalTelemetryState.ModuleUnavailable,
            _ => ExperimentalTelemetryState.QueryFailed,
        },
        _ => ExperimentalTelemetryState.WorkerUnavailable,
    };

    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed) return;
            disposed = true;
            await InvalidateCoreAsync(ExperimentalTelemetryState.Cancelled).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private sealed class Operation(string name)
    {
        internal string Name { get; } = name;
        internal IExperimentalWorker? Worker { get; set; }
        internal ExperimentalTelemetryState? Failure { get; set; }
        internal long? LastStartedAt { get; set; }
    }
}
