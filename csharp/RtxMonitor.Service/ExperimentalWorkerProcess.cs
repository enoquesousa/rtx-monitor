using System.Diagnostics;
using System.Text.Json;
using RtxMonitor.ConsoleApp;
using RtxMonitor.Managed;

namespace RtxMonitor.Service;

// The process inherits the service token. This transport never requests elevation,
// installs a helper, loads an external profile, or accepts arbitrary private calls.
internal static class ExperimentalWorkerProcess
{
    internal static Task<int> RunAsync(string[] args) => PrivateWorkerServer.RunAsync(args);
}

internal sealed record ExperimentalProfileObservation(
    GpuInfo Gpu, BoardIdentity? Board, PrivateProfileStatus? Profile);

internal interface IExperimentalProfileSource
{
    Task<ExperimentalProfileObservation> CaptureAsync(string gpuUuid, CancellationToken cancellationToken);
}

internal sealed class NvidiaExperimentalProfileSource : IExperimentalProfileSource
{
    public Task<ExperimentalProfileObservation> CaptureAsync(string gpuUuid, CancellationToken cancellationToken) =>
        ExperimentalProfileProcess.CaptureAsync(gpuUuid, cancellationToken);
}

internal interface IExperimentalWorker : IAsyncDisposable
{
    string GpuUuid { get; }
    uint GpuIndex { get; }
    int MinimumIntervalMilliseconds { get; }
    int AcquisitionTimeoutMilliseconds { get; }
    Task<JsonElement> ReadSampleAsync(CancellationToken cancellationToken);
}

internal interface IExperimentalWorkerFactory
{
    Task<IExperimentalWorker> StartAsync(
        ExperimentalProfileObservation observation, string operation, CancellationToken cancellationToken);
}

internal sealed class SupervisedExperimentalWorkerFactory : IExperimentalWorkerFactory
{
    public async Task<IExperimentalWorker> StartAsync(
        ExperimentalProfileObservation observation, string operation, CancellationToken cancellationToken)
    {
        PrivateProfileStatus profile = observation.Profile
            ?? throw new InvalidOperationException("O worker requer diagnóstico de perfil.");
        uint nativeTimeout = operation == "thermal"
            ? profile.ThermalTimeoutMilliseconds : profile.VoltageTimeoutMilliseconds;
        string executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("Executável do serviço indisponível.");
        var info = new ProcessStartInfo(executable);
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            info.ArgumentList.Add(typeof(ExperimentalWorkerProcess).Assembly.Location);
        }
        info.ArgumentList.Add("--private-worker");
        string wireOperation = operation == "thermal" ? "thermal-raw" : operation;
        info.ArgumentList.Add(wireOperation);
        info.ArgumentList.Add("--gpu-uuid");
        info.ArgumentList.Add(observation.Gpu.Uuid);
        PrivateWorkerClient client = await PrivateWorkerClient.StartAsync(
            info, wireOperation, cancellationToken,
            startupTimeout: TimeSpan.FromSeconds(10),
            requestTimeout: TimeSpan.FromMilliseconds(checked(nativeTimeout + 1000)))
            .ConfigureAwait(false);
        return new SupervisedExperimentalWorker(client);
    }

    private sealed class SupervisedExperimentalWorker(PrivateWorkerClient client) : IExperimentalWorker
    {
        public string GpuUuid => client.GpuUuid;
        public uint GpuIndex => client.GpuIndex;
        public int MinimumIntervalMilliseconds => client.MinimumIntervalMilliseconds;
        public int AcquisitionTimeoutMilliseconds => client.AcquisitionTimeoutMilliseconds;

        public async Task<JsonElement> ReadSampleAsync(CancellationToken cancellationToken)
        {
            string payload = await client.ReadSampleAsync(cancellationToken).ConfigureAwait(false);
            using JsonDocument document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 16 });
            return document.RootElement.Clone();
        }

        public ValueTask DisposeAsync() => client.DisposeAsync();
    }
}
