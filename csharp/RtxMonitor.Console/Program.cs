using System.Globalization;
using System.Text;
using System.Text.Json;
using RtxMonitor.Managed;

namespace RtxMonitor.ConsoleApp;

internal enum RunMode
{
    Watch,
    Once,
    List,
}

internal sealed record Options(
    RunMode Mode,
    uint GpuIndex,
    int IntervalMilliseconds,
    long Count,
    bool Json);

internal sealed class RunningStatistics
{
    private long sum;

    internal long Count { get; private set; }

    internal int Minimum { get; private set; } = int.MaxValue;

    internal int Maximum { get; private set; } = int.MinValue;

    internal double Average => Count == 0 ? 0 : (double)sum / Count;

    internal void Add(int value)
    {
        Count++;
        sum += value;
        Minimum = Math.Min(Minimum, value);
        Maximum = Math.Max(Maximum, value);
    }
}

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
            Options options = ParseOptions(args);
            using NvidiaMonitor monitor = NvidiaMonitor.Open();

            if (options.Mode == RunMode.List)
            {
                PrintGpuList(monitor.GetGpus(), options.Json);
                return 0;
            }

            GpuInfo gpu = monitor.GetGpu(options.GpuIndex);
            if (options.Mode == RunMode.Once)
            {
                TemperatureSample sample = monitor.ReadGpuDieTemperature(options.GpuIndex);
                PrintSample(gpu, sample, options.Json);
                return 0;
            }

            return await WatchAsync(monitor, gpu, options).ConfigureAwait(false);
        }
        catch (ArgumentException error)
        {
            Console.Error.WriteLine($"rtxmon-csharp: {error.Message}");
            return 2;
        }
        catch (DllNotFoundException error)
        {
            Console.Error.WriteLine(
                "rtxmon-csharp: rtxmon_native.dll não foi encontrada. Execute scripts/build.ps1 primeiro.");
            Console.Error.WriteLine(error.Message);
            return 1;
        }
        catch (BadImageFormatException error)
        {
            Console.Error.WriteLine(
                "rtxmon-csharp: a arquitetura da DLL nativa não corresponde ao processo .NET (use x64)." );
            Console.Error.WriteLine(error.Message);
            return 1;
        }
        catch (RtxMonitorException error)
        {
            Console.Error.WriteLine($"rtxmon-csharp: {error.Message}");
            return 1;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"rtxmon-csharp: {error.Message}");
            return 1;
        }
    }

    private static Options ParseOptions(string[] args)
    {
        RunMode mode = RunMode.Watch;
        uint gpuIndex = 0;
        int interval = 1000;
        long count = 0;
        bool json = false;

        for (int index = 0; index < args.Length; index++)
        {
            string argument = args[index];
            switch (argument)
            {
                case "--help":
                case "-h":
                    PrintHelp();
                    Environment.Exit(0);
                    break;
                case "--once":
                    mode = RunMode.Once;
                    break;
                case "--watch":
                    mode = RunMode.Watch;
                    break;
                case "--list":
                    mode = RunMode.List;
                    break;
                case "--json":
                    json = true;
                    break;
                case "--gpu":
                    gpuIndex = ParseUInt32(NextValue(args, ref index, argument), argument);
                    break;
                case "--interval":
                    interval = ParseInt32(NextValue(args, ref index, argument), argument);
                    break;
                case "--count":
                    count = ParseInt64(NextValue(args, ref index, argument), argument);
                    break;
                default:
                    throw new ArgumentException($"opção desconhecida: {argument}; use --help");
            }
        }

        if (interval is < 100 or > 60000)
        {
            throw new ArgumentException("--interval deve estar entre 100 e 60000 ms");
        }

        if (count < 0)
        {
            throw new ArgumentException("--count não pode ser negativo");
        }

        return new Options(mode, gpuIndex, interval, count, json);
    }

    private static string NextValue(string[] args, ref int index, string option)
    {
        if (++index >= args.Length)
        {
            throw new ArgumentException($"valor ausente para {option}");
        }

        return args[index];
    }

    private static uint ParseUInt32(string value, string option) =>
        uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out uint parsed)
            ? parsed
            : throw new ArgumentException($"valor inválido para {option}: {value}");

    private static int ParseInt32(string value, string option) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : throw new ArgumentException($"valor inválido para {option}: {value}");

    private static long ParseInt64(string value, string option) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long parsed)
            ? parsed
            : throw new ArgumentException($"valor inválido para {option}: {value}");

    private static void PrintHelp()
    {
        Console.WriteLine(
            """
            RtxMonitor.Console - monitor do sensor térmico do die NVIDIA

            Uso:
              dotnet RtxMonitor.Console.dll [--watch] [--gpu INDEX] [--interval MS]
              dotnet RtxMonitor.Console.dll --once [--gpu INDEX] [--json]
              dotnet RtxMonitor.Console.dll --list [--json]

            Opções:
              --watch         Monitor contínuo (padrão)
              --once          Lê uma amostra e encerra
              --list          Lista as GPUs NVIDIA
              --gpu INDEX     Índice da GPU, começando em zero
              --interval MS   Intervalo de 100 a 60000 ms (padrão: 1000)
              --count N       Encerra após N amostras; zero é ilimitado
              --json          JSON; em watch, uma amostra por linha
              --help          Mostra esta ajuda
            """);
    }

    private static void PrintGpuList(IReadOnlyList<GpuInfo> gpus, bool json)
    {
        if (json)
        {
            var payload = gpus.Select(gpu => new
            {
                index = gpu.Index,
                name = gpu.Name,
                uuid = gpu.Uuid,
                driver_version = gpu.DriverVersion,
                nvml_version = gpu.NvmlVersion,
            });
            Console.WriteLine(JsonSerializer.Serialize(payload));
            return;
        }

        foreach (GpuInfo gpu in gpus)
        {
            Console.WriteLine(
                $"[{gpu.Index}] {gpu.Name} | {gpu.Uuid} | driver {gpu.DriverVersion} | NVML {gpu.NvmlVersion}");
        }
    }

    private static void PrintSample(GpuInfo gpu, TemperatureSample sample, bool json)
    {
        if (json)
        {
            var payload = new
            {
                schema_version = 1,
                gpu_index = sample.GpuIndex,
                gpu_name = gpu.Name,
                gpu_uuid = gpu.Uuid,
                temperature_c = sample.TemperatureC,
                sensor = "gpu_die",
                backend = sample.BackendName,
                timestamp_unix_ms = sample.TimestampUnixMilliseconds,
            };
            Console.WriteLine(JsonSerializer.Serialize(payload));
            return;
        }

        Console.WriteLine(
            $"{sample.CapturedAt:O} | GPU {sample.GpuIndex} {gpu.Name} | die {sample.TemperatureC} °C | {sample.BackendName}");
    }

    private static async Task<int> WatchAsync(
        NvidiaMonitor monitor,
        GpuInfo gpu,
        Options options)
    {
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        bool dashboard = !options.Json && !Console.IsOutputRedirected;
        var statistics = new RunningStatistics();
        long samples = 0;

        if (dashboard)
        {
            Console.Write("\u001b[2J");
        }

        while (!cancellation.IsCancellationRequested && (options.Count == 0 || samples < options.Count))
        {
            TemperatureSample sample = monitor.ReadGpuDieTemperature(options.GpuIndex);
            statistics.Add(sample.TemperatureC);
            samples++;

            if (dashboard)
            {
                RenderDashboard(gpu, sample, statistics, options.IntervalMilliseconds);
            }
            else
            {
                PrintSample(gpu, sample, options.Json);
            }

            if (options.Count != 0 && samples >= options.Count)
            {
                break;
            }

            try
            {
                await Task.Delay(options.IntervalMilliseconds, cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        if (dashboard)
        {
            Console.WriteLine();
        }

        return 0;
    }

    private static void RenderDashboard(
        GpuInfo gpu,
        TemperatureSample sample,
        RunningStatistics statistics,
        int intervalMilliseconds)
    {
        int width = Math.Max(40, Console.WindowWidth - 1);

        string[] lines =
        [
            "RTX MONITOR — leitura térmica do die",
            string.Empty,
            $"GPU       [{gpu.Index}] {gpu.Name}",
            $"UUID      {gpu.Uuid}",
            $"Driver    {gpu.DriverVersion}    NVML {gpu.NvmlVersion}",
            string.Empty,
            $"DIE       {sample.TemperatureC,3} °C",
            $"SESSÃO    mín {statistics.Minimum,3} °C | média {statistics.Average,5:F1} °C | máx {statistics.Maximum,3} °C",
            $"AMOSTRA   {sample.CapturedAt:yyyy-MM-dd HH:mm:ss.fff zzz} | intervalo {intervalMilliseconds} ms",
            $"FONTE     {sample.BackendName}",
            string.Empty,
            "Valor inteiro reportado pelo driver; a aplicação não interpola nem estima a temperatura.",
            "Ctrl+C para encerrar.",
        ];

        Console.Write("\u001b[H");
        foreach (string line in lines)
        {
            string visible = line.Length > width ? line[..width] : line;
            Console.WriteLine(visible.PadRight(width));
        }
    }
}
