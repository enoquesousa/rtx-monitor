using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;

namespace RtxMonitor.Service.Tests;

internal static partial class Program
{
    private static async Task TestLocalHttpBoundaryAsync()
    {
        using var temporary = new TemporaryWorkspace();
        await using WebApplication application = ServiceApplication.Build(
            ["--RtxMonitor:DatabasePath=" + temporary.DatabasePath]);
        int requestsAccepted = 0;
        application.Run(_ =>
        {
            requestsAccepted++;
            return Task.CompletedTask;
        });
        RequestDelegate pipeline = ((IApplicationBuilder)application).Build();
        var failed = new List<string>();
        foreach ((string name, Action<HttpContext> change) in RejectedLocalRequests())
        {
            DefaultHttpContext context = LocalRequest(application.Services);
            change(context);
            int before = requestsAccepted;
            await pipeline(context).ConfigureAwait(false);
            if (context.Response.StatusCode != 403 || requestsAccepted != before ||
                context.Response.Headers.CacheControl != "no-store")
            {
                failed.Add(name);
            }
        }

        foreach (string host in new[] { "127.0.0.1:5136", "localhost:5136", "LOCALHOST:5136", "[::1]:5136" })
        {
            DefaultHttpContext context = LocalRequest(application.Services);
            context.Request.Headers.Host = host;
            context.Request.Headers.Origin = "http://" + host.ToLowerInvariant();
            context.Request.Headers["Sec-Fetch-Site"] = "same-origin";
            int before = requestsAccepted;
            await pipeline(context).ConfigureAwait(false);
            if (context.Response.StatusCode != 200 || requestsAccepted != before + 1 ||
                context.Response.Headers.CacheControl != "no-store")
            {
                failed.Add("valid " + host);
            }
        }

        DefaultHttpContext mapped = LocalRequest(application.Services);
        mapped.Connection.RemoteIpAddress = IPAddress.Parse("::ffff:127.0.0.1");
        mapped.Connection.LocalIpAddress = IPAddress.Parse("::ffff:127.0.0.1");
        mapped.Request.Headers["Sec-Fetch-Site"] = "none";
        await pipeline(mapped).ConfigureAwait(false);
        Check(mapped.Response.StatusCode == 200, "peer loopback mapeado deve funcionar");
        Check(!File.Exists(temporary.DatabasePath), "guard não deve iniciar workers ou SQLite");
        Check(failed.Count == 0, "fronteira HTTP local falhou: " + string.Join(", ", failed));
    }

