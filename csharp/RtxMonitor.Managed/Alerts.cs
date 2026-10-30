namespace RtxMonitor.Managed;

public sealed record AlertOptions(int ThresholdC, int HysteresisC = 0);

// Pure state machine: turns a stream of die temperatures into AlertRaised /
// AlertCleared transitions. Holds no session, thread, or clock of its own,
// so it stays testable without a GPU and independent of ResilientSampler's
// reconnect/backoff policy.
public sealed class AlertEvaluator
{
    private readonly AlertOptions options;
    private bool alarmed;

    public AlertEvaluator(AlertOptions options)
    {
        if (options.HysteresisC < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "A histerese do alerta não pode ser negativa.");
        }
        if (options.HysteresisC > options.ThresholdC)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "A histerese do alerta não pode exceder o limiar.");
        }

        this.options = options;
    }

    public bool Alarmed => alarmed;

    public AlertOptions Options => options;

    public TelemetryEventKind? Observe(int temperatureC)
    {
        if (!alarmed && temperatureC >= options.ThresholdC)
        {
            alarmed = true;
            return TelemetryEventKind.AlertRaised;
        }

        int clearTemperature = options.ThresholdC - options.HysteresisC;
        bool droppedBelowThreshold = options.HysteresisC == 0
            ? temperatureC < clearTemperature
            : temperatureC <= clearTemperature;
        if (alarmed && droppedBelowThreshold)
        {
            alarmed = false;
            return TelemetryEventKind.AlertCleared;
        }

        return null;
    }
}
