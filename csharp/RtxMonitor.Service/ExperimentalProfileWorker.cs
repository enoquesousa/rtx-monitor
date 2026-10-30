using RtxMonitor.Managed;

namespace RtxMonitor.Service;

internal static class ExperimentalProfileWorker
{
    internal static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args.Length != 3 || args[0] != "--private-profile-worker" || args[1] != "--gpu-uuid" ||
                !Console.IsInputRedirected || !Console.IsOutputRedirected)
                throw new ArgumentException("O diagnóstico requer protocolo stdio e seleção por UUID.");
            ExperimentalProfileProtocol.ValidateUuid(args[2]);
            ExperimentalProfileObservation observation;
            // Only this disposable child opens the experimental profile diagnostic.
            // Its parent supervises initialization, identity queries, native checks, and disposal.
            using (NvidiaMonitor monitor = NvidiaMonitor.Open())
            {
                GpuInfo gpu = monitor.GetGpuByUuid(args[2]);
                observation = new ExperimentalProfileObservation(gpu,
                    monitor.GetBoardIdentity(gpu.Index), monitor.GetPrivateProfileStatus(gpu.Index));
            }
            await Console.Out.WriteLineAsync(ExperimentalProfileProtocol.Serialize(observation)).ConfigureAwait(false);
            await Console.Out.FlushAsync().ConfigureAwait(false);
            return 0;
        }
        catch (Exception error)
        {
            await Console.Out.WriteLineAsync(ExperimentalProfileProtocol.SerializeError(error)).ConfigureAwait(false);
            await Console.Out.FlushAsync().ConfigureAwait(false);
            return 1;
        }
    }
}
