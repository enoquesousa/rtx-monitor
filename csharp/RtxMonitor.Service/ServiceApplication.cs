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
        ValidateLocalTransportConfiguration(builder.Configuration);
        // These host defaults must stay disabled even if appsettings is reloaded.
        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ForwardedHeaders_Enabled"] = "false",
                [WebHostDefaults.PreferHostingUrlsKey] = "false",
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
                // Prevent Kestrel:Endpoints from adding listeners, including on reload.
                server.Configure(new ConfigurationBuilder().Build(), reloadOnChange: false);
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
        builder.Services.AddSingleton<ExperimentalTelemetrySnapshotState>();
        builder.Services.AddSingleton<IExperimentalTelemetrySnapshotSource>(
            services => services.GetRequiredService<ExperimentalTelemetrySnapshotState>());
        builder.Services.AddSingleton<IExperimentalTelemetryPublicationSource>(
            services => services.GetRequiredService<ExperimentalTelemetrySnapshotState>());
        builder.Services.AddSingleton<IExperimentalProfileSource, NvidiaExperimentalProfileSource>();
        builder.Services.AddSingleton<IExperimentalWorkerFactory, SupervisedExperimentalWorkerFactory>();
        builder.Services.AddSingleton<ExperimentalTelemetryWorker>();
        builder.Services.AddSingleton<ResearchTelemetryState>();
        builder.Services.AddHostedService<ResearchTelemetryWorker>();
        builder.Services.AddSingleton<GpuMonitoringWorker>();
        builder.Services.AddSingleton<IHostedService>(
            services => services.GetRequiredService<GpuMonitoringWorker>());
        builder.Services.AddSingleton<IHostedService>(
            services => services.GetRequiredService<WindowsTelemetryWorker>());
        builder.Services.AddSingleton<IHostedService>(
            services => services.GetRequiredService<ExperimentalTelemetryWorker>());

        WebApplication application = builder.Build();
        application.Use(LoopbackRequestGuard.InvokeAsync);
        application.Use(
            async (context, next) =>
            {
                context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                context.Response.Headers["Referrer-Policy"] = "no-referrer";
                context.Response.Headers["Content-Security-Policy"] =
                    "default-src 'self'; script-src 'self'; style-src 'self'; " +
                    "img-src 'self' data:; connect-src 'self'; object-src 'none'; " +
                    "base-uri 'none'; frame-ancestors 'none'";
                await next(context).ConfigureAwait(false);
            });
        if (builder.Configuration.GetValue("RtxMonitor:DashboardEnabled", true))
        {
            application.UseDefaultFiles();
            application.UseStaticFiles(new StaticFileOptions
            {
                OnPrepareResponse = context =>
                    context.Context.Response.Headers.CacheControl = "no-store",
            });
        }
        application.MapGet(
            "/api",
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
                        "/api/v1/gpus/{gpu_uuid}/profile-telemetry",
                        "/api/v1/research-telemetry",
                        "/api/v1/snapshot?gpu_uuid={gpu_uuid}",
                        "/api/v1/events",
                        "/api/v1/history",
                    },
                }));
        ServiceEndpoints.Map(application);
        DashboardEndpoints.Map(application);
        application.MapGet("/api/v1/research-telemetry",
            (HttpContext context, ResearchTelemetryState state) =>
            {
                context.Response.Headers.CacheControl = "no-store";
                bool summary = string.Equals(context.Request.Query["view"], "summary", StringComparison.Ordinal);
                return Results.Json(state.GetSnapshot(summary));
            });
        return application;
    }

    private static void ValidateLocalTransportConfiguration(IConfiguration configuration)
    {
        if (configuration.GetSection("Kestrel:Endpoints").Exists())
        {
            throw new InvalidOperationException(
                "Kestrel:Endpoints não é suportado; configure somente RtxMonitor:Port.");
        }

        foreach (string key in new[] { "ForwardedHeaders_Enabled", WebHostDefaults.PreferHostingUrlsKey })
        {
            string? value = configuration[key];
            if (value is not null && !value.Equals("false", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "ForwardedHeaders_Enabled e PreferHostingUrls devem estar ausentes ou false.");
            }
        }
    }
}
