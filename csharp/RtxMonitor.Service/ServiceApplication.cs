using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace RtxMonitor.Service;

public static class ServiceApplication
{
    public const string WindowsServiceName = "RtxMonitorService";

    public static WebApplication Build(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var builder = WebApplication.CreateBuilder(
            new WebApplicationOptions
            {
                Args = args,
                ContentRootPath = AppContext.BaseDirectory,
            });
        RtxMonitorServiceOptions options =
            RtxMonitorServiceOptions.FromConfiguration(builder.Configuration);

        builder.Services.AddWindowsService(
            service => service.ServiceName = WindowsServiceName);
        builder.Services.ConfigureHttpJsonOptions(
            json =>
            {
                json.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
                json.SerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower;
            });
        builder.WebHost.ConfigureKestrel(
            server =>
            {
                server.AddServerHeader = false;
                server.Limits.MaxConcurrentConnections = options.MaximumSseClients + 32;
                server.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
                server.Listen(
                    IPAddress.Loopback,
                    options.Port,
                    listen => listen.Protocols = HttpProtocols.Http1);
            });

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<MonitoringState>();
        builder.Services.AddSingleton<IMonitoringSnapshotSource>(
            services => services.GetRequiredService<MonitoringState>());
        builder.Services.AddSingleton<TelemetryStoreProvider>();
        builder.Services.AddSingleton<IHistorySource>(
            services => services.GetRequiredService<TelemetryStoreProvider>());
        builder.Services.AddSingleton<TelemetryEventHub>();
        builder.Services.AddSingleton<IMonitoringBackend, NvidiaMonitoringBackend>();
        builder.Services.AddSingleton<WindowsTelemetryState>();
        builder.Services.AddSingleton<IWindowsTelemetrySnapshotSource>(
            services => services.GetRequiredService<WindowsTelemetryState>());
        builder.Services.AddSingleton<IWindowsGpuReader>(
            _ => new WindowsGpuReader(new DxgiAdapterSource(), new PdhGpuCounterSource()));
        builder.Services.AddSingleton<WindowsTelemetryWorker>();
        builder.Services.AddSingleton<GpuMonitoringWorker>();
        builder.Services.AddSingleton<IHostedService>(
            services => services.GetRequiredService<GpuMonitoringWorker>());
        builder.Services.AddSingleton<IHostedService>(
            services => services.GetRequiredService<WindowsTelemetryWorker>());

        WebApplication application = builder.Build();
        application.Use(
            async (context, next) =>
            {
                IPAddress? remoteAddress = context.Connection.RemoteIpAddress;
                if (remoteAddress is not null && !IPAddress.IsLoopback(remoteAddress))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }

                context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                await next(context).ConfigureAwait(false);
            });
        application.MapGet(
            "/",
            () => Results.Json(
                new
                {
                    service = "rtx-monitor",
                    api_version = 1,
                    version = typeof(ServiceApplication).Assembly
                        .GetName()
                        .Version?
                        .ToString(3) ?? "unknown",
                    endpoints = new[]
                    {
                        "/health",
                        "/api/v1/gpus",
                        "/api/v1/gpus/{gpu_uuid}/capabilities",
                        "/api/v1/gpus/{gpu_uuid}/windows-telemetry",
                        "/api/v1/events",
                        "/api/v1/history",
                    },
                }));
        ServiceEndpoints.Map(application);
        return application;
    }
}