    private static IEnumerable<(string, Action<HttpContext>)> RejectedLocalRequests()
    {
        yield return ("missing peer", context => context.Connection.RemoteIpAddress = null);
        yield return ("remote peer", context => context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.10"));
        yield return ("missing local address", context => context.Connection.LocalIpAddress = null);
        yield return ("remote local address", context => context.Connection.LocalIpAddress = IPAddress.Parse("192.0.2.10"));
        yield return ("missing local port", context => context.Connection.LocalPort = 0);
        yield return ("unexpected HTTPS", context => context.Request.Scheme = "https");
        foreach (string host in new[]
        {
            "rebind.example:5136", "localhost.example:5136", "127.0.0.1.example:5136",
            "localhost", "localhost:5137", "localhost:05136", "localhost:5136:5136",
            "localhost:5136,localhost:5136", "user@localhost:5136", "localhost:5136/",
            "localhost:5136?token=synthetic", "localhost:5136#fragment", "localhost:5136\\",
            " localhost:5136", "localhost:5136\r\n", "", "127.1:5136", "2130706433:5136",
            "0177.0.0.1:5136", "[::1]suffix:5136", "[::1%1]:5136", "[::1:5136",
        })
        {
            yield return ("Host " + host, context => context.Request.Headers.Host = host);
        }
        yield return ("duplicate Host", context => context.Request.Headers.Host = new[] { "localhost:5136", "localhost:5136" });
        foreach (string origin in new[]
        {
            "http://rebind.example:5136", "http://localhost:5136", "https://127.0.0.1:5136",
            "http://127.0.0.1:5137", "http://127.0.0.1:5136/", "http://127.0.0.1:5136?token=synthetic",
            "http://127.0.0.1:5136#fragment", "null", "", "http://127.0.0.1:5136 http://127.0.0.1:5136",
        })
        {
            yield return ("Origin " + origin, context => context.Request.Headers.Origin = origin);
        }
        yield return ("duplicate Origin", context => context.Request.Headers.Origin = new[] { "http://127.0.0.1:5136", "http://127.0.0.1:5136" });
        foreach (string site in new[] { "cross-site", "same-site", "", "same-origin,none" })
        {
            yield return ("Sec-Fetch-Site " + site, context => context.Request.Headers["Sec-Fetch-Site"] = site);
        }
        yield return ("duplicate fetch metadata", context => context.Request.Headers["Sec-Fetch-Site"] = new[] { "same-origin", "none" });
        foreach (string header in new[] { "Forwarded", "X-Forwarded-For", "X-Forwarded-Host", "X-Forwarded-Proto", "X-Original-For" })
        {
            yield return (header, context => context.Request.Headers[header] = "127.0.0.1");
        }
    }

    private static DefaultHttpContext LocalRequest(IServiceProvider services)
    {
        var context = new DefaultHttpContext { RequestServices = services };
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.Connection.LocalIpAddress = IPAddress.Loopback;
        context.Connection.LocalPort = 5136;
        context.Request.Scheme = "http";
        context.Request.Method = "GET";
        context.Request.Path = "/api/v1/history";
        context.Request.Headers.Host = "127.0.0.1:5136";
        return context;
    }

    private static async Task TestLocalTransportConfigurationAsync()
    {
        using var temporary = new TemporaryWorkspace();
        var failed = new List<string>();
        foreach (string argument in new[]
        {
            "--Kestrel:Endpoints:Extra:Url=http://127.0.0.1:0",
            "--ForwardedHeaders_Enabled=true", "--ForwardedHeaders_Enabled=invalid-synthetic",
            "--PreferHostingUrls=true", "--PreferHostingUrls=invalid-synthetic",
        })
        {
            try
            {
                await using WebApplication unexpected = ServiceApplication.Build(
                    ["--RtxMonitor:DatabasePath=" + temporary.DatabasePath, argument]);
                failed.Add(argument.Split('=')[0]);
            }
            catch (InvalidOperationException error)
            {
                Check(!error.Message.Contains("invalid-synthetic", StringComparison.Ordinal),
                    "diagnóstico de configuração não deve ecoar o valor recusado");
            }
        }
        Check(!File.Exists(temporary.DatabasePath), "configuração recusada não deve abrir SQLite");
        Check(failed.Count == 0, "configuração insegura foi aceita: " + string.Join(", ", failed));
    }

    private static async Task TestLocalListenerIsolationAsync()
    {
        using var temporary = new TemporaryWorkspace();
        int port;
        using (var reservation = new TcpListener(IPAddress.Loopback, 0))
        {
            reservation.Start();
            port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        }

        await using WebApplication application = ServiceApplication.Build(
        [
            "--RtxMonitor:DatabasePath=" + temporary.DatabasePath,
            "--RtxMonitor:Port=" + port,
            "--urls=http://192.0.2.10:5136",
            "--ForwardedHeaders_Enabled=false",
            "--PreferHostingUrls=false",
        ]);
        application.Run(context => context.Response.WriteAsync("local test response"));
        RequestDelegate pipeline = ((IApplicationBuilder)application).Build();
        IServer server = application.Services.GetRequiredService<IServer>();
        // Start only Kestrel, never the GPU/Windows workers registered by the host.
        await server.StartAsync(new GuardTestApplication(application.Services, pipeline), CancellationToken.None)
            .ConfigureAwait(false);
        try
        {
            IServerAddressesFeature addresses = server.Features.Get<IServerAddressesFeature>()!;
            string expected = "http://127.0.0.1:" + port;
            Check(addresses.Addresses.SequenceEqual([expected]), "Kestrel deve possuir somente o endpoint fixo");
            using var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false };
            using var client = new HttpClient(handler) { BaseAddress = new Uri(expected), Timeout = TimeSpan.FromSeconds(5) };
            using HttpResponseMessage local = await client.GetAsync("/security-transport-test").ConfigureAwait(false);
            Check(local.StatusCode == HttpStatusCode.OK &&
                await local.Content.ReadAsStringAsync().ConfigureAwait(false) == "local test response",
                "requisição local sem Origin deve alcançar o pipeline");
            using var rebind = new HttpRequestMessage(HttpMethod.Get, "/security-transport-test");
            rebind.Headers.Host = "rebind.example:" + port;
            using HttpResponseMessage rejected = await client.SendAsync(rebind).ConfigureAwait(false);
            Check(rejected.StatusCode == HttpStatusCode.Forbidden &&
                rejected.Headers.CacheControl?.NoStore == true,
                "Host de rebinding real deve ser recusado sem cache");

            application.Configuration["Kestrel:Endpoints:Late:Url"] = "http://127.0.0.1:0";
            ((IConfigurationRoot)application.Configuration).Reload();
            await Task.Delay(250).ConfigureAwait(false);
            Check(addresses.Addresses.SequenceEqual([expected]), "reload não deve acrescentar listeners");
            using var forwarded = new HttpRequestMessage(HttpMethod.Get, "/security-transport-test");
            forwarded.Headers.TryAddWithoutValidation("X-Forwarded-For", "127.0.0.1");
            using HttpResponseMessage forwardedResponse = await client.SendAsync(forwarded).ConfigureAwait(false);
            Check(forwardedResponse.StatusCode == HttpStatusCode.Forbidden,
                "headers encaminhados não podem autorizar acesso após reload");
            Check(!File.Exists(temporary.DatabasePath), "teste de transporte não deve iniciar coleta ou storage");
        }
        finally
        {
            await server.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static async Task CheckBoundaryBeforeEndpointsAsync(
        HttpClient client,
        RecordingHistorySource history,
        TelemetryEventHub hub)
    {
        foreach (string path in new[] { "/health", "/api/v1/history", "/api/v1/events" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.TryAddWithoutValidation("Origin", "http://untrusted.example");
            using HttpResponseMessage response = await client.SendAsync(request).ConfigureAwait(false);
            Check(response.StatusCode == HttpStatusCode.Forbidden && response.Headers.CacheControl?.NoStore == true,
                "origem externa deve falhar antes de " + path);
        }
        Check(history.LastQuery is null && hub.ConnectedClients == 0,
            "requisições recusadas não podem consultar histórico nem ocupar vaga SSE");
    }

    private sealed class GuardTestApplication(
        IServiceProvider services,
        RequestDelegate pipeline) : IHttpApplication<DefaultHttpContext>
    {
        public DefaultHttpContext CreateContext(IFeatureCollection features) =>
            new(features) { RequestServices = services };

        public Task ProcessRequestAsync(DefaultHttpContext context) => pipeline(context);

        public void DisposeContext(DefaultHttpContext context, Exception? exception)
        {
        }
    }
}
