using System.Globalization;

namespace RtxMonitor.Service;

public sealed record RtxMonitorServiceOptions(
    int Port,
    string DatabasePath,
    int IntervalMilliseconds,
    int BufferCapacity,
    int RetentionDays,
    TimeSpan DiscoveryInterval,
    TimeSpan DependencyRetryInterval,
    int SseClientQueueCapacity,
    int MaximumSseClients,
    TimeSpan SseHeartbeatInterval,
    int HistoryMaximumLimit,
    int? AlertThresholdC,
    int AlertHysteresisC)
{
    public const string SectionName = "RtxMonitor";

    public static RtxMonitorServiceOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        IConfigurationSection section = configuration.GetSection(SectionName);

        string configuredDatabasePath = section["DatabasePath"] ?? string.Empty;
        string databasePath = string.IsNullOrWhiteSpace(configuredDatabasePath)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "RtxMonitor",
                "telemetry.db")
            : Path.GetFullPath(configuredDatabasePath, AppContext.BaseDirectory);

        var options = new RtxMonitorServiceOptions(
            ReadInt32(section, "Port", 5136),
            Path.GetFullPath(databasePath),
            ReadInt32(section, "IntervalMilliseconds", 1000),
            ReadInt32(section, "BufferCapacity", 256),
            ReadInt32(section, "RetentionDays", 30),
            TimeSpan.FromSeconds(ReadInt32(section, "DiscoveryIntervalSeconds", 15)),
            TimeSpan.FromSeconds(ReadInt32(section, "DependencyRetrySeconds", 5)),
            ReadInt32(section, "SseClientQueueCapacity", 256),
            ReadInt32(section, "MaximumSseClients", 32),
            TimeSpan.FromSeconds(ReadInt32(section, "SseHeartbeatSeconds", 15)),
            ReadInt32(section, "HistoryMaximumLimit", 1000),
            ReadNullableInt32(section, "AlertThresholdC"),
            ReadInt32(section, "AlertHysteresisC", 0));

        options.Validate();
        return options;
    }

    public void Validate()
    {
        if (Port is < 1 or > 65535)
        {
            throw new InvalidOperationException("RtxMonitor:Port deve estar entre 1 e 65535.");
        }
        if (string.IsNullOrWhiteSpace(DatabasePath))
        {
            throw new InvalidOperationException("RtxMonitor:DatabasePath não pode estar vazio.");
        }
        if (IntervalMilliseconds is < 100 or > 60000)
        {
            throw new InvalidOperationException(
                "RtxMonitor:IntervalMilliseconds deve estar entre 100 e 60000.");
        }
        if (BufferCapacity is < 1 or > 65536)
        {
            throw new InvalidOperationException(
                "RtxMonitor:BufferCapacity deve estar entre 1 e 65536.");
        }
        if (RetentionDays is < 1 or > 3650)
        {
            throw new InvalidOperationException(
                "RtxMonitor:RetentionDays deve estar entre 1 e 3650.");
        }
        ValidateDuration(DiscoveryInterval, "DiscoveryIntervalSeconds", 1, 3600);
        ValidateDuration(DependencyRetryInterval, "DependencyRetrySeconds", 1, 300);
        ValidateDuration(SseHeartbeatInterval, "SseHeartbeatSeconds", 1, 300);
        if (SseClientQueueCapacity is < 1 or > 8192)
        {
            throw new InvalidOperationException(
                "RtxMonitor:SseClientQueueCapacity deve estar entre 1 e 8192.");
        }
        if (MaximumSseClients is < 1 or > 256)
        {
            throw new InvalidOperationException(
                "RtxMonitor:MaximumSseClients deve estar entre 1 e 256.");
        }
        if (HistoryMaximumLimit is < 1 or > 10000)
        {
            throw new InvalidOperationException(
                "RtxMonitor:HistoryMaximumLimit deve estar entre 1 e 10000.");
        }
        if (AlertThresholdC is < 0 or > 500)
        {
            throw new InvalidOperationException(
                "RtxMonitor:AlertThresholdC deve estar entre 0 e 500.");
        }
        if (AlertHysteresisC < 0 || AlertHysteresisC > (AlertThresholdC ?? 0))
        {
            throw new InvalidOperationException(
                "RtxMonitor:AlertHysteresisC deve estar entre 0 e o limiar.");
        }
    }

    private static int ReadInt32(IConfiguration section, string name, int defaultValue)
    {
        string? value = section[name];
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : throw new InvalidOperationException($"RtxMonitor:{name} possui valor inválido: {value}.");
    }

    private static int? ReadNullableInt32(IConfiguration section, string name)
    {
        string? value = section[name];
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : throw new InvalidOperationException($"RtxMonitor:{name} possui valor inválido: {value}.");
    }

    private static void ValidateDuration(
        TimeSpan value,
        string name,
        int minimumSeconds,
        int maximumSeconds)
    {
        if (value < TimeSpan.FromSeconds(minimumSeconds) ||
            value > TimeSpan.FromSeconds(maximumSeconds))
        {
            throw new InvalidOperationException(
                $"RtxMonitor:{name} deve estar entre {minimumSeconds} e {maximumSeconds}.");
        }
    }
}
