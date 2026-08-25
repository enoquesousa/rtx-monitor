namespace RtxMonitor.Service;

public static class Program
{
    public static async Task Main(string[] args)
    {
        WebApplication application = ServiceApplication.Build(args);
        await application.RunAsync().ConfigureAwait(false);
    }
}
