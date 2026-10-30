namespace RtxMonitor.Service;

public static class Program
{
    public static async Task Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--private-profile-worker")
        {
            Environment.ExitCode = await ExperimentalProfileWorker.RunAsync(args).ConfigureAwait(false);
            return;
        }

        if (args.Length > 0 && args[0] == "--private-worker")
        {
            Environment.ExitCode = await ExperimentalWorkerProcess.RunAsync(args).ConfigureAwait(false);
            return;
        }

        WebApplication application = ServiceApplication.Build(args);
        await application.RunAsync().ConfigureAwait(false);
    }
}
