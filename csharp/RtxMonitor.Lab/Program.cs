namespace RtxMonitor.Lab;

internal static class Program
{
    private static int Main(string[] args) =>
        LabCli.Run(args, Console.Out, Console.Error);
}
