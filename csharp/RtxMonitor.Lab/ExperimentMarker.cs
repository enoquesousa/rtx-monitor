using System.Diagnostics;

namespace RtxMonitor.Lab;

public sealed record ExperimentMarker(
    int SchemaVersion,
    string ScenarioId,
    string Phase,
    long UtcUnixMs,
    long MonotonicNs,
    long MonotonicFrequencyHz,
    string? Note);

public static class ExperimentMarkers
{
    public const int SchemaVersion = 1;

    public static ExperimentMarker Create(string scenarioId, string phase, string? note)
    {
        long frequency = Stopwatch.Frequency;
        long timestamp = Stopwatch.GetTimestamp();
        long monotonicNs = checked(
            (long)(((Int128)timestamp * 1_000_000_000L) / frequency));

        return new ExperimentMarker(
            SchemaVersion,
            scenarioId,
            phase,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            monotonicNs,
            frequency,
            note);
    }
}
