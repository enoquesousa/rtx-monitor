namespace RtxMonitor.Managed;

public sealed record PerformanceLimitReasonReport(
    ulong RawBitmask,
    IReadOnlyList<string> ActiveReasons,
    string PrimaryReason);

public static class PerformanceLimitReasons
{
    private static readonly (ulong Mask, string Name, string Primary)[] Known =
    [
        (1UL << 0, "gpu_idle", "idle"),
        (1UL << 1, "application_clocks", "application_clocks"),
        (1UL << 2, "software_power_cap", "power"),
        (1UL << 3, "hardware_slowdown", "hardware_slowdown"),
        (1UL << 4, "sync_boost", "sync_boost"),
        (1UL << 5, "software_thermal", "thermal"),
        (1UL << 6, "hardware_thermal", "thermal"),
        (1UL << 7, "hardware_power_brake", "power_brake"),
        (1UL << 8, "display_clock", "display_clock"),
    ];

    public static PerformanceLimitReasonReport? From(PublicTelemetryReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        PublicTelemetryValue? field = report.Find(
            PublicTelemetryField.ClockEventReasonsCurrent);
        if (field?.State != CapabilityState.Available || field.UnsignedValue is not ulong raw)
        {
            return null;
        }

        string[] active = Known
            .Where(reason => (raw & reason.Mask) != 0)
            .Select(reason => reason.Name)
            .ToArray();
        string primary = Known.FirstOrDefault(reason => (raw & reason.Mask) != 0).Primary
            ?? (raw == 0 ? "none" : "unknown");
        return new PerformanceLimitReasonReport(raw, active, primary);
    }
}
