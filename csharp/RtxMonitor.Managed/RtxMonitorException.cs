namespace RtxMonitor.Managed;

public sealed class RtxMonitorException : Exception
{
    internal RtxMonitorException(NativeStatus status, string message)
        : base(message)
    {
        StatusCode = (int)status;
    }

    public int StatusCode { get; }
}
