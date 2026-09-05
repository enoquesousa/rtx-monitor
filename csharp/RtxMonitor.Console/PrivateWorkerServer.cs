using System.Globalization;
using System.Text.Json;
using RtxMonitor.Managed;

namespace RtxMonitor.ConsoleApp;

// Internal stdio transport. Only the existing fixed-profile operations are accepted.
internal static class PrivateWorkerServer
{
    internal static async Task<int> RunAsync(string[] args)
    {
        long requestId = 0;
        try
        {
            if (args.Length != 4 || args[1] is not ("thermal" or "voltage") ||
                args[2] is not ("--gpu" or "--gpu-uuid") ||
                !Console.IsInputRedirected || !Console.IsOutputRedirected)
            {
                throw new ArgumentException("O worker privado requer protocolo stdio e uma operação fixa.");
            }

            string operation = args[1];
            uint index = 0;
            if (args[2] == "--gpu" && !uint.TryParse(args[3], NumberStyles.None, CultureInfo.InvariantCulture, out index))
            {
                throw new ArgumentException("Índice da GPU inválido.");
            }
            using NvidiaMonitor monitor = NvidiaMonitor.Open();
            GpuInfo gpu = args[2] == "--gpu" ? monitor.GetGpu(index) : monitor.GetGpuByUuid(args[3]);
            PrivateProfileStatus profile = monitor.GetPrivateProfileStatus(gpu.Index);
            PrivateOperationState operationState = operation == "thermal" ? profile.ThermalState : profile.VoltageState;
            if (!profile.IsEligibleForAcquisition(operationState))
            {
                throw new InvalidOperationException($"Perfil experimental bloqueado: {PrivateProfileStatus.StateName(operationState)}.");
            }
            await WriteAsync(new
            {
                protocol_version = 1,
                kind = "ready",
                operation,
                gpu_uuid = gpu.Uuid,
                gpu_index = gpu.Index,
                minimum_interval_ms = operation == "thermal" ? profile.ThermalMinIntervalMilliseconds : profile.VoltageMinIntervalMilliseconds,
                acquisition_timeout_ms = operation == "thermal" ? profile.ThermalTimeoutMilliseconds : profile.VoltageTimeoutMilliseconds,
            }).ConfigureAwait(false);

            while (true)
            {
                string? command = await ReadCommandAsync(Console.In).ConfigureAwait(false);
                if (command is null or "stop")
                {
                    return 0;
                }
                long nextId = checked(requestId + 1);
                if (command != $"sample {nextId.ToString(CultureInfo.InvariantCulture)}")
                {
                    throw new InvalidDataException("Sequência de solicitação privada inválida.");
                }
                requestId = nextId;
                GpuInfo current = monitor.GetGpuByUuid(gpu.Uuid);
                JsonElement payload = operation == "thermal"
                    ? PrivateSampleJson.Thermal(current, monitor.ReadPrivateThermalChannels(current.Index))
                    : PrivateSampleJson.Voltage(current, monitor.ReadPrivateVoltageStatus(current.Index));
                await WriteAsync(new { protocol_version = 1, kind = "sample", request_id = requestId, payload }).ConfigureAwait(false);
            }
        }
        catch (Exception error)
        {
            string status = error is RtxMonitorException monitorError ? monitorError.Status.ToString() : "worker_error";
            await WriteAsync(new { protocol_version = 1, kind = "error", request_id = requestId, status, message = error.Message }).ConfigureAwait(false);
            return 1;
        }
    }

    private static async Task WriteAsync<T>(T value)
    {
        await Console.Out.WriteLineAsync(JsonSerializer.Serialize(value)).ConfigureAwait(false);
        await Console.Out.FlushAsync().ConfigureAwait(false);
    }

    private static async Task<string?> ReadCommandAsync(TextReader reader)
    {
        char[] buffer = new char[1];
        var command = new System.Text.StringBuilder();
        while (await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false) != 0)
        {
            if (buffer[0] == '\n')
            {
                return command.ToString().TrimEnd('\r');
            }
            if (command.Length >= 64)
            {
                throw new InvalidDataException("Solicitação privada excedeu o limite de tamanho.");
            }
            command.Append(buffer[0]);
        }
        return command.Length == 0 ? null : throw new InvalidDataException("Solicitação privada incompleta.");
    }
}
