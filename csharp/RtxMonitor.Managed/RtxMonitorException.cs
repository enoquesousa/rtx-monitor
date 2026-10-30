namespace RtxMonitor.Managed;

public sealed class RtxMonitorException : Exception
{
    public RtxMonitorException(MonitoringStatus status, string message)
        : base(message)
    {
        Status = status;
        StatusCode = (int)status;
    }

    internal RtxMonitorException(NativeStatus status, string message)
        : this((MonitoringStatus)(int)status, message)
    {
    }

    public MonitoringStatus Status { get; }

    public int StatusCode { get; }
}
